using Wisko.Engine.Editor;
using Wisko.Engine.Rendering.Backend.Bgfx.Editor;
using Wisko.Engine.Rendering.Backend.Bgfx.UI;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering;

// bgfx backend of the editor Renderer. Editor behaviour (Update: input, tools, picking, gizmo controllers,
// history, session) is shared and unchanged; this partial only replaces the OpenGL Initialize / Render /
// thumbnail / shutdown steps with bgfx submits in the same order and with the same explicit state:
//   view 0 (world): environment -> lit parts (+ selection stencil) -> particles, debug lines -> outline -> light icons
//                   -> gizmo -> labels -> sun
//   view 1 (UI):    box selection, toolbar, side panel, toast, then Hub / name prompt
public sealed partial class Renderer
{
    private readonly BgfxMoveGizmoRenderer _bgfxMoveGizmo = new();
    private readonly BgfxRotateGizmoRenderer _bgfxRotateGizmo = new();
    private readonly BgfxResizeGizmoRenderer _bgfxResizeGizmo = new();
    private BgfxEditorGraphics? _bgfx;
    private BgfxUiBackend? _bgfxUi;

    private void PrepareBgfx()
    {
        _bgfxUi = new BgfxUiBackend();
        _bgfxUi.Initialize(DpiScale);
        _uiBackend = _bgfxUi;

        _bgfx = new BgfxEditorGraphics();
        _bgfx.Initialize();
        ColoredMesh.GpuBackend = _bgfx; // every mesh created from here on (scene, parts, gizmos) lives on bgfx
    }

    private void InitializeBgfx(SceneComposer scene, Workspace workspace, EditorSelection selection)
    {
        _workspace = workspace;
        _selection = selection;
        ConfigureCamera();

        var ui = _bgfxUi!;
        _toolbar.Initialize(ui, _toolBench);
        _worldExplorer.Initialize(ui);
        _colorPanel.Initialize(ui);
        _toast.Initialize(ui);
        _boxSelectionRenderer.Initialize(ui);
        _dimensionLabels.InitializeLayout(ui.Fonts); // same glyph atlas as the bgfx UI
        _bgfxMoveGizmo.Initialize();
        _bgfxRotateGizmo.Initialize();
        _bgfxResizeGizmo.Initialize();

        InitializeEditorState(scene, workspace, selection);
    }

    private void RenderBgfx()
    {
        var graphics = _bgfx;
        if (graphics is null)
        {
            return;
        }

        // A script tab covers the whole content area: UI only (the scene and camera stay as they are).
        if (ActiveDocument is not null)
        {
            _bgfxUi!.BeginFrame(_width, _height, _fbWidth, _fbHeight);
            RenderScreenUi();
            return;
        }

        const ushort world = BgfxEditorGraphics.WorldView;
        var aspect = _width / (float)global::System.Math.Max(_height, 1);
        var view = _camera.ViewMatrix;
        var projection = _camera.GetProjectionMatrix(aspect);
        graphics.SetCamera(world, view, projection);
        graphics.SetAtmosphere(BuildAtmosphereUniforms(aspect));

        SubmitWorldBgfx(graphics, world, writeSelectionStencil: true);

        // While playing: scripts' particles and debug lines (transient geometry, not yet run on macOS).
        SubmitGameplayBgfx(graphics, world);
        _partRenderer.SubmitOutlinesBgfx(graphics, world, _camera);
        SubmitLightGizmosBgfx(graphics, world); // light icons, a selected light's reach, the selected baseplate's outline

        // Gizmos are editing handles: hidden while playing (the selection outline stays).
        var selected = _playMode.IsPlaying || _toolBench.Current == ToolBenchSection.UI ? null : _selection?.SelectedPart;
        if (selected is not null)
        {
            switch (_toolState.ActiveTool)
            {
                case EditorToolMode.Move:
                    _bgfxMoveGizmo.Submit(graphics, world, selected, _camera, _moveHover, _moveGizmoController.ActiveAxis);
                    SubmitDimensionLabelsBgfx(graphics, world, selected, EditorToolMode.Move);
                    break;
                case EditorToolMode.Resize:
                    var showResizeCorners = (_selection?.SelectedParts.Count ?? 0) == 1;
                    _bgfxResizeGizmo.Submit(graphics, world, selected, _camera, _resizeHover, _resizeGizmoController.ActiveHandle,
                        _resizeCornerHover, _resizeGizmoController.ActiveCorner, showResizeCorners);
                    SubmitDimensionLabelsBgfx(graphics, world, selected, EditorToolMode.Resize);
                    break;
                case EditorToolMode.Rotate:
                    _bgfxRotateGizmo.Submit(graphics, world, selected, _camera, _rotateHover, _rotateGizmoController.ActiveHandle);
                    SubmitDimensionLabelsBgfx(graphics, world, selected, EditorToolMode.Rotate);
                    break;
            }
        }

        _bgfxUi!.BeginFrame(_width, _height, _fbWidth, _fbHeight);
        RenderScreenUi();
    }

    // Atmosphere sky first (no depth), then the environment (depth LESS + write) and the lit parts, all with this
    // frame's Atmosphere parameters; the thumbnail capture reuses this without stencil.
    private void SubmitWorldBgfx(BgfxEditorGraphics graphics, ushort view, bool writeSelectionStencil)
    {
        graphics.SubmitAtmosphereSky(view);
        foreach (var drawable in _drawables)
        {
            if (IsEnvironmentDrawn(drawable))
            {
                graphics.SubmitEnvironment(view, drawable.Mesh, drawable.Model, BgfxEditorGraphics.OpaqueState);
            }
        }

        _partRenderer.SubmitPartsBgfx(graphics, view, writeSelectionStencil);
    }

    private void SubmitDimensionLabelsBgfx(BgfxEditorGraphics graphics, ushort view, Part selected, EditorToolMode tool)
    {
        if (!_dimensionLabels.BuildGeometry(selected, _camera, tool, _moveGizmoController, _resizeGizmoController, DpiScale, _rotateGizmoController))
        {
            return;
        }

        // Same as the GL labels: background quads, then glyphs; depth off, glyphs alpha-blended.
        graphics.SubmitTransientTriangles(view, _dimensionLabels.BackgroundVertices, BgfxEditorGraphics.OverlayState);
        graphics.SubmitWorldText(view, _dimensionLabels.GlyphVertices, _bgfxUi!.Fonts.AtlasTexture);
    }

    /// <summary>World-only thumbnail (Atmosphere sky, environment, lit parts) rendered offscreen and read back asynchronously.</summary>
    private bool TryCaptureViewportThumbnailBgfx(int width, int height, out byte[] rgba)
    {
        rgba = [];
        var graphics = _bgfx;
        if (graphics is null || _fbWidth <= 0 || _fbHeight <= 0)
        {
            return false;
        }

        var aspect = _width / (float)global::System.Math.Max(_height, 1);
        var view = _camera.ViewMatrix;
        var projection = _camera.GetProjectionMatrix(aspect);
        var captured = graphics.TryCaptureWorld(_fbWidth, _fbHeight, captureView =>
        {
            graphics.SetCamera(captureView, view, projection);
            graphics.SetAtmosphere(BuildAtmosphereUniforms(aspect));
            SubmitWorldBgfx(graphics, captureView, writeSelectionStencil: false);
        }, out var pixels);

        if (!captured)
        {
            return false;
        }

        rgba = ImageResample.CropAndDownscaleFromFramebuffer(pixels, _fbWidth, _fbHeight, width, height);
        return true;
    }

    private void BeginScreenUiFrameBgfx() => _bgfxUi?.BeginFrame(_width, _height, _fbWidth, _fbHeight);

    private void ShutdownBgfx()
    {
        _playMode.Dispose();
        ShutdownScriptEditorUi();
        ShutdownRuntimeGui();
        foreach (var drawable in _drawables)
        {
            drawable.Mesh.Dispose();
        }

        _drawables.Clear();
        _partRenderer.Dispose();
        DisposeGameplayRendering();
        _bgfxMoveGizmo.Dispose();
        _bgfxRotateGizmo.Dispose();
        _bgfxResizeGizmo.Dispose();
        _toolbar.Dispose();
        _worldExplorer.Dispose();
        _colorPanel.Dispose();
        _toast.Dispose();
        _boxSelectionRenderer.Dispose();
        ColoredMesh.GpuBackend = null;
        _bgfx?.Dispose();
        _bgfx = null;
        _bgfxUi?.Dispose(); // glyph atlas + UI programs last (labels and UI reference the atlas)
        _bgfxUi = null;
        _uiBackend = null;
    }
}
