using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

internal static class PartMeshBuilder
{
    private const int CylinderSegments = 24;
    private const int SphereSegments = 24;
    private const int SphereRings = 16;
    private const float SphereRadius = 0.5f;
    private static readonly Vector3 UnitSize = new(1f, 1f, 1f);

    // A model part only reaches this when its asset is missing: the placeholder box keeps its own grey, while the
    // part's Color (the model's tint) stays untouched for when the asset is back.
    public static (Vertex[] Vertices, uint[] Indices) CreateMesh(Part part) =>
        CreateUnitMesh(part.ShapeType, part.Model is null ? part.Color : ModelReference.PlaceholderColor);

    public static (Vertex[] Vertices, uint[] Indices) CreateHighlightMesh(Part part, Vector3 color) =>
        CreateUnitMesh(part.ShapeType, color);

    private static (Vertex[] Vertices, uint[] Indices) CreateUnitMesh(ShapeType shapeType, Vector3 color) =>
        shapeType switch
        {
            ShapeType.Block => MeshFactory.CreateIndexedBox(UnitSize.X, UnitSize.Y, UnitSize.Z, color),
            ShapeType.Cylinder => MeshFactory.CreateIndexedCylinder(
                UnitSize.X, UnitSize.Y, UnitSize.Z, CylinderSegments, color),
            ShapeType.Wedge => MeshFactory.CreateIndexedWedge(UnitSize.X, UnitSize.Y, UnitSize.Z, color),
            ShapeType.Sphere => MeshFactory.CreateUvSphere(SphereRadius, SphereSegments, SphereRings, color),
            ShapeType.Pyramid => MeshFactory.CreateIndexedPyramid(UnitSize.X, UnitSize.Y, UnitSize.Z, color),
            // Controlled fallback for any unknown/out-of-range ShapeType (future or corrupted value):
            // render a box so a part is never invisible. Matches the load-time Block fallback in SceneFile.
            _ => MeshFactory.CreateIndexedBox(UnitSize.X, UnitSize.Y, UnitSize.Z, color)
        };
}
