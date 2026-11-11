using Wisko.Engine.Assets.Importing;
using Matrix4 = Wisko.Engine.Math.Matrix4;

namespace Wisko.Engine.Animation;

/// <summary>
/// Every node of an imported model (the file's scene graph) as one flat, immutable list in depth-first order, so a
/// parent always comes before its children. Bones are the nodes skinned meshes follow; any node can be animated (a
/// rigid part of a model moves with its node). A node's stable identifier is its index in this order (the same for every
/// load of the same file) plus its name. Shared by every instance of the asset; playback state lives elsewhere.
/// </summary>
public sealed class ModelSkeleton
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly Matrix4[] _restLocal;
    private readonly Matrix4[] _restGlobal;
    private readonly NodeTransform[] _restTransforms;
    private readonly bool[] _isBone;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    private ModelSkeleton(List<(string Name, int Parent, Matrix4 Local, bool DrawsMesh)> nodes)
    {
        var count = nodes.Count;
        _names = new string[count];
        _parents = new int[count];
        _restLocal = new Matrix4[count];
        _restGlobal = new Matrix4[count];
        _restTransforms = new NodeTransform[count];
        _isBone = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var (name, parent, local, drawsMesh) = nodes[i];
            _names[i] = name;
            _parents[i] = parent;
            _restLocal[i] = local;

            // The same multiplication order as ModelAsset's draw flattening, so a rest pose reproduces it exactly.
            _restGlobal[i] = parent < 0 ? Matrix4.Multiply(Matrix4.Identity, local) : Matrix4.Multiply(_restGlobal[parent], local);
            _restTransforms[i] = AnimationMath.Decompose(local);

            // Bones and keyframe tracks name their node (Assimp keeps no other link). When several nodes share a name, the
            // first one that draws no mesh wins, else the first: exporters often give a joint and the mesh node under or
            // beside it the same name, and the joint is the one that moves.
            if (_byName.TryGetValue(name, out var earlier))
            {
                DuplicateNameCount++;
                if (nodes[earlier].DrawsMesh && !drawsMesh)
                {
                    _byName[name] = i;
                }
            }
            else
            {
                _byName[name] = i;
            }
        }
    }

    /// <summary>The nodes of a decoded model, root first.</summary>
    public static ModelSkeleton FromNodes(ImportedNode root)
    {
        var nodes = new List<(string, int, Matrix4, bool)>();
        Walk(root, -1, nodes);
        return new ModelSkeleton(nodes);
    }

    public int NodeCount => _names.Length;

    /// <summary>Nodes that share a name with an earlier node (a name then finds the first one).</summary>
    public int DuplicateNameCount { get; }

    /// <summary>Nodes at least one valid skin follows.</summary>
    public int BoneCount { get; private set; }

    public string NameOf(int node) => _names[node];

    /// <summary>The parent's index, or -1 for the root.</summary>
    public int ParentOf(int node) => _parents[node];

    /// <summary>The node's own transform relative to its parent, as stored in the file (its rest pose).</summary>
    public Matrix4 RestLocal(int node) => _restLocal[node];

    /// <summary>The rest local transform split into translation, rotation and scale (what a missing channel falls back to).</summary>
    public NodeTransform RestTransform(int node) => _restTransforms[node];

    /// <summary>Model space (above the node hierarchy) of the node at rest.</summary>
    public Matrix4 RestGlobal(int node) => _restGlobal[node];

    public bool IsBone(int node) => _isBone[node];

    /// <summary>
    /// The node with exactly this name, or -1. Of several with the same name: the first in depth-first order that draws
    /// no mesh, else the first.
    /// </summary>
    public int IndexOf(string name) => _byName.TryGetValue(name, out var index) ? index : -1;

    internal int[] Parents => _parents;

    internal Matrix4[] RestLocals => _restLocal;

    internal Matrix4[] RestGlobals => _restGlobal;

    internal NodeTransform[] RestTransforms => _restTransforms;

    internal void MarkBone(int node)
    {
        if (!_isBone[node])
        {
            _isBone[node] = true;
            BoneCount++;
        }
    }

    private static void Walk(ImportedNode node, int parent, List<(string, int, Matrix4, bool)> nodes)
    {
        var index = nodes.Count;
        nodes.Add((node.Name, parent, node.LocalTransform, node.MeshIndices.Count > 0));
        foreach (var child in node.Children)
        {
            Walk(child, index, nodes);
        }
    }
}
