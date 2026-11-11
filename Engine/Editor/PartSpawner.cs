using Wisko.Engine.Assets;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

internal static class PartSpawner
{
    private const float SpawnDistance = 9f;

    // A model spawns far enough away to fit in view: its bounding sphere at this many radii (~55 degree FOV),
    // never closer than SpawnDistance and never beyond half the camera's far plane. Its scale is not changed.
    private const float ModelFitRadii = 2.2f;
    private static readonly Vector3 DefaultSize = Part.DefaultSize;
    private static readonly Vector3 DefaultColor = Part.DefaultColor;

    public static Part CreateInFrontOfCamera(Workspace workspace, EditorCamera camera, ShapeType shapeType)
    {
        var name = GenerateNextPartName(workspace);
        var position = ComputeSpawnPosition(camera, DefaultSize, SpawnDistance, workspace);

        return new Part(
            name,
            position,
            DefaultSize,
            DefaultColor,
            Vector3.Zero,
            anchored: true,
            shapeType: shapeType);
    }

    /// <summary>
    /// A model instance of <paramref name="asset"/> at its own scale, its bounds centre in front of the camera (the
    /// same rule as primitives: not below the baseplate), named <paramref name="baseName"/> made unique.
    /// </summary>
    public static Part CreateModelInFrontOfCamera(Workspace workspace, EditorCamera camera, ModelAsset asset, string baseName)
    {
        var model = new ModelReference(asset.AssetPath, asset);
        var size = model.BoundsSize;
        var distance = global::System.Math.Clamp(size.Length * 0.5f * ModelFitRadii, SpawnDistance, camera.FarPlane * 0.5f);
        var part = new Part(GenerateUniqueName(workspace, baseName), Vector3.Zero, Vector3.Zero, ModelReference.DefaultTint) { Model = model };
        ModelTransform.SetInstanceTransform(part, Vector3.Zero, Vector3.Zero, new Vector3(1f, 1f, 1f));
        part.Position = ComputeSpawnPosition(camera, part.Size, distance, workspace);
        return part;
    }

    /// <summary>
    /// Add Light: a small invisible, anchored, non-colliding Part (its material is fully see-through; the
    /// editor marks it with an icon) carrying a light of <paramref name="type"/>, in front of the camera and at least 3
    /// studs above the ground so it lights it. A spot light points down (its Rotation X is 90: lights shine along the
    /// part's forward, local -Z), a directional light slants down. Any other Part (a lamp) can carry a light too.
    /// </summary>
    public static Part CreateLightInFrontOfCamera(Workspace workspace, EditorCamera camera, Graphics.LightType type)
    {
        var size = new Vector3(1f, 1f, 1f);
        var color = new Vector3(1f, 0.95f, 0.85f);
        var spawn = ComputeSpawnPosition(camera, size, SpawnDistance, workspace);
        var position = workspace.HasBaseplate ? new Vector3(spawn.X, MathF.Max(spawn.Y, 3f), spawn.Z) : spawn;
        var name = type switch
        {
            Graphics.LightType.Spot => "Spot Light",
            Graphics.LightType.Directional => "Directional Light",
            _ => "Point Light"
        };
        var rotation = type switch
        {
            Graphics.LightType.Spot => new Vector3(90f, 0f, 0f),
            Graphics.LightType.Directional => new Vector3(50f, 30f, 0f),
            _ => Vector3.Zero
        };
        return new Part(GenerateUniqueName(workspace, name), position, size, color, rotation, anchored: true, shapeType: ShapeType.Sphere)
        {
            CanCollide = false,
            Material = Graphics.SurfaceMaterial.Default with
            {
                AlphaMode = Graphics.AlphaMode.Blend, Opacity = 0f, CastShadows = false, ReceiveShadows = false
            },
            Light = Graphics.PartLight.Create(type) with { Color = color }
        };
    }

    /// <summary><paramref name="baseName"/> if unused, else the base (without trailing digits) + the first free number.</summary>
    internal static string GenerateUniqueName(Workspace workspace, string baseName)
    {
        if (!ContainsPartNamed(workspace, baseName))
        {
            return baseName;
        }

        var stem = baseName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (stem.Length == 0)
        {
            stem = baseName;
        }

        var index = 2;
        while (ContainsPartNamed(workspace, $"{stem}{index}"))
        {
            index++;
        }

        return $"{stem}{index}";
    }

    internal static string GenerateNextPartName(Workspace workspace)
    {
        var index = 1;
        while (ContainsPartNamed(workspace, $"Part{index}"))
        {
            index++;
        }

        return $"Part{index}";
    }

    private static bool ContainsPartNamed(Workspace workspace, string name)
    {
        foreach (var part in workspace.Parts)
        {
            if (part.Name == name)
            {
                return true;
            }
        }

        return false;
    }

    // In front of the camera; not below the baseplate while the world has one (without it, exactly where the camera looks).
    private static Vector3 ComputeSpawnPosition(EditorCamera camera, Vector3 size, float distance, Workspace workspace)
    {
        var halfHeight = size.Y * 0.5f;
        var position = camera.Position + camera.Forward * distance;

        if (workspace.HasBaseplate && position.Y < halfHeight)
        {
            position = new Vector3(position.X, halfHeight, position.Z);
        }

        return position;
    }
}
