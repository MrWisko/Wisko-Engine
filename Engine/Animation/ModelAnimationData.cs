using Wisko.Engine.Assets.Importing;
using Matrix4 = Wisko.Engine.Math.Matrix4;
using Vector3 = Wisko.Engine.Math.Vector3;

namespace Wisko.Engine.Animation;

/// <summary>
/// How a skinned mesh follows the skeleton once its bone names are resolved: palette slot -> node and inverse bind
/// matrix, plus the per-vertex weights (<see cref="ImportedSkin"/>). When some vertices have no weight, one extra slot
/// at the end follows the mesh's own node with an identity inverse bind (they move rigidly with it).
/// </summary>
public sealed class ModelSkin
{
    internal ModelSkin(int meshIndex, int[] paletteNodes, Matrix4[] inverseBind, ImportedSkin source)
    {
        MeshIndex = meshIndex;
        PaletteNodes = paletteNodes;
        InverseBind = inverseBind;
        Source = source;
    }

    public int MeshIndex { get; }

    /// <summary>Matrices the mesh needs per pose (its bones, plus the mesh-node slot when some vertices are unweighted).</summary>
    public int PaletteSize => PaletteNodes.Length;

    public ImportedSkin Source { get; }

    internal int[] PaletteNodes { get; }

    internal Matrix4[] InverseBind { get; }
}

/// <summary>Limits of Wisko's skinning, the same on every backend.</summary>
public static class SkinningLimits
{
    /// <summary>
    /// Bone matrices one skinned mesh may use: 256 x 64 bytes fill exactly the 16 KB uniform block OpenGL 3.3 guarantees
    /// every implementation supports (GL_MAX_UNIFORM_BLOCK_SIZE). A mesh needing more is drawn in its rest pose (reported).
    /// </summary>
    public const int MaxPaletteSize = 256;

    /// <summary>Bone influences per vertex (see <see cref="ImportedSkin.MaxInfluences"/>).</summary>
    public const int MaxInfluences = ImportedSkin.MaxInfluences;
}

/// <summary>
/// The animation side of one model asset: its skeleton (every node), the skins of its skinned meshes (null for rigid
/// meshes and for skins that could not be used), its clips, and what was wrong with the file's animation data (each
/// problem degrades only what it touches: a bad skin draws its mesh in the rest pose, a bad track is ignored). Built
/// once per asset from the decoded model; immutable; shared by every instance.
/// </summary>
public sealed class ModelAnimationData
{
    private readonly List<string> _notes = [];

    private ModelAnimationData(ImportedModel model)
    {
        Skeleton = ModelSkeleton.FromNodes(model.RootNode);
        var meshNodes = FirstNodeOfEachMesh(model);
        Skins = new ModelSkin?[model.Meshes.Count];
        for (var m = 0; m < model.Meshes.Count; m++)
        {
            if (model.Meshes[m].Skin is { } skin)
            {
                Skins[m] = ResolveSkin(model.Meshes[m], m, skin, meshNodes[m]);
            }
        }

        if (Skeleton.DuplicateNameCount > 0)
        {
            Note($"{Skeleton.DuplicateNameCount} node name(s) are used more than once; a bone or keyframe track with such a name follows the first of those nodes that draws no mesh (else the first).");
        }

        Clips = ResolveClips(model);
        RestPose = new ModelPose(this);
        RestPose.EvaluateRest();
    }

    public ModelSkeleton Skeleton { get; }

    /// <summary>Per mesh index: its skin, or null (a rigid mesh, or a skin that could not be used; see <see cref="Notes"/>).</summary>
    public ModelSkin?[] Skins { get; }

    /// <summary>Playable clips in file order.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>The model at rest: every node's rest transform, every skin's matrices for it.</summary>
    public ModelPose RestPose { get; }

    /// <summary>Problems found in the file's skeleton, skins and clips, in the order found (empty when there were none).</summary>
    public IReadOnlyList<string> Notes => _notes;

    public bool HasClips => Clips.Count > 0;

    public bool HasSkins => Skins.Any(skin => skin is not null);

    public static ModelAnimationData Build(ImportedModel model) => new(model);

    /// <summary>
    /// The clip a script or the editor names: an exact name first (the first clip with it, in file order), then the
    /// first whose name matches ignoring case. Null when there is none.
    /// </summary>
    public AnimationClip? FindClip(string name)
    {
        foreach (var clip in Clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.Ordinal))
            {
                return clip;
            }
        }

        foreach (var clip in Clips)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return clip;
            }
        }

        return null;
    }

    private void Note(string note) => _notes.Add(note);

    private ModelSkin? ResolveSkin(ImportedMesh mesh, int meshIndex, ImportedSkin skin, int meshNode)
    {
        var label = $"Mesh '{mesh.Name}'";
        var paletteSize = skin.BoneNames.Count + (skin.UnweightedVertices > 0 ? 1 : 0);
        if (paletteSize > SkinningLimits.MaxPaletteSize)
        {
            Note($"{label} uses {skin.BoneNames.Count} bones; Wisko skins at most {SkinningLimits.MaxPaletteSize} per mesh, so it is shown in its rest pose.");
            return null;
        }

        var nodes = new int[paletteSize];
        var inverseBind = new Matrix4[paletteSize];
        for (var slot = 0; slot < skin.BoneNames.Count; slot++)
        {
            var node = Skeleton.IndexOf(skin.BoneNames[slot]);
            if (node < 0)
            {
                Note($"{label}: bone '{skin.BoneNames[slot]}' is not a node of the model, so the mesh is shown in its rest pose.");
                return null;
            }

            var matrix = skin.InverseBindMatrices[slot];
            if (!AnimationMath.IsFinite(matrix) || !(MathF.Abs(AnimationMath.Determinant3x3(matrix)) > 1e-12f))
            {
                Note($"{label}: the bind pose of bone '{skin.BoneNames[slot]}' is not usable (its inverse bind matrix is not finite or not invertible), so the mesh is shown in its rest pose.");
                return null;
            }

            nodes[slot] = node;
            inverseBind[slot] = matrix;
        }

        if (skin.UnweightedVertices > 0)
        {
            if (meshNode < 0)
            {
                Note($"{label} has vertices without bone weights and no node of its own; it is shown in its rest pose.");
                return null;
            }

            nodes[^1] = meshNode;
            inverseBind[^1] = Matrix4.Identity;
            Note($"{label}: {skin.UnweightedVertices} vertex(es) have no bone weights; they move with the mesh's own node.");
        }

        if (skin.TruncatedVertices > 0)
        {
            Note($"{label}: {skin.TruncatedVertices} vertex(es) had more than {ImportedSkin.MaxInfluences} bone influences; the {ImportedSkin.MaxInfluences} strongest were kept and renormalized.");
        }

        if (skin.RenormalizedVertices > 0)
        {
            Note($"{label}: the bone weights of {skin.RenormalizedVertices} vertex(es) did not add up to 1; they were renormalized.");
        }

        if (skin.InvalidWeights > 0)
        {
            Note($"{label}: {skin.InvalidWeights} bone weight(s) were negative, not finite or named a vertex that does not exist; they were ignored.");
        }

        for (var slot = 0; slot < skin.BoneNames.Count; slot++)
        {
            Skeleton.MarkBone(nodes[slot]);
        }

        return new ModelSkin(meshIndex, nodes, inverseBind, skin);
    }

    private List<AnimationClip> ResolveClips(ImportedModel model)
    {
        var clips = new List<AnimationClip>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < model.Animations.Count; i++)
        {
            var animation = model.Animations[i];
            var name = animation.Name.Length > 0 ? animation.Name : $"Animation {i + 1}";
            var tracks = new List<NodeTrack>();
            var claimed = new HashSet<int>();
            var unknown = 0;
            var repeated = 0;
            foreach (var track in animation.Tracks)
            {
                if (track.Translation.Count == 0 && track.Rotation.Count == 0 && track.Scale.Count == 0)
                {
                    continue;
                }

                var node = Skeleton.IndexOf(track.NodeName);
                if (node < 0)
                {
                    unknown++;
                }
                else if (!claimed.Add(node))
                {
                    repeated++;
                }
                else
                {
                    tracks.Add(new NodeTrack(node, track.Translation, track.Rotation, track.Scale));
                }
            }

            if (unknown > 0)
            {
                Note($"Animation '{name}': {unknown} track(s) name a node the model does not have; they were ignored.");
            }

            if (repeated > 0)
            {
                Note($"Animation '{name}': {repeated} node(s) have more than one track; the first one is used.");
            }

            if (tracks.Count == 0)
            {
                Note($"Animation '{name}' has no keyframes Wisko can play; it shows the rest pose.");
            }

            if (!names.Add(name))
            {
                Note($"Two or more animations are called '{name}': by name the first one plays; use its number (GetAnimations) for the others.");
            }

            var duration = (float)animation.DurationSeconds;
            clips.Add(new AnimationClip(name, animation.Name, i, float.IsFinite(duration) && duration > 0f ? duration : 0f, tracks.ToArray(), Skeleton.NodeCount));
        }

        return clips;
    }

    // Each mesh's first referencing node in depth-first order (-1 when no node draws it): the node unweighted vertices follow.
    private static int[] FirstNodeOfEachMesh(ImportedModel model)
    {
        var result = new int[model.Meshes.Count];
        Array.Fill(result, -1);
        var index = 0;
        Visit(model.RootNode);
        return result;

        void Visit(ImportedNode node)
        {
            var current = index++;
            foreach (var mesh in node.MeshIndices)
            {
                if (result[mesh] < 0)
                {
                    result[mesh] = current;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child);
            }
        }
    }
}

/// <summary>
/// A model's pose: every node's model-space transform and every skin's bone matrices (bone node * inverse bind), also as
/// column-major floats ready to upload. Owned by one animator (or the asset's rest pose); its buffers are allocated once
/// and reused for every evaluation, so playing an animation allocates nothing per frame.
/// </summary>
public sealed class ModelPose
{
    private readonly ModelAnimationData _data;

    internal ModelPose(ModelAnimationData data)
    {
        _data = data;
        var nodes = data.Skeleton.NodeCount;
        Globals = new Matrix4[nodes];
        Locals = new NodeTransform[nodes];
        Palettes = new Matrix4[data.Skins.Length][];
        PaletteFloats = new float[data.Skins.Length][];
        for (var m = 0; m < data.Skins.Length; m++)
        {
            if (data.Skins[m] is { } skin)
            {
                Palettes[m] = new Matrix4[skin.PaletteSize];
                PaletteFloats[m] = new float[skin.PaletteSize * 16];
            }
            else
            {
                Palettes[m] = [];
                PaletteFloats[m] = [];
            }
        }
    }

    /// <summary>Evaluations so far: changes whenever the pose does (renderers that copy it compare this).</summary>
    public int Version { get; private set; }

    /// <summary>Model space of each node.</summary>
    internal Matrix4[] Globals { get; }

    /// <summary>Per mesh index: its bone matrices (empty for a mesh without a usable skin).</summary>
    internal Matrix4[][] Palettes { get; }

    /// <summary><see cref="Palettes"/> as 16 column-major floats per matrix.</summary>
    internal float[][] PaletteFloats { get; }

    // Scratch: the blended local transforms of the nodes being animated.
    internal NodeTransform[] Locals { get; }

    public Matrix4 GlobalOf(int node) => Globals[node];

    /// <summary>The bone matrices of the skin of a mesh (empty when it has none).</summary>
    public ReadOnlySpan<Matrix4> PaletteOf(int meshIndex) => Palettes[meshIndex];

    /// <summary>Copies another pose of the same model (e.g. the rest pose).</summary>
    internal void CopyFrom(ModelPose other)
    {
        other.Globals.CopyTo(Globals, 0);
        for (var m = 0; m < Palettes.Length; m++)
        {
            other.Palettes[m].CopyTo(Palettes[m], 0);
            other.PaletteFloats[m].CopyTo(PaletteFloats[m], 0);
        }

        Version++;
    }

    internal void EvaluateRest()
    {
        _data.Skeleton.RestGlobals.CopyTo(Globals, 0);
        UpdatePalettes();
    }

    /// <summary>
    /// Evaluates the blended layers (oldest first; each later layer blends over the result so far by its weight). A node
    /// no layer animates keeps its exact rest matrix; an animated one is composed from the blended translation, rotation and
    /// scale, with any channel a clip does not key at its rest value.
    /// </summary>
    internal void Evaluate(IReadOnlyList<AnimationLayer> layers)
    {
        var skeleton = _data.Skeleton;
        var parents = skeleton.Parents;
        var restLocals = skeleton.RestLocals;
        var restTransforms = skeleton.RestTransforms;
        for (var node = 0; node < parents.Length; node++)
        {
            var animated = false;
            for (var i = 0; i < layers.Count && !animated; i++)
            {
                animated = layers[i].Clip?.Animates(node) == true;
            }

            Matrix4 local;
            if (!animated)
            {
                local = restLocals[node];
            }
            else
            {
                var rest = restTransforms[node];
                var blended = SampleLayer(layers[0], node, rest);
                for (var i = 1; i < layers.Count; i++)
                {
                    var weight = layers[i].Weight;
                    if (weight <= 0f)
                    {
                        continue;
                    }

                    var sample = SampleLayer(layers[i], node, rest);
                    blended = weight >= 1f ? sample : AnimationMath.Blend(blended, sample, weight);
                }

                Locals[node] = blended;
                local = AnimationMath.Compose(blended);
            }

            var parent = parents[node];
            Globals[node] = parent < 0 ? Matrix4.Multiply(Matrix4.Identity, local) : Matrix4.Multiply(Globals[parent], local);
        }

        UpdatePalettes();
    }

    private static NodeTransform SampleLayer(AnimationLayer layer, int node, in NodeTransform rest) =>
        layer.Clip is { } clip ? clip.Sample(node, (float)layer.Time, rest) : rest;

    private void UpdatePalettes()
    {
        for (var m = 0; m < _data.Skins.Length; m++)
        {
            if (_data.Skins[m] is not { } skin)
            {
                continue;
            }

            var palette = Palettes[m];
            var floats = PaletteFloats[m];
            for (var slot = 0; slot < palette.Length; slot++)
            {
                palette[slot] = Matrix4.Multiply(Globals[skin.PaletteNodes[slot]], skin.InverseBind[slot]);
                AnimationMath.WriteColumnMajor(palette[slot], floats, slot * 16);
            }
        }

        Version++;
    }
}

/// <summary>
/// CPU linear blend skinning, the same formula the GPU path uses: each vertex is moved by the weighted sum of its bones'
/// matrices, and its normal by that matrix's inverse transpose. Used for the rest-pose geometry (bounds, picking), for
/// backends without a skinning shader (bgfx) and to check the GPU path.
/// </summary>
public static class SkinningCpu
{
    /// <summary>Skinned positions (and normals, when <paramref name="normalsOut"/> is given) in model space.</summary>
    public static void Skin(ImportedMesh mesh, ReadOnlySpan<Matrix4> palette, Span<Vector3> positionsOut, Span<Vector3> normalsOut)
    {
        var skin = mesh.Skin ?? throw new ArgumentException($"Mesh '{mesh.Name}' is not skinned.", nameof(mesh));
        var joints = skin.Joints;
        var weights = skin.Weights;
        const int max = ImportedSkin.MaxInfluences;
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            // Blended 3x4 matrix.
            float m11 = 0, m12 = 0, m13 = 0, m14 = 0, m21 = 0, m22 = 0, m23 = 0, m24 = 0, m31 = 0, m32 = 0, m33 = 0, m34 = 0;
            for (var i = 0; i < max; i++)
            {
                var w = weights[v * max + i];
                if (w == 0f)
                {
                    continue;
                }

                ref readonly var b = ref palette[joints[v * max + i]];
                m11 += b.M11 * w; m12 += b.M12 * w; m13 += b.M13 * w; m14 += b.M14 * w;
                m21 += b.M21 * w; m22 += b.M22 * w; m23 += b.M23 * w; m24 += b.M24 * w;
                m31 += b.M31 * w; m32 += b.M32 * w; m33 += b.M33 * w; m34 += b.M34 * w;
            }

            var p = mesh.Positions[v];
            positionsOut[v] = new Vector3(
                m11 * p.X + m12 * p.Y + m13 * p.Z + m14,
                m21 * p.X + m22 * p.Y + m23 * p.Z + m24,
                m31 * p.X + m32 * p.Y + m33 * p.Z + m34);
            if (normalsOut.IsEmpty)
            {
                continue;
            }

            // Cofactor matrix = det * inverse transpose; the normal is renormalized, so the det's scale does not matter
            // (its sign does: a mirrored blend flips the cofactor normal back the right way).
            var n = mesh.Normals[v];
            var c11 = m22 * m33 - m23 * m32;
            var c12 = m23 * m31 - m21 * m33;
            var c13 = m21 * m32 - m22 * m31;
            var c21 = m13 * m32 - m12 * m33;
            var c22 = m11 * m33 - m13 * m31;
            var c23 = m12 * m31 - m11 * m32;
            var c31 = m12 * m23 - m13 * m22;
            var c32 = m13 * m21 - m11 * m23;
            var c33 = m11 * m22 - m12 * m21;
            var det = m11 * c11 + m12 * c12 + m13 * c13;
            var nx = c11 * n.X + c12 * n.Y + c13 * n.Z;
            var ny = c21 * n.X + c22 * n.Y + c23 * n.Z;
            var nz = c31 * n.X + c32 * n.Y + c33 * n.Z;
            var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            normalsOut[v] = length > 1e-20f ? new Vector3(nx, ny, nz) * (MathF.Sign(det) / length) : Vector3.Zero;
        }
    }
}
