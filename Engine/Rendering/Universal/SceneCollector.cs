using Wisko.Engine.Assets;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.Physics;
using Wisko.Engine.World;
using NVector3 = System.Numerics.Vector3;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// What one frame draws: every visible mesh (<see cref="DrawItem"/>) with its world bounds, resolved material and
/// skinning palette, and the lights. Light rules (docs/graphics.md, "Lights"):
/// <list type="bullet">
/// <item>At most <see cref="MaxLights"/> lights per frame. With more, the ones that matter most to the camera are kept
/// (brightness / distance², directional lights first); ties keep scene order, so the choice is deterministic.</item>
/// <item>Each object is lit by at most the quality's lights-per-object: the strongest at its bounds, again
/// deterministic. A light only touches objects within its range (and, for a spot, near its cone).</item>
/// </list>
/// Collecting costs one pass over the Parts plus objects x lights bounds tests; no allocation once warm.
/// </summary>
internal sealed class SceneCollector
{
    public const int MaxLights = 64;

    public List<DrawItem> Items { get; } = [];

    public List<LightRecord> Lights { get; } = [];

    /// <summary>Per item: indices into <see cref="Lights"/> (ranges given by DrawItem.LightStart / LightCount).</summary>
    public List<int> LightIndices { get; } = [];

    private readonly List<LightRecord> _candidates = [];
    private readonly float[] _scores = new float[16];
    private readonly int[] _chosen = new int[16];

    public void Collect(
        IReadOnlyList<Part> parts,
        IReadOnlyList<SceneObject> environment,
        IReadOnlyCollection<Part> selected,
        MaterialResolver materials,
        UniversalMeshCache meshes,
        int maxLightsPerObject,
        Vector3 cameraPosition)
    {
        Items.Clear();
        LightIndices.Clear();
        CollectLights(parts, cameraPosition);

        foreach (var obj in environment)
        {
            if (meshes.Environment(obj) is { } mesh)
            {
                AddItem(mesh, obj.Model, null, null, materials.ForEnvironment(), selected: false, animated: false, new Vector3(1f, 1f, 1f));
            }
        }

        foreach (var part in parts)
        {
            if (!PartRenderer.IsDrawn(part))
            {
                continue;
            }

            var isSelected = selected.Count > 0 && selected.Contains(part);
            if (part.Model?.Asset is { } asset)
            {
                AddModel(part, asset, materials, meshes, isSelected);
                continue;
            }

            var shape = part.Model is not null ? ShapeType.Block : part.ShapeType;
            var material = part.Model is not null ? Placeholder(materials) : materials.ForPrimitive(part);
            AddItem(meshes.Primitive(shape), Matrix4.ComposeTrs(part.Position, part.Rotation, part.Size), part, null, material, isSelected,
                animated: false, part.Size);
        }

        AssignLights(maxLightsPerObject);
    }

    // A model whose asset is missing draws as a grey box (as in the Classic renderer).
    private static readonly Part PlaceholderPart = new("Placeholder", Vector3.Zero, Part.DefaultSize, ModelReference.PlaceholderColor);

    private static GpuMaterial Placeholder(MaterialResolver materials) => materials.ForPrimitive(PlaceholderPart);

    private void AddModel(Part part, ModelAsset asset, MaterialResolver materials, UniversalMeshCache meshes, bool isSelected)
    {
        var instance = ModelTransform.GetInstanceMatrix(part);
        var pose = part.Animator?.Pose;
        var animated = part.Animator is { IsPlaying: true };
        foreach (var draw in asset.Draws)
        {
            var material = materials.ForModel(part, asset, draw.MeshIndex);
            if (draw.Skinned)
            {
                var palette = (pose ?? asset.Animation.RestPose).PaletteFloats[draw.MeshIndex];
                var mesh = meshes.ModelMesh(asset, draw.MeshIndex, skinned: true);
                var rest = asset.RestBoundsOf(draw);
                // Bones move the mesh: its rest bounds grown by half their size on every side cover typical motion.
                var grow = rest.IsEmpty ? Vector3.Zero : rest.Size * 0.5f;
                AddItem(mesh, instance, part, palette, material, isSelected, animated || pose is not null, Vector3.Zero,
                    rest.IsEmpty ? null : (rest.Min - grow, rest.Max + grow));
                continue;
            }

            var node = pose is not null && draw.Node >= 0 ? pose.GlobalOf(draw.Node) : draw.Transform;
            AddItem(meshes.ModelMesh(asset, draw.MeshIndex, skinned: false), Matrix4.Multiply(instance, node), part, null, material, isSelected,
                animated, Vector3.Zero);
        }
    }

    private void AddItem(UniversalMesh mesh, Matrix4 model, Part? part, float[]? palette, GpuMaterial material, bool selected, bool animated,
        Vector3 objectSize, (Vector3 Min, Vector3 Max)? localBounds = null)
    {
        var (min, max) = localBounds ?? (mesh.BoundsMin, mesh.BoundsMax);
        var (worldMin, worldMax) = TransformBounds(model, min, max);
        var determinant = model.M11 * (model.M22 * model.M33 - model.M23 * model.M32)
            - model.M12 * (model.M21 * model.M33 - model.M23 * model.M31)
            + model.M13 * (model.M21 * model.M32 - model.M22 * model.M31);
        Items.Add(new DrawItem
        {
            Mesh = mesh,
            Model = model,
            Normal = Mat3.NormalMatrix(model),
            Mirrored = determinant < 0f,
            Part = part,
            Palette = palette,
            WorldMin = worldMin,
            WorldMax = worldMax,
            ObjectSize = objectSize,
            Material = material,
            Selected = selected,
            Animated = animated
        });
    }

    public static (Vector3 Min, Vector3 Max) TransformBounds(in Matrix4 m, Vector3 min, Vector3 max)
    {
        // Arvo's method: the world AABB of a transformed box.
        var center = (min + max) * 0.5f;
        var half = (max - min) * 0.5f;
        var c = new Vector3(
            m.M11 * center.X + m.M12 * center.Y + m.M13 * center.Z + m.M14,
            m.M21 * center.X + m.M22 * center.Y + m.M23 * center.Z + m.M24,
            m.M31 * center.X + m.M32 * center.Y + m.M33 * center.Z + m.M34);
        var e = new Vector3(
            MathF.Abs(m.M11) * half.X + MathF.Abs(m.M12) * half.Y + MathF.Abs(m.M13) * half.Z,
            MathF.Abs(m.M21) * half.X + MathF.Abs(m.M22) * half.Y + MathF.Abs(m.M23) * half.Z,
            MathF.Abs(m.M31) * half.X + MathF.Abs(m.M32) * half.Y + MathF.Abs(m.M33) * half.Z);
        return (c - e, c + e);
    }

    private void CollectLights(IReadOnlyList<Part> parts, Vector3 camera)
    {
        _candidates.Clear();
        foreach (var part in parts)
        {
            if (part.Light is not { Enabled: true } light || light.Intensity <= 0f)
            {
                continue;
            }

            var color = ColorSpace.SrgbToLinear(light.Color);
            var radiance = color * light.Intensity;
            var forward = PhysicsMath.ToWisko(NVector3.Transform(new NVector3(0f, 0f, -1f), PhysicsMath.EulerDegreesToQuaternion(part.Rotation)));
            var luminance = 0.2126f * radiance.X + 0.7152f * radiance.Y + 0.0722f * radiance.Z;
            var distance2 = (part.Position - camera).Length;
            distance2 *= distance2;
            _candidates.Add(new LightRecord
            {
                Type = light.Type,
                Position = part.Position,
                Direction = forward.Normalized(),
                Radiance = radiance,
                Range = light.Range,
                CosOuter = MathF.Cos(light.Angle * 0.5f * MathF.PI / 180f),
                CosInner = MathF.Cos(light.InnerAngle * 0.5f * MathF.PI / 180f),
                OuterAngleRadians = light.Angle * MathF.PI / 180f,
                WantsShadow = light.CastShadows && light.Type != LightType.Directional,
                ShadowSlot = -1,
                Part = part,
                Score = light.Type == LightType.Directional ? float.MaxValue : luminance / MathF.Max(distance2, 1f)
            });
        }

        Lights.Clear();
        if (_candidates.Count <= MaxLights)
        {
            Lights.AddRange(_candidates);
            return;
        }

        // Stable: equal scores keep scene order.
        var order = Enumerable.Range(0, _candidates.Count).OrderByDescending(i => _candidates[i].Score).ThenBy(i => i).Take(MaxLights).OrderBy(i => i);
        foreach (var i in order)
        {
            Lights.Add(_candidates[i]);
        }
    }

    private void AssignLights(int maxPerObject)
    {
        maxPerObject = global::System.Math.Clamp(maxPerObject, 1, 16);
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.LightStart = LightIndices.Count;
            var count = 0;
            if (item.Material.Mode != ShadingMode.Unlit && Lights.Count > 0)
            {
                count = ChooseLights(item, maxPerObject);
                for (var k = 0; k < count; k++)
                {
                    LightIndices.Add(_chosen[k]);
                }
            }

            item.LightCount = count;
            Items[i] = item;
        }
    }

    // The strongest lights at the item's bounds (insertion into a small sorted array; ties keep light order).
    private int ChooseLights(in DrawItem item, int max)
    {
        var count = 0;
        var center = (item.WorldMin + item.WorldMax) * 0.5f;
        var radius = (item.WorldMax - item.WorldMin).Length * 0.5f;
        for (var l = 0; l < Lights.Count; l++)
        {
            var light = Lights[l];
            float score;
            if (light.Type == LightType.Directional)
            {
                score = float.MaxValue;
            }
            else
            {
                var closest = new Vector3(
                    global::System.Math.Clamp(light.Position.X, item.WorldMin.X, item.WorldMax.X),
                    global::System.Math.Clamp(light.Position.Y, item.WorldMin.Y, item.WorldMax.Y),
                    global::System.Math.Clamp(light.Position.Z, item.WorldMin.Z, item.WorldMax.Z));
                var d = (closest - light.Position).Length;
                if (d >= light.Range)
                {
                    continue;
                }

                if (light.Type == LightType.Spot)
                {
                    // Outside the cone by more than the object's angular size: no light reaches it.
                    var toCenter = center - light.Position;
                    var distance = toCenter.Length;
                    if (distance > radius)
                    {
                        var cos = Vector3.Dot(toCenter / distance, light.Direction);
                        var angle = MathF.Acos(global::System.Math.Clamp(cos, -1f, 1f));
                        var spread = MathF.Asin(global::System.Math.Clamp(radius / distance, 0f, 1f));
                        if (angle - spread > light.OuterAngleRadians * 0.5f)
                        {
                            continue;
                        }
                    }
                }

                var lum = 0.2126f * light.Radiance.X + 0.7152f * light.Radiance.Y + 0.0722f * light.Radiance.Z;
                score = lum / MathF.Max(d * d, 0.25f);
            }

            // Insert keeping descending order (equal scores: the earlier light stays first).
            var position = count;
            while (position > 0 && _scores[position - 1] < score)
            {
                position--;
            }

            if (position >= max)
            {
                continue;
            }

            var last = global::System.Math.Min(count, max - 1);
            for (var k = last; k > position; k--)
            {
                _scores[k] = _scores[k - 1];
                _chosen[k] = _chosen[k - 1];
            }

            _scores[position] = score;
            _chosen[position] = l;
            count = global::System.Math.Min(count + 1, max);
        }

        return count;
    }
}
