using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Backend.Bgfx.Editor;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

// bgfx submission for PartRenderer. Same part/outline data and the same stencil technique as the OpenGL
// RenderParts / RenderSelectionOutlines, expressed as explicit per-draw bgfx state.
//
// Skinned meshes: bgfx's shaders are compiled offline (Metal binaries, see Shaders/README.md) and this build has no
// skinning shader, so a skinned mesh is skinned on the CPU (SkinningCpu, the formula the OpenGL shader uses) into a
// dynamic vertex buffer per model instance and mesh, refilled only when that instance's pose (or tint) changed, and drawn
// with the ordinary lit program; its triangles come from the asset's shared static mesh. Buffers of instances not drawn
// in a frame are freed after it.
public sealed partial class PartRenderer
{
    private readonly Dictionary<(Part Part, int Mesh), BgfxSkinnedInstance> _bgfxSkinned = new();

    /// <summary>
    /// Lit part surfaces. With <paramref name="writeSelectionStencil"/>, selected parts mark stencil = 1 from
    /// their OWN lit fragments (no separate mask mesh), exactly like the GL path, so the outline rim matches
    /// the lit surface with no speckles. Non-selected parts write no stencil.
    /// </summary>
    internal void SubmitPartsBgfx(BgfxEditorGraphics graphics, ushort view, bool writeSelectionStencil)
    {
        foreach (var entry in _entries)
        {
            // A Part a script hid while playing is not drawn (as on OpenGL).
            if (!IsDrawn(entry.Part))
            {
                continue;
            }

            EnsureMesh(entry);
            var stencil = writeSelectionStencil && _selectedParts.Contains(entry.Part)
                ? BgfxEditorGraphics.SelectionMaskStencil
                : 0u;
            if (entry.Part.Model?.Asset is { } asset)
            {
                foreach (var (mesh, model) in ModelSurfaceDraws(entry.Part, asset))
                {
                    graphics.SubmitLit(view, mesh, model, stencil);
                }

                if (asset.Animation.HasSkins)
                {
                    var instance = ModelTransform.GetInstanceMatrix(entry.Part);
                    foreach (var draw in asset.Draws)
                    {
                        if (draw.Skinned)
                        {
                            var skinned = SkinnedInstanceBgfx(graphics, entry.Part, asset, draw.MeshIndex);
                            graphics.SubmitLitDynamic(view, skinned.Surface!, _modelMeshes.GetSurface(asset, draw, entry.Part.Color), instance, stencil);
                        }
                    }
                }

                continue;
            }

            graphics.SubmitLit(view, entry.Mesh!, BuildModelMatrix(entry.Part), stencil);
        }

        _modelMeshes.ReleaseUnusedTints();
        ReleaseUnusedBgfxSkinning();
    }

    /// <summary>Light-blue rim: enlarged shells drawn only where stencil != 1, depth LEQUAL, no depth write.</summary>
    internal void SubmitOutlinesBgfx(BgfxEditorGraphics graphics, ushort view, EditorCamera camera)
    {
        foreach (var highlight in _highlightEntries)
        {
            var part = highlight.Part;
            if (!IsDrawn(part))
            {
                continue;
            }

            var thickness = OutlineThickness(part, camera);
            if (part.Model?.Asset is { } asset)
            {
                foreach (var (mesh, model) in ModelOutlineDraws(part, asset, thickness))
                {
                    graphics.SubmitUnlit(view, mesh, model, BgfxEditorGraphics.OutlineState, BgfxEditorGraphics.OutlineStencil);
                }

                if (asset.Animation.HasSkins)
                {
                    var instance = ModelTransform.GetInstanceMatrix(part);
                    foreach (var draw in asset.Draws)
                    {
                        if (draw.Skinned && _bgfxSkinned.TryGetValue((part, draw.MeshIndex), out var skinned))
                        {
                            var rim = skinned.OutlineBuffer(graphics, asset.Model.Meshes[draw.MeshIndex].VertexCount, OutlineColor);
                            graphics.SubmitUnlitDynamic(view, rim, _modelMeshes.GetSurface(asset, draw, part.Color),
                                EnlargeAboutBoundsCenter(instance, asset.RestBoundsOf(draw), thickness), BgfxEditorGraphics.OutlineState, BgfxEditorGraphics.OutlineStencil);
                        }
                    }
                }

                continue;
            }

            EnsureHighlightMesh(highlight);
            if (highlight.Mesh is null)
            {
                continue;
            }

            var enlargedModel = Matrix4.ComposeTrs(
                part.Position,
                part.Rotation,
                new Vector3(part.Size.X + 2f * thickness, part.Size.Y + 2f * thickness, part.Size.Z + 2f * thickness));
            graphics.SubmitUnlit(view, highlight.Mesh, enlargedModel, BgfxEditorGraphics.OutlineState, BgfxEditorGraphics.OutlineStencil);
        }
    }

    // The instance's CPU-skinned buffer for one mesh, refilled when its pose or tint changed since the last fill.
    private BgfxSkinnedInstance SkinnedInstanceBgfx(BgfxEditorGraphics graphics, Part part, ModelAsset asset, int meshIndex)
    {
        if (!_bgfxSkinned.TryGetValue((part, meshIndex), out var state))
        {
            state = new BgfxSkinnedInstance();
            _bgfxSkinned[(part, meshIndex)] = state;
        }

        state.Used = true;
        var mesh = asset.Model.Meshes[meshIndex];
        state.Surface ??= graphics.CreateDynamicMesh(mesh.VertexCount);
        var tintKey = ModelMeshCache.TintKeyOf(part.Color);
        if (state.Colored is null || state.TintKey != tintKey)
        {
            state.Colored = ModelMeshCache.BuildVertices(asset, meshIndex, part.Color);
            state.TintKey = tintKey;
            state.PoseVersion = -1;
        }

        var pose = PoseOf(part, asset);
        if (!ReferenceEquals(state.PoseSource, pose) || state.PoseVersion != pose.Version)
        {
            state.Positions ??= new Vector3[mesh.VertexCount];
            state.Normals ??= new Vector3[mesh.VertexCount];
            state.Packed ??= new float[mesh.VertexCount * 9];
            SkinningCpu.Skin(mesh, pose.PaletteOf(meshIndex), state.Positions, state.Normals);
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var o = v * 9;
                var color = state.Colored[v].Color;
                state.Packed[o] = state.Positions[v].X;
                state.Packed[o + 1] = state.Positions[v].Y;
                state.Packed[o + 2] = state.Positions[v].Z;
                state.Packed[o + 3] = color.X;
                state.Packed[o + 4] = color.Y;
                state.Packed[o + 5] = color.Z;
                state.Packed[o + 6] = state.Normals[v].X;
                state.Packed[o + 7] = state.Normals[v].Y;
                state.Packed[o + 8] = state.Normals[v].Z;
            }

            graphics.UpdateDynamicMesh(state.Surface, state.Packed);
            state.PoseSource = pose;
            state.PoseVersion = pose.Version;
            state.OutlineVersion = -1;
        }

        return state;
    }

    private void ReleaseUnusedBgfxSkinning()
    {
        List<(Part, int)>? unused = null;
        foreach (var (key, state) in _bgfxSkinned)
        {
            if (state.Used)
            {
                state.Used = false;
            }
            else
            {
                (unused ??= []).Add(key);
            }
        }

        foreach (var key in unused ?? [])
        {
            _bgfxSkinned[key].Dispose();
            _bgfxSkinned.Remove(key);
        }
    }

    private void DisposeBgfxSkinning()
    {
        foreach (var state in _bgfxSkinned.Values)
        {
            state.Dispose();
        }

        _bgfxSkinned.Clear();
    }

    private sealed class BgfxSkinnedInstance : IDisposable
    {
        private BgfxDynamicMesh? _outline;
        private float[]? _outlinePacked;

        public BgfxDynamicMesh? Surface { get; set; }

        public Vertex[]? Colored { get; set; }

        public uint TintKey { get; set; }

        public ModelPose? PoseSource { get; set; }

        public int PoseVersion { get; set; } = -1;

        public int OutlineVersion { get; set; } = -1;

        public Vector3[]? Positions { get; set; }

        public Vector3[]? Normals { get; set; }

        public float[]? Packed { get; set; }

        public bool Used { get; set; }

        // The selection rim: the same skinned positions in the outline colour (refilled when the pose changed).
        public BgfxDynamicMesh OutlineBuffer(BgfxEditorGraphics graphics, int vertexCount, Vector3 color)
        {
            _outline ??= graphics.CreateDynamicMesh(vertexCount);
            if (OutlineVersion != PoseVersion && Positions is not null)
            {
                _outlinePacked ??= new float[vertexCount * 9];
                for (var v = 0; v < vertexCount; v++)
                {
                    var o = v * 9;
                    _outlinePacked[o] = Positions[v].X;
                    _outlinePacked[o + 1] = Positions[v].Y;
                    _outlinePacked[o + 2] = Positions[v].Z;
                    _outlinePacked[o + 3] = color.X;
                    _outlinePacked[o + 4] = color.Y;
                    _outlinePacked[o + 5] = color.Z;
                }

                graphics.UpdateDynamicMesh(_outline, _outlinePacked);
                OutlineVersion = PoseVersion;
            }

            return _outline;
        }

        public void Dispose()
        {
            Surface?.Dispose();
            Surface = null;
            _outline?.Dispose();
            _outline = null;
        }
    }
}
