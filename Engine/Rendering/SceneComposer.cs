using Wisko.Engine.Math;
using Wisko.Engine.Physics;

namespace Wisko.Engine.Rendering;

public sealed class SceneObject
{
    public required string Name { get; init; }
    public required ColoredMesh Mesh { get; init; }
    public Matrix4 Model { get; init; } = Matrix4.Identity;
    public Vector3 Position { get; init; } = Vector3.Zero;
    public Vector3 Scale { get; init; } = new(1f, 1f, 1f);

    /// <summary>
    /// The triangle vertices (non-indexed list) the mesh was built from, kept so the Universal renderer can
    /// light the environment (the baseplate) and let it receive shadows. Null for line meshes.
    /// </summary>
    public Vertex[]? SourceTriangles { get; init; }
}

public sealed class SceneComposer
{
    private readonly List<SceneObject> _objects = new();

    public IReadOnlyList<SceneObject> Objects => _objects;

    /// <summary>Colliders for the environment this scene draws (e.g. its baseplate), used by Play Mode's physics.</summary>
    public PhysicsEnvironment PhysicsEnvironment { get; } = new();

    public void AddLineMesh(string name, Vertex[] vertices, Matrix4 model)
    {
        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromVertices(name, vertices, MeshTopology.Lines),
            Model = model,
            Position = new Vector3(model.M14, model.M24, model.M34)
        });
    }

    public void AddLineLoop(string name, Vertex[] vertices, Matrix4 model)
    {
        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromVertices(name, vertices, MeshTopology.LineLoop),
            Model = model,
            Position = new Vector3(model.M14, model.M24, model.M34)
        });
    }

    public void AddSolidMesh(string name, Vertex[] vertices, Vector3 position, Vector3 scale)
    {
        var model = Matrix4.CreateTranslation(position);

        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromVertices(name, vertices),
            Model = model,
            Position = position,
            Scale = scale
        });
    }

    public void AddIndexedBox(string name, Vector3 position, float sizeX, float sizeY, float sizeZ, Vector3 color)
    {
        var (vertices, indices) = MeshFactory.CreateIndexedBox(sizeX, sizeY, sizeZ, color);
        var model = Matrix4.CreateTranslation(position);

        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromIndexedVertices(name, vertices, indices),
            Model = model,
            Position = position,
            Scale = new Vector3(sizeX, sizeY, sizeZ)
        });
    }

    public void AddIndexedMesh(string name, Vertex[] vertices, uint[] indices, Vector3 position, Vector3 scale)
    {
        var model = Matrix4.CreateTranslation(position);

        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromIndexedVertices(name, vertices, indices),
            Model = model,
            Position = position,
            Scale = scale
        });
    }

    public void AddStudioBaseplate(
        string name,
        Vector3 position,
        float sizeX,
        float sizeY,
        float sizeZ,
        Vector3 surfaceColor,
        Vector3 sideColor)
    {
        var solid = MeshFactory.CreateSolidBaseplateBox(sizeX, sizeY, sizeZ, surfaceColor, sideColor);
        var model = Matrix4.CreateTranslation(position);

        AddSceneObject(name, solid, model, position, new Vector3(sizeX, sizeY, sizeZ));

        // The solid box is centred on position: the collider is the same box, so what stands on it lines up exactly.
        PhysicsEnvironment.AddBox(name, position, new Vector3(sizeX, sizeY, sizeZ));
    }

    public void DisposeMeshes()
    {
        foreach (var obj in _objects)
        {
            obj.Mesh.Dispose();
        }

        _objects.Clear();
    }

    private void AddSceneObject(string name, Vertex[] vertices, Matrix4 model, Vector3 position, Vector3 scale)
    {
        _objects.Add(new SceneObject
        {
            Name = name,
            Mesh = ColoredMesh.FromVertices(name, vertices),
            Model = model,
            Position = position,
            Scale = scale,
            SourceTriangles = vertices
        });
    }
}
