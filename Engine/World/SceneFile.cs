using System.Text.Json;
using System.Text.Json.Serialization;
using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Math;
using Wisko.Engine.Platform;

namespace Wisko.Engine.World;

/// <summary>A scene file's contents: its Parts and the host environment objects the world deleted (empty: all present).</summary>
public sealed record SceneContents(List<Part> Parts, IReadOnlyList<string> RemovedEnvironment);

public static class SceneFile
{
    /// <summary>"Kind" of an imported model instance. Entries without a Kind are primitive parts (the original format).</summary>
    public const string ModelKind = "Model";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Writes the scene through <see cref="AtomicFile"/>: the old file stays intact until the new one is complete on
    /// the disk. False and the reason when the scene could not be serialized or written.
    /// </summary>
    public static bool TrySave(Workspace workspace, string path, out string? error)
    {
        string json;
        try
        {
            json = ToJson(workspace);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            error = $"The scene could not be serialized ({ex.Message})";
            return false;
        }

        return AtomicFile.TryWriteAllText(path, json, out error);
    }

    public static bool TryLoad(string path, out List<Part> parts, out string? error) =>
        TryLoad(path, null, out parts, out error);

    /// <param name="resolveModel">
    /// Returns the loaded asset for a model instance's project-relative AssetPath, or null when it is unavailable (the
    /// instance then loads as a placeholder that keeps its saved transform). Without a resolver every model instance
    /// loads as a placeholder.
    /// </param>
    public static bool TryLoad(string path, Func<string, ModelAsset?>? resolveModel, out List<Part> parts, out string? error)
    {
        var loaded = TryLoadScene(path, resolveModel, out var contents, out error);
        parts = contents.Parts;
        return loaded;
    }

    /// <summary>
    /// The scene: its Parts and the host environment objects (the baseplate) the world deleted. A scene saved as a plain
    /// list of Parts (every project before the baseplate could be deleted) has its whole environment.
    /// </summary>
    public static bool TryLoadScene(string path, Func<string, ModelAsset?>? resolveModel, out SceneContents contents, out string? error)
    {
        var parts = new List<Part>();
        var removed = new List<string>();
        contents = new SceneContents(parts, removed);
        error = null;

        if (!File.Exists(path))
        {
            error = "Scene file not found";
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            ScenePartDto[]? dtos;
            if (json.AsSpan().TrimStart().StartsWith("{"))
            {
                var document = JsonSerializer.Deserialize<SceneDocumentDto>(json, JsonOptions);
                dtos = document?.Parts;
                foreach (var (name, present) in document?.Environment ?? [])
                {
                    if (!present && !string.IsNullOrWhiteSpace(name))
                    {
                        removed.Add(name);
                    }
                }
            }
            else
            {
                dtos = JsonSerializer.Deserialize<ScenePartDto[]>(json, JsonOptions);
            }

            if (dtos is null || dtos.Length == 0)
            {
                return true;
            }

            // Groups are rebuilt from their members' paths: one group object per distinct path, parents first.
            var groups = new Dictionary<string, PartGroup>(StringComparer.Ordinal);
            foreach (var dto in dtos)
            {
                if (TryFromDto(dto, resolveModel, out var part))
                {
                    part.Group = GroupFromPath(dto.Group, groups);
                    parts.Add(part);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            parts.Clear();
            removed.Clear();
            return false;
        }
    }

    /// <summary>
    /// The scene file's text for this world (exactly what <see cref="TrySave(Workspace, string, out string?)"/> writes): the
    /// plain list of Parts while it has its whole environment, else <c>{ "Environment": { "Baseplate": false }, "Parts": [...] }</c>.
    /// </summary>
    public static string ToJson(Workspace workspace) =>
        workspace.RemovedEnvironment.Count == 0
            ? ToJson(workspace.Parts)
            : JsonSerializer.Serialize(new SceneDocumentDto
            {
                Environment = workspace.RemovedEnvironment.ToDictionary(name => name, _ => false, StringComparer.Ordinal),
                Parts = workspace.Parts.Select(ToDto).ToArray()
            }, JsonOptions);

    /// <summary>The scene file's text for these Parts (exactly what <see cref="TrySave(Workspace, string, out string?)"/> writes).</summary>
    public static string ToJson(IEnumerable<Part> parts) => JsonSerializer.Serialize(parts.Select(ToDto).ToArray(), JsonOptions);

    // "Outer/Inner" as group objects (shared by every Part with the same path); null for no path or an unusable one.
    private static PartGroup? GroupFromPath(string? path, Dictionary<string, PartGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        PartGroup? group = null;
        var prefix = string.Empty;
        foreach (var name in path.Split(PartGroup.PathSeparator))
        {
            if (!PartGroup.IsValidName(name))
            {
                Console.WriteLine($"[SceneFile] Group path \"{path}\" has an unusable name; the Part loads ungrouped");
                return null;
            }

            prefix = prefix.Length == 0 ? name : prefix + PartGroup.PathSeparator + name;
            if (!groups.TryGetValue(prefix, out var existing))
            {
                existing = new PartGroup(name, group);
                groups[prefix] = existing;
            }

            group = existing;
        }

        return group;
    }

    private static ScenePartDto ToDto(Part part)
    {
        var dto = ToDtoWithoutGroup(part);
        dto.Group = part.Group?.Path;
        return dto;
    }

    private static ScenePartDto ToDtoWithoutGroup(Part part)
    {
        if (part.Model is { } model)
        {
            // The instance transform, not the editor's bounds box: origin + rotation + scale (see ModelTransform).
            var origin = ModelTransform.GetOrigin(part);
            var scale = ModelTransform.GetScale(part);
            return new ScenePartDto
            {
                Name = part.Name,
                Kind = ModelKind,
                AssetPath = model.AssetPath,
                Position = [origin.X, origin.Y, origin.Z],
                Rotation = [part.Rotation.X, part.Rotation.Y, part.Rotation.Z],
                Scale = [scale.X, scale.Y, scale.Z],
                Color = ToColorBytes(part.Color),
                Anchored = part.Anchored,
                Locked = LockedToDto(part),
                Gravity = part.GravityEnabled ? null : false,
                CanCollide = part.CanCollide ? null : false,
                IsTrigger = part.IsTrigger ? true : null,
                Friction = FrictionToDto(part),
                Mass = part.MassOverride,
                Scripts = ScriptsToDto(part),
                Animation = AnimationToDto(part.Animation),
                Materials = Graphics.GraphicsDto.ToDto(part.ModelMaterials),
                Light = Graphics.GraphicsDto.ToDto(part.Light)
            };
        }

        return new ScenePartDto
        {
            Name = part.Name,
            ShapeType = part.ShapeType.ToString(),
            Position = [part.Position.X, part.Position.Y, part.Position.Z],
            Rotation = [part.Rotation.X, part.Rotation.Y, part.Rotation.Z],
            Size = [part.Size.X, part.Size.Y, part.Size.Z],
            Color = ToColorBytes(part.Color),
            Anchored = part.Anchored,
            Locked = LockedToDto(part),
            Gravity = part.GravityEnabled ? null : false,
            CanCollide = part.CanCollide ? null : false,
            IsTrigger = part.IsTrigger ? true : null,
            Friction = FrictionToDto(part),
            Mass = part.MassOverride,
            Scripts = ScriptsToDto(part),
            Material = Graphics.GraphicsDto.ToDto(part.Material),
            Light = Graphics.GraphicsDto.ToDto(part.Light)
        };
    }

    private static bool TryFromDto(ScenePartDto dto, Func<string, ModelAsset?>? resolveModel, out Part part)
    {
        part = null!;

        if (string.IsNullOrWhiteSpace(dto.Name)
            || dto.Position is not { Length: 3 }
            || dto.Rotation is not { Length: 3 })
        {
            return false;
        }

        if (string.Equals(dto.Kind, ModelKind, StringComparison.OrdinalIgnoreCase))
        {
            return TryModelFromDto(dto, resolveModel, out part);
        }

        if (dto.Kind is not null)
        {
            Console.WriteLine($"[SceneFile] Skipping '{dto.Name}': unknown Kind \"{dto.Kind}\"");
            return false;
        }

        if (dto.Size is not { Length: 3 } || dto.Color is not { Length: 3 })
        {
            return false;
        }

        if (!Enum.TryParse<ShapeType>(dto.ShapeType, ignoreCase: true, out var shapeType))
        {
            shapeType = ShapeType.Block;
        }

        part = new Part(
            dto.Name.Trim(),
            new Vector3(dto.Position[0], dto.Position[1], dto.Position[2]),
            new Vector3(dto.Size[0], dto.Size[1], dto.Size[2]),
            FromColorBytes(dto.Color),
            new Vector3(dto.Rotation[0], dto.Rotation[1], dto.Rotation[2]),
            dto.Anchored,
            shapeType)
        {
            Locked = dto.Locked == true
        };
        ReadPhysics(dto, part);
        ReadScripts(dto, part);
        part.Material = Graphics.GraphicsDto.FromDto(dto.Material);
        part.Light = Graphics.GraphicsDto.FromDto(dto.Light);
        return true;
    }

    private static bool TryModelFromDto(ScenePartDto dto, Func<string, ModelAsset?>? resolveModel, out Part part)
    {
        part = null!;

        // Only project-relative references inside Assets/ are accepted: a scene file never points elsewhere.
        if (dto.Scale is not { Length: 3 } || !ProjectAssetPaths.TryNormalize(dto.AssetPath, out var assetPath))
        {
            Console.WriteLine($"[SceneFile] Skipping model '{dto.Name}': missing Scale or invalid AssetPath \"{dto.AssetPath}\"");
            return false;
        }

        // Color is the instance tint; entries written before tints existed have none and load as white.
        var tint = dto.Color is { Length: 3 } ? FromColorBytes(dto.Color) : ModelReference.DefaultTint;
        var asset = resolveModel?.Invoke(assetPath);
        part = new Part(dto.Name.Trim(), Vector3.Zero, new Vector3(1f, 1f, 1f), tint, anchored: dto.Anchored)
        {
            Model = new ModelReference(assetPath, asset),
            Locked = dto.Locked == true
        };
        ReadPhysics(dto, part);
        ModelTransform.SetInstanceTransform(
            part,
            new Vector3(dto.Position[0], dto.Position[1], dto.Position[2]),
            new Vector3(dto.Rotation[0], dto.Rotation[1], dto.Rotation[2]),
            new Vector3(ValidScale(dto.Scale[0]), ValidScale(dto.Scale[1]), ValidScale(dto.Scale[2])));
        ReadScripts(dto, part);
        part.Animation = AnimationFromDto(dto.Animation);
        part.ModelMaterials = Graphics.GraphicsDto.FromDto(dto.Materials);
        part.Light = Graphics.GraphicsDto.FromDto(dto.Light);
        return true;
    }

    // A model's animation settings; omitted when they are the defaults (no clip, nothing on start, loop, speed 1), so
    // scenes without them keep their exact JSON. Each value is written only when it differs from its default.
    private static SceneAnimationDto? AnimationToDto(ModelAnimationSettings? settings) =>
        settings is null || settings.IsDefault
            ? null
            : new SceneAnimationDto
            {
                Clip = settings.Clip,
                PlayOnStart = settings.PlayOnStart ? true : null,
                Loop = settings.Loop ? null : false,
                Speed = settings.Speed.Equals(1f) ? null : settings.Speed
            };

    // The clip is kept by name even when the asset (or a reimport of it) no longer has it: Play then reports it.
    private static ModelAnimationSettings? AnimationFromDto(SceneAnimationDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var settings = new ModelAnimationSettings
        {
            Clip = string.IsNullOrWhiteSpace(dto.Clip) ? null : dto.Clip,
            PlayOnStart = dto.PlayOnStart == true,
            Loop = dto.Loop != false,
            Speed = dto.Speed ?? 1f
        };
        return settings.IsDefault ? null : settings;
    }

    // Written only for locked objects (older scenes and unlocked objects keep their exact original JSON).
    private static bool? LockedToDto(Part part) => part.Locked ? true : null;

    // Written only when it differs from the default, like the other physics settings.
    private static float? FrictionToDto(Part part) => part.Friction.Equals(Part.DefaultFriction) ? null : part.Friction;

    // Missing physics settings are the defaults (Gravity and Can Collide on, not a trigger, friction 0.6, Auto mass), so
    // older scenes behave as before. Part clamps a friction outside 0-1 and treats an unusable mass as Auto.
    private static void ReadPhysics(ScenePartDto dto, Part part)
    {
        part.GravityEnabled = dto.Gravity != false;
        part.CanCollide = dto.CanCollide != false;
        part.IsTrigger = dto.IsTrigger == true;
        part.Friction = dto.Friction ?? Part.DefaultFriction;
        part.MassOverride = dto.Mass;
    }

    // Omitted when empty, so objects without scripts keep their exact original JSON.
    private static string[]? ScriptsToDto(Part part) =>
        part.Scripts.Count == 0 ? null : part.Scripts.Select(script => script.AssetPath).ToArray();

    // Keeps valid references in order. Anything absolute, escaping the project, outside Assets/Scripts or not a .lua
    // file is skipped (logged): a bad reference never fails the scene load.
    private static void ReadScripts(ScenePartDto dto, Part part)
    {
        foreach (var path in dto.Scripts ?? [])
        {
            if (ScriptReference.TryCreate(path, out var reference))
            {
                part.Scripts.Add(reference);
            }
            else
            {
                Console.WriteLine($"[SceneFile] Skipping script reference \"{path}\" on '{part.Name}': must be a project-relative .lua file under Assets/Scripts");
            }
        }
    }

    private static float ValidScale(float value) => float.IsFinite(value) && value > 0f ? value : 1f;

    // The scene file's colour representation (primitive colours and model tints alike): 0-255 per channel.
    private static int[] ToColorBytes(Vector3 color) =>
    [
        (int)global::System.Math.Round(color.X * 255f),
        (int)global::System.Math.Round(color.Y * 255f),
        (int)global::System.Math.Round(color.Z * 255f)
    ];

    private static Vector3 FromColorBytes(int[] color) => new(color[0] / 255f, color[1] / 255f, color[2] / 255f);

    // A scene whose world deleted part of the host's environment: { "Environment": { name: false }, "Parts": [...] }.
    private sealed class SceneDocumentDto
    {
        public Dictionary<string, bool>? Environment { get; set; }

        public ScenePartDto[]? Parts { get; set; }
    }

    // Property order is the JSON order. Primitive parts leave Kind/AssetPath/Scale null (omitted), so their JSON is
    // exactly the original format; model instances leave ShapeType/Size null instead and use Color for their tint.
    // Locked and IsTrigger are omitted unless true; Gravity, CanCollide, Friction and Mass unless they differ from the
    // defaults (Mass is a manual mass; Auto is omitted); Scripts for objects without scripts.
    private sealed class ScenePartDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public string? AssetPath { get; set; }
        public string? ShapeType { get; set; }
        public float[] Position { get; set; } = [];
        public float[] Rotation { get; set; } = [];
        public float[]? Size { get; set; }
        public float[]? Scale { get; set; }
        public int[]? Color { get; set; }
        public bool Anchored { get; set; } = true;
        public bool? Locked { get; set; }
        public bool? Gravity { get; set; }
        public bool? CanCollide { get; set; }
        public bool? IsTrigger { get; set; }
        public float? Friction { get; set; }
        public float? Mass { get; set; }
        public string[]? Scripts { get; set; }

        // A model instance's animation settings (ModelAnimationSettings); absent when they are the defaults. Older builds
        // ignore it.
        public SceneAnimationDto? Animation { get; set; }

        // The World panel group path ("Outer/Inner"); absent when the Part is directly in the Workspace. Older builds
        // ignore it (the Part loads ungrouped).
        public string? Group { get; set; }

        // Materials and lights (absent when default; older builds ignore them): a primitive's surface material, a model
        // instance's overrides of its imported materials, and the light the object carries.
        public Graphics.SurfaceMaterialDto? Material { get; set; }
        public Graphics.ModelMaterialDto[]? Materials { get; set; }
        public Graphics.PartLightDto? Light { get; set; }
    }

    // "Animation": { "Clip": "Walk", "PlayOnStart": true, "Loop": false, "Speed": 1.5 } (each value only when not its default).
    private sealed class SceneAnimationDto
    {
        public string? Clip { get; set; }
        public bool? PlayOnStart { get; set; }
        public bool? Loop { get; set; }
        public float? Speed { get; set; }
    }
}
