using Wisko.Engine.Animation;
using Wisko.Engine.Assets;
using Wisko.Engine.Assets.Importing;
using Wisko.Engine.Math;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

/// <summary>
/// Renders Workspace Parts with shape-aware meshes. A model part draws its asset's meshes through the imported node
/// hierarchy (instance transform * node transform * mesh) from <see cref="ModelMeshCache"/>, in the same lit pass and
/// with the same selection stencil and enlarged-shell outline as primitives.
/// </summary>
public sealed partial class PartRenderer : IDisposable
{
    // Selection outline (Stage 5): a light-blue outer silhouette drawn via a stencil mask, matching
    // the box-selection colour. The part keeps its exact real colour; only a rim is added.
    private static readonly Vector3 OutlineColor = new(0.55f, 0.78f, 1.0f);
    private const float OutlineThicknessFactor = 0.0025f; // world rim per unit of camera distance
    private const float MinOutlineThickness = 0.02f;
    private const float MaxOutlineThickness = 0.5f;

    private readonly List<PartDrawEntry> _entries = new();
    private readonly List<Part> _selectedParts = new();
    private readonly List<HighlightEntry> _highlightEntries = new();
    private readonly ModelMeshCache _modelMeshes = new();

    // OpenGL: the bone matrices of the skinned mesh being drawn (created on the first skinned draw).
    private readonly BoneUniformBuffer _bones = new();
    private bool _bonesReady;

    public void Sync(Workspace workspace)
    {
        Clear();

        foreach (var part in workspace.Parts)
        {
            _entries.Add(CreateEntry(part));
        }
    }

    public void RegisterPart(Part part)
    {
        _entries.Add(CreateEntry(part));
    }

    public bool UnregisterPart(Part part)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_entries[i].Part, part))
            {
                continue;
            }

            _entries[i].Mesh?.Dispose();
            _entries.RemoveAt(i);

            if (_selectedParts.Remove(part))
            {
                RebuildHighlights();
            }

            return true;
        }

        return false;
    }

    public void SetSelected(Part? part) => SetSelected(part is null ? Array.Empty<Part>() : [part]);

    public void SetSelected(IReadOnlyList<Part> parts)
    {
        _selectedParts.Clear();
        if (parts.Count > 0)
        {
            _selectedParts.AddRange(parts);
        }

        RebuildHighlights();
    }

    public void InvalidatePart(Part part)
    {
        foreach (var entry in _entries)
        {
            if (!ReferenceEquals(entry.Part, part))
            {
                continue;
            }

            RebuildEntryMesh(entry);
            break;
        }

        if (_selectedParts.Contains(part))
        {
            RebuildHighlights();
        }
    }

    // Normal part surfaces (lit shader). Selected parts also write the selection stencil (ref 1) from
    // their OWN fragments, so RenderOutlines can reject the enlarged outline exactly over the part.
    // Marking the mask from the part's own lit draw (instead of a separate mesh drawn with the unlit
    // shader) avoids the lit-vs-unlit gl_Position depth mismatch that otherwise left the mask incomplete
    // and bled blue outline speckles across the surface.
    // Skinned model meshes (second pass, skinned lit shader): the bone matrices of the instance's pose go to the bone
    // uniform block before each mesh, which the shared skinned mesh then reads.
    internal void RenderParts(ShaderProgram shader, ShaderProgram skinnedShader)
    {
        GL.Enable(EnableCap.StencilTest);
        GL.StencilFunc(StencilFunction.Always, 1, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);

        var anySkinned = false;
        foreach (var entry in _entries)
        {
            // A Part a script hid while playing is not drawn (its mesh stays, ready for when it shows again).
            if (!IsDrawn(entry.Part))
            {
                continue;
            }

            EnsureMesh(entry);
            // Only selected parts populate the selection mask; non-selected parts write no stencil bits.
            GL.StencilMask(_selectedParts.Contains(entry.Part) ? 0xFF : 0x00);
            if (entry.Part.Model?.Asset is { } asset)
            {
                foreach (var (mesh, model) in ModelSurfaceDraws(entry.Part, asset))
                {
                    shader.SetMatrix4("uModel", model);
                    mesh.Draw();
                }

                anySkinned |= asset.Animation.HasSkins;
                continue;
            }

            shader.SetMatrix4("uModel", BuildModelMatrix(entry.Part));
            entry.Mesh!.Draw();
        }

        if (anySkinned)
        {
            skinnedShader.Use();
            EnsureBoneBuffer();
            foreach (var entry in _entries)
            {
                if (!entry.Part.Visible || entry.Part.Model?.Asset is not { Animation.HasSkins: true } asset)
                {
                    continue;
                }

                GL.StencilMask(_selectedParts.Contains(entry.Part) ? 0xFF : 0x00);
                var pose = PoseOf(entry.Part, asset);
                skinnedShader.SetMatrix4("uModel", ModelTransform.GetInstanceMatrix(entry.Part));
                foreach (var draw in asset.Draws)
                {
                    if (draw.Skinned)
                    {
                        _bones.Upload(pose.PaletteFloats[draw.MeshIndex]);
                        _modelMeshes.GetSkinnedSurface(asset, draw.MeshIndex, entry.Part.Color).Draw();
                    }
                }
            }

            shader.Use();
        }

        _modelMeshes.ReleaseUnusedTints();

        // Keep the selection marks in the buffer for the outline pass; stop writing and restore baseline.
        GL.StencilMask(0xFF);
        GL.Disable(EnableCap.StencilTest);
    }

    // Selection outlines (unlit shader) — kept separate so lighting never touches the silhouette colour. Skinned model
    // meshes use the skinned unlit shader with the same state.
    internal void RenderOutlines(ShaderProgram shader, ShaderProgram skinnedShader, EditorCamera camera)
    {
        RenderSelectionOutlines(shader, skinnedShader, camera);
    }

    /// <summary>
    /// Whether a Part is drawn (and outlined when selected): not hidden by a script, and not a primitive whose material is
    /// invisible (Add Light's parts: the editor shows their icon instead).
    /// </summary>
    internal static bool IsDrawn(Part part) => part.Visible && !(part.Model is null && part.Material is { IsInvisible: true });

    /// <summary>The pose a model instance is drawn in: its animator's, or the asset's rest pose.</summary>
    private static ModelPose PoseOf(Part part, ModelAsset asset) => part.Animator?.Pose ?? asset.Animation.RestPose;

    private void EnsureBoneBuffer()
    {
        if (!_bonesReady)
        {
            _bones.Initialize();
            _bonesReady = true;
        }
    }

    /// <summary>
    /// Draws a light-blue outer silhouette around each selected part. The selected parts already marked
    /// stencil = 1 over their visible pixels during <see cref="RenderParts"/> (from their own lit
    /// fragments, so the mask matches the lit surface exactly — no depth-fighting). Here we draw each
    /// enlarged mesh only where the stencil is NOT 1 (the rim), so no internal edges appear and the
    /// part's own colour is untouched.
    ///
    /// Depth: LEQUAL + DepthMask off, so the outline sits at the silhouette edge and nearer scene objects
    /// still occlude it, while the enlarged shell never pollutes the depth buffer. The outline writes no
    /// stencil bits (mask 0). Every GL state changed here is restored once at the end so later gizmos and
    /// UI are unaffected. (The leftover selection marks are harmless once StencilTest is disabled, and are
    /// cleared with the stencil buffer at the start of the next frame.)
    /// </summary>
    private void RenderSelectionOutlines(ShaderProgram shader, ShaderProgram skinnedShader, EditorCamera camera)
    {
        if (_highlightEntries.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.StencilTest);
        GL.StencilMask(0x00);                                // outline pass writes no stencil
        GL.StencilFunc(StencilFunction.Notequal, 1, 0xFF);   // only where NOT the selected part (the rim)
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        GL.DepthMask(false);
        GL.DepthFunc(DepthFunction.Lequal);

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
                    shader.SetMatrix4("uModel", model);
                    mesh.Draw();
                }

                // Skinned meshes: the rim in the pose they are drawn in, enlarged about their rest bounds in model space.
                if (asset.Animation.HasSkins)
                {
                    skinnedShader.Use();
                    EnsureBoneBuffer();
                    var pose = PoseOf(part, asset);
                    var instance = ModelTransform.GetInstanceMatrix(part);
                    foreach (var draw in asset.Draws)
                    {
                        if (!draw.Skinned)
                        {
                            continue;
                        }

                        skinnedShader.SetMatrix4("uModel", EnlargeAboutBoundsCenter(instance, asset.RestBoundsOf(draw), thickness));
                        _bones.Upload(pose.PaletteFloats[draw.MeshIndex]);
                        _modelMeshes.GetSkinnedOutline(asset, draw.MeshIndex, OutlineColor).Draw();
                    }

                    shader.Use();
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
                new Vector3(
                    part.Size.X + 2f * thickness,
                    part.Size.Y + 2f * thickness,
                    part.Size.Z + 2f * thickness));
            shader.SetMatrix4("uModel", enlargedModel);
            highlight.Mesh.Draw();
        }

        // Restore the engine baseline (once) so later gizmos and UI are unaffected.
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.StencilFunc(StencilFunction.Always, 0, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        GL.StencilMask(0xFF);
        GL.Disable(EnableCap.StencilTest);
    }

    private static float OutlineThickness(Part part, EditorCamera camera)
    {
        var distance = (part.Position - camera.Position).Length;
        return global::System.Math.Clamp(distance * OutlineThicknessFactor, MinOutlineThickness, MaxOutlineThickness);
    }

    public void Dispose()
    {
        ClearHighlights();
        _selectedParts.Clear();
        Clear();
        if (_bonesReady)
        {
            _bones.Dispose();
            _bonesReady = false;
        }

        DisposeBgfxSkinning();
    }

    private static PartDrawEntry CreateEntry(Part part)
    {
        // A loaded model draws from ModelMeshCache; a model whose asset is missing falls through to its placeholder box.
        if (part.Model?.Asset is not null)
        {
            return new PartDrawEntry { Part = part, Mesh = null, CachedShapeType = part.ShapeType, CachedColor = part.Color };
        }

        var (vertices, indices) = PartMeshBuilder.CreateMesh(part);

        return new PartDrawEntry
        {
            Part = part,
            Mesh = ColoredMesh.FromIndexedVertices(part.Name, vertices, indices),
            CachedShapeType = part.ShapeType,
            CachedColor = part.Color
        };
    }

    private static void EnsureMesh(PartDrawEntry entry)
    {
        if (entry.Part.Model?.Asset is not null
            || entry.Part.ShapeType == entry.CachedShapeType
            && NearlyEqual(entry.Part.Color, entry.CachedColor))
        {
            return;
        }

        RebuildEntryMesh(entry);
    }

    private static void RebuildEntryMesh(PartDrawEntry entry)
    {
        entry.Mesh?.Dispose();
        var rebuilt = CreateEntry(entry.Part);
        entry.Mesh = rebuilt.Mesh;
        entry.CachedShapeType = rebuilt.CachedShapeType;
        entry.CachedColor = rebuilt.CachedColor;
    }

    private void EnsureHighlightMesh(HighlightEntry highlight)
    {
        // The outline colour is fixed, so only a shape change can require a rebuild
        // (the part's own colour is irrelevant to the outline). Models use their cached outline meshes instead.
        if (highlight.Part.Model?.Asset is not null
            || highlight.Mesh is not null && highlight.Part.ShapeType == highlight.CachedShapeType)
        {
            return;
        }

        RebuildHighlightEntry(highlight);
    }

    private void RebuildHighlights()
    {
        ClearHighlights();
        foreach (var part in _selectedParts)
        {
            var entry = new HighlightEntry { Part = part };
            RebuildHighlightEntry(entry);
            _highlightEntries.Add(entry);
        }
    }

    private static void RebuildHighlightEntry(HighlightEntry highlight)
    {
        highlight.Mesh?.Dispose();
        highlight.Mesh = null;
        if (highlight.Part.Model?.Asset is not null)
        {
            return;
        }

        var (vertices, indices) = PartMeshBuilder.CreateHighlightMesh(highlight.Part, OutlineColor);
        highlight.Mesh = ColoredMesh.FromIndexedVertices($"{highlight.Part.Name}_Outline", vertices, indices);
        highlight.CachedShapeType = highlight.Part.ShapeType;
    }

    private void ClearHighlights()
    {
        foreach (var highlight in _highlightEntries)
        {
            highlight.Mesh?.Dispose();
        }

        _highlightEntries.Clear();
    }

    private static bool NearlyEqual(Vector3 a, Vector3 b)
    {
        return global::System.Math.Abs(a.X - b.X) < 1e-4f
            && global::System.Math.Abs(a.Y - b.Y) < 1e-4f
            && global::System.Math.Abs(a.Z - b.Z) < 1e-4f;
    }

    private static Matrix4 BuildModelMatrix(Part part) =>
        Matrix4.ComposeTrs(part.Position, part.Rotation, part.Size);

    // Every rigid mesh reference of the model's node hierarchy: instance transform * the node's transform (where an
    // animation has it now, else its rest transform), in the colours of this instance's tint (Part.Color). Skinned meshes
    // are drawn separately (their bones place them).
    private IEnumerable<(ColoredMesh Mesh, Matrix4 Model)> ModelSurfaceDraws(Part part, ModelAsset asset)
    {
        var instance = ModelTransform.GetInstanceMatrix(part);
        var pose = part.Animator?.Pose;
        foreach (var draw in asset.Draws)
        {
            if (!draw.Skinned)
            {
                yield return (_modelMeshes.GetSurface(asset, draw, part.Color), Matrix4.Multiply(instance, NodeTransformOf(draw, pose)));
            }
        }
    }

    // The primitive outline is the part's mesh enlarged by 2 * thickness (Size + 2t). A model gets the same rim per
    // mesh: each mesh is scaled about its own bounds centre so every local axis grows by ~2 * thickness in world
    // units after the full transform. Axes on which a mesh has no world extent (flat meshes) are left as they are.
    private IEnumerable<(ColoredMesh Mesh, Matrix4 Model)> ModelOutlineDraws(Part part, ModelAsset asset, float thickness)
    {
        var instance = ModelTransform.GetInstanceMatrix(part);
        var pose = part.Animator?.Pose;
        foreach (var draw in asset.Draws)
        {
            if (draw.Skinned)
            {
                continue;
            }

            var meshToWorld = Matrix4.Multiply(instance, NodeTransformOf(draw, pose));
            var bounds = asset.Model.Meshes[draw.MeshIndex].Bounds;
            yield return (_modelMeshes.GetOutline(asset, draw.MeshIndex, OutlineColor), EnlargeAboutBoundsCenter(meshToWorld, bounds, thickness));
        }
    }

    private static Matrix4 NodeTransformOf(ModelDraw draw, ModelPose? pose) =>
        pose is not null && draw.Node >= 0 ? pose.GlobalOf(draw.Node) : draw.Transform;

    private static Matrix4 EnlargeAboutBoundsCenter(Matrix4 meshToWorld, ImportedBounds bounds, float thickness)
    {
        if (bounds.IsEmpty)
        {
            return meshToWorld;
        }

        var center = (bounds.Min + bounds.Max) * 0.5f;
        var size = bounds.Size;
        var growth = new Vector3(
            AxisGrowth(meshToWorld.M11, meshToWorld.M21, meshToWorld.M31, size.X, thickness),
            AxisGrowth(meshToWorld.M12, meshToWorld.M22, meshToWorld.M32, size.Y, thickness),
            AxisGrowth(meshToWorld.M13, meshToWorld.M23, meshToWorld.M33, size.Z, thickness));
        var aboutCenter = Matrix4.Multiply(
            Matrix4.CreateTranslation(center),
            Matrix4.Multiply(Matrix4.CreateScale(growth), Matrix4.CreateTranslation(center * -1f)));
        return Matrix4.Multiply(meshToWorld, aboutCenter);
    }

    // (x, y, z) is the world image of one local unit axis (a matrix column).
    private static float AxisGrowth(float x, float y, float z, float localSize, float thickness)
    {
        var worldExtent = MathF.Sqrt(x * x + y * y + z * z) * localSize;
        return worldExtent > 1e-5f ? (worldExtent + 2f * thickness) / worldExtent : 1f;
    }

    private void Clear()
    {
        foreach (var entry in _entries)
        {
            entry.Mesh?.Dispose();
        }

        _entries.Clear();
        _modelMeshes.Clear();
        DisposeBgfxSkinning();
    }

    private sealed class PartDrawEntry
    {
        public required Part Part { get; init; }

        // Null for a model part: its meshes are shared per asset in ModelMeshCache.
        public required ColoredMesh? Mesh { get; set; }
        public ShapeType CachedShapeType { get; set; }
        public Vector3 CachedColor { get; set; }
    }

    private sealed class HighlightEntry
    {
        public required Part Part { get; init; }
        public ColoredMesh? Mesh { get; set; }
        public ShapeType CachedShapeType { get; set; }
    }
}
