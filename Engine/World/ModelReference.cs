using Wisko.Engine.Assets;
using Wisko.Engine.Math;

namespace Wisko.Engine.World;

/// <summary>
/// The imported model a <see cref="Part"/> shows instead of a primitive shape. The part stays the one editor object
/// (selection, gizmos, history, World Explorer); this only names the project asset it draws. The model's internal
/// node hierarchy is never exposed as separate editor objects.
/// </summary>
public sealed class ModelReference
{
    /// <summary>
    /// A model part's <see cref="Part.Color"/> is a tint multiplied over its imported colours; white leaves them as
    /// imported (the default for every new or older instance).
    /// </summary>
    public static readonly Vector3 DefaultTint = new(1f, 1f, 1f);

    /// <summary>Colour of the box drawn while the asset is missing (independent of the instance's tint).</summary>
    public static readonly Vector3 PlaceholderColor = new(0.78f, 0.78f, 0.82f);

    public ModelReference(string assetPath, ModelAsset? asset)
    {
        AssetPath = assetPath;
        Asset = asset;
        if (asset is { Bounds.IsEmpty: false })
        {
            BoundsCenter = (asset.Bounds.Min + asset.Bounds.Max) * 0.5f;
            BoundsSize = asset.Bounds.Size;
        }
        else
        {
            BoundsCenter = Vector3.Zero;
            BoundsSize = new Vector3(1f, 1f, 1f);
        }
    }

    /// <summary>Project-relative asset reference, e.g. "Assets/Models/Tree/Tree.glb" (persisted as-is).</summary>
    public string AssetPath { get; }

    /// <summary>
    /// The loaded asset, or null when the file could not be loaded: the part then draws as a placeholder box, and its
    /// saved transform is written back unchanged (a unit box at the model origin stands in for the bounds).
    /// </summary>
    public ModelAsset? Asset { get; }

    /// <summary>Centre of the model-space box that the part's Position and Size describe.</summary>
    public Vector3 BoundsCenter { get; }

    /// <summary>Size of that box at scale 1.</summary>
    public Vector3 BoundsSize { get; }
}

/// <summary>
/// Adapter between the editor's box view of a model part and the model instance transform.
/// <para>Editor view (what the existing Move/Rotate/Resize gizmos, picking, snapping and history work on):
/// <see cref="Part.Position"/> = world centre of the model bounds, <see cref="Part.Size"/> = bounds size * scale,
/// <see cref="Part.Rotation"/>.</para>
/// <para>Instance transform (what Scene.json stores): origin, rotation, scale, with
/// world = T(origin) * R * S * node hierarchy * mesh.</para>
/// Resizing therefore turns into scale = size / original bounds size. An axis on which the bounds have no extent
/// (a flat model) keeps scale 1: its geometry is unaffected by scale there.
/// </summary>
public static class ModelTransform
{
    private const float MinBoundsExtent = 1e-6f;

    public static Vector3 GetScale(Part part)
    {
        var bounds = RequireModel(part).BoundsSize;
        return new Vector3(
            AxisScale(part.Size.X, bounds.X),
            AxisScale(part.Size.Y, bounds.Y),
            AxisScale(part.Size.Z, bounds.Z));
    }

    /// <summary>World position of the model's own origin.</summary>
    public static Vector3 GetOrigin(Part part)
    {
        var model = RequireModel(part);
        return part.Position - Rotate(part.Rotation, Multiply(GetScale(part), model.BoundsCenter));
    }

    /// <summary>T(origin) * R * S: maps model space (above the node hierarchy) to world space.</summary>
    public static Matrix4 GetInstanceMatrix(Part part) =>
        Matrix4.ComposeTrs(GetOrigin(part), part.Rotation, GetScale(part));

    /// <summary>Places a model part from an instance transform (scene load).</summary>
    public static void SetInstanceTransform(Part part, Vector3 origin, Vector3 rotation, Vector3 scale)
    {
        var model = RequireModel(part);
        part.Rotation = rotation;
        part.Size = Multiply(model.BoundsSize, scale);
        part.Position = origin + Rotate(rotation, Multiply(GetScale(part), model.BoundsCenter));
    }

    private static float AxisScale(float size, float boundsSize) =>
        boundsSize > MinBoundsExtent ? size / boundsSize : 1f;

    private static Vector3 Multiply(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    private static Vector3 Rotate(Vector3 rotationDegrees, Vector3 v)
    {
        var r = Matrix4.CreateRotationEuler(rotationDegrees);
        return new Vector3(
            r.M11 * v.X + r.M12 * v.Y + r.M13 * v.Z,
            r.M21 * v.X + r.M22 * v.Y + r.M23 * v.Z,
            r.M31 * v.X + r.M32 * v.Y + r.M33 * v.Z);
    }

    private static ModelReference RequireModel(Part part) =>
        part.Model ?? throw new InvalidOperationException($"Part '{part.Name}' is not a model instance.");
}
