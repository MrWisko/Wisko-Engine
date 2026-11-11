using Wisko.Engine.Assets;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>
/// The Universal renderer's GPU meshes. One mesh per primitive shape, shared by every Part of that shape (size,
/// colour and material are per-draw uniforms, so recolouring or restyling never rebuilds a mesh); one static and, for
/// skinned meshes, one skinned mesh per model asset mesh, shared by every instance (tint, materials and pose are per
/// draw); and the environment's meshes. Created on first use; released with <see cref="Clear"/> (project change,
/// shutdown) and, for assets no longer drawn, by <see cref="ReleaseUnused"/>.
/// </summary>
internal sealed class UniversalMeshCache : IDisposable
{
    private readonly Dictionary<ShapeType, UniversalMesh> _primitives = new();
    private readonly Dictionary<ModelAsset, ModelMeshes> _models = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SceneObject, UniversalMesh?> _environment = new(ReferenceEqualityComparer.Instance);

    public UniversalMesh Primitive(ShapeType shape)
    {
        if (!Enum.IsDefined(shape))
        {
            shape = ShapeType.Block;
        }

        if (!_primitives.TryGetValue(shape, out var mesh))
        {
            var (vertices, indices) = UniversalPrimitives.Create(shape);
            mesh = UniversalMesh.Create($"Primitive.{shape}", vertices, indices, skinned: false);
            _primitives[shape] = mesh;
        }

        return mesh;
    }

    /// <summary>A model mesh: rigid (node transform applies) or skinned (bone palette applies).</summary>
    public UniversalMesh ModelMesh(ModelAsset asset, int meshIndex, bool skinned)
    {
        if (!_models.TryGetValue(asset, out var meshes))
        {
            meshes = new ModelMeshes(asset.Model.Meshes.Count);
            _models[asset] = meshes;
        }

        meshes.Used = true;
        var slot = skinned ? meshes.Skinned : meshes.Static;
        return slot[meshIndex] ??= BuildModelMesh(asset, meshIndex, skinned);
    }

    /// <summary>The environment object's lit mesh (flat normals per triangle), or null for line meshes.</summary>
    public UniversalMesh? Environment(SceneObject obj)
    {
        if (_environment.TryGetValue(obj, out var mesh))
        {
            return mesh;
        }

        mesh = obj.SourceTriangles is { Length: >= 3 } triangles ? BuildEnvironmentMesh(obj.Name, triangles) : null;
        _environment[obj] = mesh;
        return mesh;
    }

    /// <summary>Releases the meshes of assets not drawn since the previous call.</summary>
    public void ReleaseUnused()
    {
        List<ModelAsset>? unused = null;
        foreach (var (asset, meshes) in _models)
        {
            if (meshes.Used)
            {
                meshes.Used = false;
            }
            else
            {
                (unused ??= []).Add(asset);
            }
        }

        foreach (var asset in unused ?? [])
        {
            _models[asset].Dispose();
            _models.Remove(asset);
        }
    }

    /// <summary>Releases environment meshes of scene objects that are gone (the scene was recomposed).</summary>
    public void RetainEnvironment(IReadOnlyList<SceneObject> current)
    {
        var keep = current.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var obj in _environment.Keys.Where(o => !keep.Contains(o)).ToList())
        {
            _environment[obj]?.Dispose();
            _environment.Remove(obj);
        }
    }

    public void Clear()
    {
        foreach (var mesh in _primitives.Values)
        {
            mesh.Dispose();
        }

        _primitives.Clear();
        foreach (var meshes in _models.Values)
        {
            meshes.Dispose();
        }

        _models.Clear();
        foreach (var mesh in _environment.Values)
        {
            mesh?.Dispose();
        }

        _environment.Clear();
    }

    public void Dispose() => Clear();

    private static UniversalMesh BuildModelMesh(ModelAsset asset, int meshIndex, bool skinned)
    {
        var mesh = asset.Model.Meshes[meshIndex];
        var uvs = mesh.Uv0?.Select(t => (t.U, t.V)).ToArray();
        var colors = mesh.Colors?.Select(c => (c.R, c.G, c.B, c.A)).ToArray();
        float[]? joints = null;
        float[]? weights = null;
        if (skinned && mesh.Skin is { } skin)
        {
            joints = skin.Joints.Select(j => (float)j).ToArray();
            weights = skin.Weights;
        }

        var data = MeshBuilder.Interleave(mesh.Positions, mesh.Normals, uvs, mesh.Tangents, colors, mesh.Indices, joints, weights);
        return UniversalMesh.Create($"{asset.AssetPath}#{meshIndex}{(skinned ? "_Skinned" : string.Empty)}", data, mesh.Indices, skinned && joints is not null);
    }

    private static UniversalMesh BuildEnvironmentMesh(string name, Vertex[] triangles)
    {
        var count = triangles.Length / 3 * 3;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new (float U, float V)[count];
        var colors = new (float R, float G, float B, float A)[count];
        var indices = new uint[count];
        for (var i = 0; i < count; i += 3)
        {
            var a = triangles[i].Position;
            var b = triangles[i + 1].Position;
            var c = triangles[i + 2].Position;
            var n = Vector3.Cross(b - a, c - a);
            n = n.Length > 1e-12f ? n.Normalized() : Vector3.UnitY;
            for (var k = 0; k < 3; k++)
            {
                var v = triangles[i + k];
                positions[i + k] = v.Position;
                normals[i + k] = v.Normal.Length > 1e-6f ? v.Normal.Normalized() : n;
                // A texture would tile every 8 studs, projected like the Studs mapping.
                var (u, w) = UniversalPrimitives.Project(v.Position / 8f, normals[i + k]);
                uvs[i + k] = (u, w);
                colors[i + k] = (v.Color.X, v.Color.Y, v.Color.Z, 1f);
                indices[i + k] = (uint)(i + k);
            }
        }

        var data = MeshBuilder.Interleave(positions, normals, uvs, null, colors, indices);
        return UniversalMesh.Create($"Environment.{name}", data, indices, skinned: false);
    }

    private sealed class ModelMeshes(int count) : IDisposable
    {
        public UniversalMesh?[] Static { get; } = new UniversalMesh?[count];

        public UniversalMesh?[] Skinned { get; } = new UniversalMesh?[count];

        public bool Used { get; set; } = true;

        public void Dispose()
        {
            foreach (var mesh in Static.Concat(Skinned))
            {
                mesh?.Dispose();
            }
        }
    }
}
