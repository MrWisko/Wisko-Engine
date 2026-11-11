using Wisko.Engine.Animation;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;

namespace Wisko.Engine.Assets;

/// <summary>
/// A project model asset loaded for the editor: the decoded <see cref="ImportedModel"/> plus its node hierarchy
/// flattened into <see cref="Draws"/> (one per mesh reference, with the accumulated node transform). The hierarchy
/// itself is kept untouched in <see cref="ImportedModel.RootNode"/>. Backend-independent: GPU meshes are created
/// from it by the renderer, and every scene instance of the asset shares this one object.
/// <para>Animation (<see cref="Animation"/>): the skeleton, skins and clips, shared too; each instance plays them with its
/// own <see cref="ModelAnimator"/>. A skinned mesh's draw has an identity transform: its vertices are moved into model
/// space by its bone matrices instead (at rest: <see cref="RestPositionsOf"/>).</para>
/// </summary>
public sealed class ModelAsset
{
    private readonly Vector3[]?[] _restSkinnedPositions;
    private readonly ImportedBounds[] _restSkinnedBounds;

    public ModelAsset(string assetPath, ImportedModel model)
    {
        AssetPath = assetPath;
        Model = model;
        Animation = ModelAnimationData.Build(model);

        var draws = new List<ModelDraw>();
        var nodeIndex = 0;
        Flatten(model.RootNode, Matrix4.Identity, draws, ref nodeIndex);
        Draws = draws;
        TriangleCount = draws.Sum(draw => model.Meshes[draw.MeshIndex].TriangleCount);

        // Skinned meshes at rest, in model space: what bounds, picking and the selection outline use.
        _restSkinnedPositions = new Vector3[]?[model.Meshes.Count];
        _restSkinnedBounds = new ImportedBounds[model.Meshes.Count];
        for (var m = 0; m < model.Meshes.Count; m++)
        {
            if (Animation.Skins[m] is null)
            {
                continue;
            }

            var mesh = model.Meshes[m];
            var positions = new Vector3[mesh.VertexCount];
            SkinningCpu.Skin(mesh, Animation.RestPose.PaletteOf(m), positions, []);
            var bounds = ImportedBounds.Empty;
            foreach (var p in positions)
            {
                bounds = bounds.Include(p);
            }

            _restSkinnedPositions[m] = positions;
            _restSkinnedBounds[m] = bounds;
        }

        Bounds = Animation.HasSkins ? DrawBounds() : model.Bounds;
    }

    /// <summary>Project-relative reference, e.g. "Assets/Models/Tree/Tree.glb".</summary>
    public string AssetPath { get; }

    public ImportedModel Model { get; }

    /// <summary>Skeleton, skins and clips (a static model has a skeleton of plain nodes and no clips).</summary>
    public ModelAnimationData Animation { get; }

    /// <summary>
    /// Model-space bounds of every drawn mesh (node transforms applied; skinned meshes in their rest pose). An animation
    /// moving the model does not change them.
    /// </summary>
    public ImportedBounds Bounds { get; }

    public IReadOnlyList<ModelDraw> Draws { get; }

    /// <summary>Triangles drawn per instance (a mesh referenced by two nodes counts twice).</summary>
    public int TriangleCount { get; }

    /// <summary>The vertex positions a draw uses in its own space: the mesh's, or for a skinned mesh its rest pose in model space.</summary>
    public Vector3[] RestPositionsOf(ModelDraw draw) => draw.Skinned ? _restSkinnedPositions[draw.MeshIndex]! : Model.Meshes[draw.MeshIndex].Positions;

    /// <summary>Bounds of <see cref="RestPositionsOf"/>.</summary>
    public ImportedBounds RestBoundsOf(ModelDraw draw) => draw.Skinned ? _restSkinnedBounds[draw.MeshIndex] : Model.Meshes[draw.MeshIndex].Bounds;

    private void Flatten(ImportedNode node, Matrix4 parent, List<ModelDraw> draws, ref int nodeIndex)
    {
        var index = nodeIndex++;
        var transform = Matrix4.Multiply(parent, node.LocalTransform);
        foreach (var meshIndex in node.MeshIndices)
        {
            // A skinned mesh ignores its node's transform (glTF's rule): its bones place it.
            var skinned = Animation.Skins[meshIndex] is not null;
            draws.Add(skinned
                ? new ModelDraw(meshIndex, Matrix4.Identity, false, index, true)
                : new ModelDraw(meshIndex, transform, Determinant3x3(transform) < 0f, index, false));
        }

        foreach (var child in node.Children)
        {
            Flatten(child, transform, draws, ref nodeIndex);
        }
    }

    private ImportedBounds DrawBounds()
    {
        var bounds = ImportedBounds.Empty;
        foreach (var draw in Draws)
        {
            foreach (var p in RestPositionsOf(draw))
            {
                bounds = bounds.Include(draw.Skinned ? p : AnimationMath.TransformPoint(draw.Transform, p));
            }
        }

        return bounds;
    }

    private static float Determinant3x3(Matrix4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
        - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
        + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
}

/// <summary>
/// One mesh drawn by one node. <see cref="Transform"/> maps mesh space to model space at rest (identity for a
/// <see cref="Skinned"/> mesh, whose bones place it). <see cref="Mirrored"/> marks a negative-determinant transform,
/// which flips triangle winding (front faces must then be drawn reversed). <see cref="Node"/> is the drawing node's index
/// in the asset's skeleton: an animation that moves that node moves the mesh with it.
/// </summary>
public readonly record struct ModelDraw(int MeshIndex, Matrix4 Transform, bool Mirrored, int Node = -1, bool Skinned = false);
