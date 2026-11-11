using Wisko.Engine.Assets;
using Wisko.Engine.Editor;
using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Rendering.Backend;
using Wisko.Engine.Scripting;
using Wisko.Engine.Text;
using Wisko.Engine.UI;
using Wisko.Engine.World;
using OpenTK.Graphics.OpenGL4;

namespace Wisko.Engine.Rendering;

public sealed partial class Renderer : IDisposable
{
    private const string VertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aColor;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vColor;

        void main()
        {
            vColor = aColor;
            gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core

        in vec3 vColor;
        out vec4 FragColor;

        void main()
        {
            FragColor = vec4(vColor, 1.0);
        }
        """;

    // Dedicated LIT shader for normal part surfaces only. Reads position + color, plus an optional vertex
    // normal (location 2): sphere / cylinder use it for smooth shading; flat primitives leave it zero and
    // fall back to the world-position derivative normal. World position is passed for that derivative path.
    private const string LitVertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aColor;
        layout (location = 2) in vec3 aNormal;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vColor;
        out vec3 vWorldPos;
        out vec3 vNormal;

        void main()
        {
            vColor = aColor;
            vec4 world = uModel * vec4(aPosition, 1.0);
            vWorldPos = world.xyz;
            // Inverse-transpose normal matrix so non-uniform scaling shades correctly. A zero vertex normal
            // (flat primitives) stays zero here and triggers the derivative fallback in the fragment stage.
            mat3 normalMatrix = transpose(inverse(mat3(uModel)));
            vNormal = normalMatrix * aNormal;
            gl_Position = uProjection * uView * world;
        }
        """;

    // Lit fragment body, after the shared Atmosphere functions (AtmosphereSkyRenderer.WithAtmosphere): the Atmosphere
    // sun + ambient light, then haze, fog and image controls; the plain environment keeps the former light and clamp.
    private const string LitFragmentShaderBody = """
        in vec3 vColor;
        in vec3 vWorldPos;
        in vec3 vNormal;
        out vec4 FragColor;

        void main()
        {
            // Smooth per-vertex normal when one was supplied (sphere / cylinder sides / cylinder caps);
            // otherwise the flat face normal from world-position screen-space derivatives (box / wedge /
            // pyramid). Either way orientation is fixed by triangle winding (gl_FrontFacing) only, never the
            // camera, so lighting stays stable as the camera moves. Degenerate derivatives (edge-on /
            // zero-area) fall back to ambient (no NaN).
            vec3 normal;
            bool hasNormal = true;
            if (length(vNormal) > 1e-4)
            {
                normal = normalize(vNormal);
            }
            else
            {
                vec3 faceCross = cross(dFdx(vWorldPos), dFdy(vWorldPos));
                float faceLen = length(faceCross);
                if (faceLen > 1e-8)
                {
                    normal = faceCross / faceLen;
                }
                else
                {
                    hasNormal = false;
                    normal = vec3(0.0);
                }
            }

            float diffuse = 0.0;
            if (hasNormal)
            {
                if (!gl_FrontFacing)
                {
                    normal = -normal;
                }
                diffuse = max(dot(normal, u_atmo[0].xyz), 0.0);
            }

            vec3 lit = vColor * (u_atmo[2].rgb + u_atmo[1].rgb * diffuse);
            FragColor = vec4(atmoGrade(atmoDistance(lit, vWorldPos)), 1.0);
        }
        """;

    // Skinned model meshes (Animation): the lit shader's inputs plus four bone slots and weights per vertex. The vertex is
    // moved into model space by the weighted sum of its bones' matrices (linear blend skinning; the matrices of the mesh
    // being drawn are in BoneBlock), then by uModel as any part; the normal by the inverse transpose of both, so bones
    // that scale unevenly still shade correctly. Used with the lit fragment body (surfaces) and the unlit fragment shader
    // (selection rim).
    private const string SkinnedVertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aColor;
        layout (location = 2) in vec3 aNormal;
        layout (location = 3) in vec4 aJoints;
        layout (location = 4) in vec4 aWeights;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        layout (std140) uniform BoneBlock
        {
            mat4 uBones[256];
        };

        out vec3 vColor;
        out vec3 vWorldPos;
        out vec3 vNormal;

        void main()
        {
            mat4 skin = aWeights.x * uBones[int(aJoints.x + 0.5)]
                      + aWeights.y * uBones[int(aJoints.y + 0.5)]
                      + aWeights.z * uBones[int(aJoints.z + 0.5)]
                      + aWeights.w * uBones[int(aJoints.w + 0.5)];
            vColor = aColor;
            vec4 world = uModel * (skin * vec4(aPosition, 1.0));
            vWorldPos = world.xyz;
            mat3 linear = mat3(uModel) * mat3(skin);
            vNormal = transpose(inverse(linear)) * aNormal;
            gl_Position = uProjection * uView * world;
        }
        """;

    // Environment (baseplate): the unlit colours with the Atmosphere applied. Clip position exactly as
    // the unlit shader, so the plain environment draws the same pixels.
    private const string EnvironmentVertexShaderSource = """
        #version 330 core

        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec3 aColor;

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vColor;
        out vec3 vWorldPos;

        void main()
        {
            vColor = aColor;
            vWorldPos = (uModel * vec4(aPosition, 1.0)).xyz;
            gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
        }
        """;

    private const string EnvironmentFragmentShaderBody = """
        in vec3 vColor;
        in vec3 vWorldPos;
        out vec4 FragColor;

        void main()
        {
            if (!atmoOn())
            {
                FragColor = vec4(vColor, 1.0);
                return;
            }

            vec3 color = atmoDistance(vColor * u_atmo[8].rgb, vWorldPos);
            FragColor = vec4(atmoGrade(color), 1.0);
        }
        """;

    private readonly EditorCamera _camera = new();
    private readonly AtmosphereSkyRenderer _atmosphereSky = new();
    private readonly PartRenderer _partRenderer = new();
    private readonly ToolbarRenderer _toolbar = new();
    private readonly WorldExplorerRenderer _worldExplorer = new() { ScriptLabelOf = ScriptLabel };
    private readonly ColorPanelRenderer _colorPanel = new();
    private readonly ToastRenderer _toast = new();
    private readonly BoxSelectionRenderer _boxSelectionRenderer = new();
    private readonly BoxSelectionController _boxSelection = new();
    private readonly EditorToolState _toolState = new();
    private readonly EditorShapeState _shapeState = new();
    private readonly MoveGizmoRenderer _moveGizmo = new();
    private readonly MoveGizmoController _moveGizmoController = new();
    private readonly ResizeGizmoRenderer _resizeGizmo = new();
    private readonly ResizeGizmoController _resizeGizmoController = new();
    private readonly DimensionLabelRenderer _dimensionLabels = new();
    private readonly RotateGizmoRenderer _rotateGizmo = new();
    private readonly RotateGizmoController _rotateGizmoController = new();
    private RotateHandle _rotateHover = RotateHandle.None;
    private GizmoAxis _moveHover = GizmoAxis.None;
    private ResizeHandle _resizeHover = ResizeHandle.None;
    private ResizeCornerHandle _resizeCornerHover = ResizeCornerHandle.None;
    private readonly List<SceneObject> _drawables = new();
    private readonly PartDragger _partDragger = new();
    private readonly EditorHistory _editorHistory = new();
    private readonly PlayModeController _playMode = new();
    private PlayCamera? _playCamera;
    private readonly Queue<string> _pendingScriptMessages = new();
    private bool _playStartedThisFrame;
    private float _scriptToastCooldown;
    private int _scriptToastsShown;
    private Part? _keyboardMovePart;
    private Vector3 _keyboardMoveStart;
    private ShaderProgram? _shader;
    private ShaderProgram? _litShader;
    private ShaderProgram? _environmentShader;
    private ShaderProgram? _skinnedLitShader;
    private ShaderProgram? _skinnedUnlitShader;
    private int _width;
    private int _height;
    private int _fbWidth;
    private int _fbHeight;
    private Workspace? _workspace;
    private EditorSelection? _selection;
    private bool _toolbarConsumedClick;
    private FontLibrary? _fonts;
    private IUiBackend? _uiBackend;
    private readonly RenderBackendKind _backend;

    private const float ScriptToastSeconds = 4f;
    private const float ScriptToastInterval = 2f;
    private const int MaxScriptToastsPerPlay = 6;
    private const int MaxScriptToastLength = 110;
    private const string PlayBlockedMessage = "Stop Play Mode to edit the scene.";

    // What scripts see while the editor (not the game) has the keyboard: nothing held, nothing moving.
    private static readonly Engine.Scripting.ScriptInputState NoScriptInput = new();

    // Legacy scales the screen UI actually uses; pre-warmed so first frames have no raster hitch.
    private static readonly float[] PrewarmScales = { 1.65f, 1.7f, 1.8f, 1.85f, 1.9f, 2.0f, 2.1f };

    // Framebuffer-to-logical ratio (2x on Retina). Layout stays logical; glyphs rasterize at device px.
    private float DpiScale => _width > 0 ? _fbWidth / (float)_width : 1f;

    public Renderer(int width, int height, RenderBackendKind backend = RenderBackendKind.OpenGL)
    {
        _backend = backend;
        _width = width;
        _height = height;
        // Seeded equal to the logical size; corrected on the first framebuffer-resize.
        // On HiDPI (e.g. Retina) the real framebuffer is larger than the logical client size.
        _fbWidth = width;
        _fbHeight = height;
    }

    /// <summary>
    /// Backend setup that must exist before any mesh is created (the scene is composed right after this).
    /// OpenGL needs nothing here; bgfx creates its editor GPU resources and routes ColoredMesh to bgfx.
    /// </summary>
    internal void PrepareBackend()
    {
        if (_backend == RenderBackendKind.Bgfx)
        {
            PrepareBgfx();
        }
    }

    // The public entry points only dispatch; each backend's body lives in its own method so the bgfx
    // path never JIT-compiles OpenGL code (and so never loads OpenTK.Graphics).
    public void Initialize(SceneComposer scene, Workspace workspace, EditorSelection selection)
    {
        if (_backend == RenderBackendKind.Bgfx)
        {
            InitializeBgfx(scene, workspace, selection);
        }
        else
        {
            InitializeGl(scene, workspace, selection);
        }
    }

    private void InitializeGl(SceneComposer scene, Workspace workspace, EditorSelection selection)
    {
        _workspace = workspace;
        _selection = selection;
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.ClearColor(0.58f, 0.78f, 0.96f, 1f);

        ConfigureCamera();

        _shader = new ShaderProgram(VertexShaderSource, FragmentShaderSource);
        _litShader = new ShaderProgram(LitVertexShaderSource, AtmosphereSkyRenderer.WithAtmosphere(LitFragmentShaderBody));
        _environmentShader = new ShaderProgram(EnvironmentVertexShaderSource, AtmosphereSkyRenderer.WithAtmosphere(EnvironmentFragmentShaderBody));
        _skinnedLitShader = new ShaderProgram(SkinnedVertexShaderSource, AtmosphereSkyRenderer.WithAtmosphere(LitFragmentShaderBody));
        _skinnedUnlitShader = new ShaderProgram(SkinnedVertexShaderSource, FragmentShaderSource);
        _skinnedLitShader.BindUniformBlock(BoneUniformBuffer.BlockName, BoneUniformBuffer.BindingPoint);
        _skinnedUnlitShader.BindUniformBlock(BoneUniformBuffer.BlockName, BoneUniformBuffer.BindingPoint);
        GL.GetInteger(GetPName.MaxUniformBlockSize, out var maxBlockBytes);
        Console.WriteLine($"[Renderer] Skinning: GPU, up to {Animation.SkinningLimits.MaxPaletteSize} bones per mesh ({BoneUniformBuffer.SizeInBytes / 1024} KB bone block; this GPU allows {maxBlockBytes / 1024} KB)");

        // One shared runtime font resource for all screen-space UI (single atlas, GL context is live here).
        _fonts = new FontLibrary();
        _fonts.Initialize();
        _fonts.PrewarmAscii(ComputePrewarmBuckets(DpiScale));

        _uiBackend = new GlUiBackend(_fonts);

        _atmosphereSky.Initialize();
        InitializeUniversalGl();
        _toolbar.Initialize(_uiBackend, _toolBench);
        _worldExplorer.Initialize(_uiBackend);
        _colorPanel.Initialize(_uiBackend);
        _toast.Initialize(_uiBackend);
        _boxSelectionRenderer.Initialize(_uiBackend);
        _moveGizmo.Initialize();
        _resizeGizmo.Initialize();
        _rotateGizmo.Initialize();
        _dimensionLabels.Initialize(_fonts);

        InitializeEditorState(scene, workspace, selection);

        GlDebug.CheckError("renderer initialization");
    }

    private void ConfigureCamera()
    {
        _camera.FieldOfViewDegrees = 55f;
        _camera.NearPlane = 0.1f;
        _camera.FarPlane = 2000f;
    }

    // Backend-independent editor state shared by the OpenGL and bgfx initialization paths.
    private void InitializeEditorState(SceneComposer scene, Workspace workspace, EditorSelection selection)
    {
        _drawables.Clear();
        _drawables.AddRange(scene.Objects);
        _playMode.PhysicsEnvironment = scene.PhysicsEnvironment;
        _sceneEnvironment = scene.PhysicsEnvironment;
        SyncEnvironmentList();
        _partRenderer.Sync(workspace);
        _partRenderer.SetSelected(selection.SelectedParts);

        _worldExplorer.RenameCommitted = (part, oldName, newName) =>
        {
            _editorHistory.Push(EditorCommand.Rename(part, oldName, newName));
        };
        _worldExplorer.GroupRenameCommitted = (group, newName) =>
        {
            var command = WorldGrouping.Rename(_workspace!, group, newName, _selection?.SelectedParts ?? [], out var error);
            FinishOrganize(command, error, null);
        };

        InitializeScriptEditorUi(UiBackend);
        InitializeRuntimeGui(UiBackend);
    }

    public void Update(InputManager input, float deltaTime)
    {
        _toolbarConsumedClick = false;
        _wheelConsumed = false;
        _physicsKeyboardThisFrame = false;
        _guiKeyboardThisFrame = false;
        _editorClock += deltaTime;
        _lastMouseX = input.MouseX;
        _lastMouseY = input.MouseY;
        _runtimeTextBoxKeyboard = false;

        // A modal question owns all input (scripts keep running in Play, without keyboard/mouse).
        if (_confirm.IsOpen)
        {
            _confirm.Update(input, _width, _height);
            UpdatePlayMode(input, deltaTime, suppressScriptInput: true);
            _toast.Update(deltaTime);
            return;
        }

        // The game holds the mouse (Input.SetMouseLocked): the hidden cursor's position means nothing, so the editor's
        // toolbar, panels and view take no input until it lets go (Escape still stops Play).
        if (GameHoldsMouse)
        {
            HandleRuntimeTextBox(input);
            UpdatePlayMode(input, deltaTime, suppressScriptInput: false);
            _toast.Update(deltaTime);
            return;
        }

        // A game TextBox with the keyboard gets this frame's typing before anything else reads the keys.
        HandleRuntimeTextBox(input);

        // Scene shortcuts and the camera only act while the 3D View is the active tab; in a script tab the keyboard
        // belongs to the code editor.
        if (ActiveDocument is null && !RuntimeTextBoxOwnsKeyboard)
        {
            HandleUndoRedo(input);
        }

        SyncEnvironmentListIfChanged();
        HandleRotationFields(input);
        HandleToolbar(input);
        HandlePhysicsPanel(input);
        HandleAnimationPanel(input);
        HandleGuiPanel(input);
        HandleTabStrip(input);
        HandleOutputPanel(input);
        HandleEnvironmentPanel(input);
        HandleWorldExplorer(input);
        HandleColorPanel(input);
        if (ActiveDocument is { } document)
        {
            CancelRuntimeGuiPointer();
            HandleCodeEditor(input, document, deltaTime);
        }
        else
        {
            HandleDeleteSelectedPart(input);
            HandleDuplicateSelectedPart(input);
            HandleSaveShortcut(input);
            HandleGroupShortcuts(input);
            HandleRotateSelectedPart(input);
            HandleCamera(input, deltaTime);
            HandleRuntimeGuiInput(input);
            if (GuiEditorActive)
            {
                HandleGuiEditorInput(input);
            }
            else
            {
                CommitGuiDrag();
                HandleViewportInput(input, deltaTime);
            }
        }

        UpdatePlayMode(input, deltaTime, suppressScriptInput: ActiveDocument is not null);
        UpdateAnimationPreview(deltaTime);
        UpdateScriptDocuments();
        _toast.Update(deltaTime);
    }

    public void Render()
    {
        if (_backend == RenderBackendKind.Bgfx)
        {
            RenderBgfx();
        }
        else
        {
            RenderGl();
        }
    }

    private void RenderGl()
    {
        if (_shader is null || _litShader is null)
        {
            return;
        }

        // The Universal renderer collects this frame's scene once (feeds and the main view share it).
        var universal = ActiveDocument is null && BeginUniversalFrame();

        // Camera feeds first (each into its own texture, for the game GUI's Images).
        RenderCameraFeedsGl();

        GL.Viewport(0, 0, _fbWidth, _fbHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        // A script tab covers the whole content area: no 3D world to draw (the camera and scene stay as they are).
        if (ActiveDocument is not null)
        {
            RenderScreenUi();
            return;
        }

        var aspect = _width / (float)global::System.Math.Max(_height, 1);
        var view = _camera.ViewMatrix;
        var projection = _camera.GetProjectionMatrix(aspect);
        if (universal)
        {
            // The scene (and, with post-processing, its final image, depth and the selection stencil) into the window.
            _universal!.RenderMain(global::Wisko.Engine.Rendering.Universal.ViewSetup.FromCamera(_camera, aspect, _fbWidth, _fbHeight), _fbWidth, _fbHeight,
                _ => DrawUniversalParticles(_camera));
        }
        else
        {
            RenderWorldGl(view, projection, aspect);
        }

        // While playing: scripts' particles and debug lines, in the world, before the editor's overlays.
        RenderGameplayGl(view, projection, _camera);

        // Editor overlays (selection outlines, gizmos, labels) stay on the UNLIT shader: no Atmosphere on them.
        _skinnedUnlitShader!.Use();
        _skinnedUnlitShader.SetMatrix4("uView", view);
        _skinnedUnlitShader.SetMatrix4("uProjection", projection);
        _shader.Use();
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProjection", projection);
        _partRenderer.RenderOutlines(_shader, _skinnedUnlitShader, _camera);
        RenderLightGizmosGl(view, projection);

        // Gizmos are editing handles: hidden while playing (the selection outline stays).
        var selected = _playMode.IsPlaying || _toolBench.Current == ToolBenchSection.UI ? null : _selection?.SelectedPart;
        if (selected is not null)
        {
            switch (_toolState.ActiveTool)
            {
                case EditorToolMode.Move:
                    _moveGizmo.Render(_shader, selected, _camera, _moveHover, _moveGizmoController.ActiveAxis);
                    _dimensionLabels.Render(_shader, selected, _camera, view, projection, EditorToolMode.Move, _moveGizmoController, _resizeGizmoController, DpiScale);
                    break;
                case EditorToolMode.Resize:
                    var showResizeCorners = (_selection?.SelectedParts.Count ?? 0) == 1;
                    _resizeGizmo.Render(_shader, selected, _camera, _resizeHover, _resizeGizmoController.ActiveHandle, _resizeCornerHover, _resizeGizmoController.ActiveCorner, showResizeCorners);
                    _dimensionLabels.Render(_shader, selected, _camera, view, projection, EditorToolMode.Resize, _moveGizmoController, _resizeGizmoController, DpiScale);
                    break;
                case EditorToolMode.Rotate:
                    _rotateGizmo.Render(_shader, selected, _camera, _rotateHover, _rotateGizmoController.ActiveHandle);
                    _dimensionLabels.Render(_shader, selected, _camera, view, projection, EditorToolMode.Rotate, _moveGizmoController, _resizeGizmoController, DpiScale,
                        _rotateGizmoController);
                    break;
            }
        }

        RenderScreenUi();
        EndUniversalFrame();
    }

    // The world only: the Atmosphere sky behind everything, the environment, then the lit parts. The frame and the
    // thumbnail capture share it; editor overlays are drawn afterwards by the caller.
    private void RenderWorldGl(Matrix4 view, Matrix4 projection, float aspect, EditorCamera? camera = null)
    {
        // The Universal renderer draws straight into the bound framebuffer (camera feeds, thumbnails).
        if (_universalFrame)
        {
            RenderWorldUniversalDirect(aspect, camera ?? _camera, selection: camera is null);
            return;
        }

        var atmosphere = BuildAtmosphereUniforms(aspect, camera);
        _atmosphereSky.Render(atmosphere);

        _environmentShader!.Use();
        _environmentShader.SetMatrix4("uView", view);
        _environmentShader.SetMatrix4("uProjection", projection);
        _environmentShader.SetVec4Array("u_atmo", atmosphere);
        foreach (var drawable in _drawables)
        {
            if (!IsEnvironmentDrawn(drawable))
            {
                continue;
            }

            _environmentShader.SetMatrix4("uModel", drawable.Model);
            drawable.Mesh.Draw();
        }

        // Normal part surfaces are LIT (dedicated shader); skinned model meshes use its skinned twin.
        _skinnedLitShader!.Use();
        _skinnedLitShader.SetMatrix4("uView", view);
        _skinnedLitShader.SetMatrix4("uProjection", projection);
        _skinnedLitShader.SetVec4Array("u_atmo", atmosphere);
        _litShader!.Use();
        _litShader.SetMatrix4("uView", view);
        _litShader.SetMatrix4("uProjection", projection);
        _litShader.SetVec4Array("u_atmo", atmosphere);
        // (Not inside a camera feed: those are timed as a whole, and GL time queries cannot nest.)
        var timeParts = MeasurePartTiming && !_renderingFeeds;
        if (timeParts)
        {
            BeginPartTiming();
        }

        _partRenderer.RenderParts(_litShader, _skinnedLitShader);
        if (timeParts)
        {
            EndPartTiming();
        }
    }

    /// <summary>
    /// Diagnostics for tests and benchmarks (off by default, then free): when on, drawing the parts is timed every frame,
    /// on the CPU (submitting, posing and uploading bone matrices) and on the GPU (an OpenGL timer query, read back a
    /// frame later).
    /// </summary>
    internal static bool MeasurePartTiming { get; set; }

    /// <summary>CPU milliseconds the last frame spent drawing parts (with <see cref="MeasurePartTiming"/>).</summary>
    internal double LastPartsCpuMs { get; private set; }

    /// <summary>GPU milliseconds of the last measured part drawing (with <see cref="MeasurePartTiming"/>; 0 until one is read).</summary>
    internal double LastPartsGpuMs { get; private set; }

    private readonly System.Diagnostics.Stopwatch _partTimer = new();
    private int _partQuery;
    private bool _partQueryPending;

    private void BeginPartTiming()
    {
        // One query in flight: read it when its result is ready, then time the next frame.
        if (_partQueryPending)
        {
            GL.GetQueryObject(_partQuery, GetQueryObjectParam.QueryResultAvailable, out int available);
            if (available != 0)
            {
                GL.GetQueryObject(_partQuery, GetQueryObjectParam.QueryResult, out long nanoseconds);
                LastPartsGpuMs = nanoseconds / 1_000_000d;
                _partQueryPending = false;
            }
        }

        if (!_partQueryPending)
        {
            if (_partQuery == 0)
            {
                _partQuery = GL.GenQuery();
            }

            GL.BeginQuery(QueryTarget.TimeElapsed, _partQuery);
        }

        _partTimer.Restart();
    }

    private void EndPartTiming()
    {
        _partTimer.Stop();
        LastPartsCpuMs = _partTimer.Elapsed.TotalMilliseconds;
        if (!_partQueryPending)
        {
            GL.EndQuery(QueryTarget.TimeElapsed);
            _partQueryPending = true;
        }
    }

    // Screen-space editor UI after the world (box selection, toolbar, side panel, toast); both backends.
    private void RenderScreenUi()
    {
        var dpiScale = DpiScale;
        if (ActiveDocument is null)
        {
            // The game GUI (Play only) or the UI section's workspace (Edit) sits on the game view, under all editor chrome.
            RenderRuntimeGui(dpiScale);
            RenderGuiEditor(dpiScale);
            _boxSelectionRenderer.Render(_width, _height, dpiScale, _boxSelection);
        }

        RenderDocumentWorkspace();
        var toolbar = ToolbarState;
        _toolbar.Render(_width, _height, dpiScale, toolbar);
        if (_colorPanel.IsVisible)
        {
            _colorPanel.Render(_width, _height, dpiScale);
        }
        else if (_workspace is not null && _worldExplorer.IsVisible)
        {
            // Selecting a Part anywhere (viewport, box select, undo) ends the Baseplate's selection; a selection that is no
            // longer exactly a group's Parts ends that group's.
            if (_selection is { SelectedParts.Count: > 0 })
            {
                _worldExplorer.SelectEnvironment(-1);
            }

            if (_worldExplorer.SelectedGroup is { } shownGroup && _selection is not null
                && !PartGroups.PartsIn(_workspace.Parts, shownGroup).SequenceEqual(_selection.SelectedParts))
            {
                _worldExplorer.SelectedGroup = null;
            }

            _worldExplorer.Render(_width, _height, dpiScale, _workspace, _selection?.SelectedParts ?? [], BuildScriptsPanelView(),
                ActiveDocument?.Reference.AssetPath);
        }

        RenderGuiPanel(dpiScale);

        RenderPhysicsPanel(dpiScale);
        RenderAnimationPanel(dpiScale);
        RenderEnvironmentPanel(dpiScale);
        _toolbar.RenderMenu(_width, _height, dpiScale, toolbar, _lastMouseX, _lastMouseY);
        _confirm.Render(_width, _height, dpiScale);
        _toast.Render(_width, _height, dpiScale);
    }

    public void OnResize(int width, int height)
    {
        _width = global::System.Math.Max(width, 1);
        _height = global::System.Math.Max(height, 1);
    }

    // Framebuffer size is in real pixels (2x logical on Retina/HiDPI). Only the GL viewport
    // uses this; aspect ratio, UI layout, mouse hit-testing, MouseRay and ScreenProjection
    // stay on the logical _width/_height.
    public void OnFramebufferResize(int width, int height)
    {
        _fbWidth = global::System.Math.Max(width, 1);
        _fbHeight = global::System.Math.Max(height, 1);
    }

    public void Shutdown()
    {
        if (_backend == RenderBackendKind.Bgfx)
        {
            ShutdownBgfx();
        }
        else
        {
            ShutdownGl();
        }
    }

    private void ShutdownGl()
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
        _moveGizmo.Dispose();
        _resizeGizmo.Dispose();
        _rotateGizmo.Dispose();
        _dimensionLabels.Dispose();
        _toolbar.Dispose();
        _worldExplorer.Dispose();
        _colorPanel.Dispose();
        _toast.Dispose();
        _boxSelectionRenderer.Dispose();
        _atmosphereSky.Dispose();
        ShutdownUniversalGl();
        ShutdownLightGizmos();
        _shader?.Dispose();
        _shader = null;
        _litShader?.Dispose();
        _litShader = null;
        _environmentShader?.Dispose();
        _environmentShader = null;
        _skinnedLitShader?.Dispose();
        _skinnedLitShader = null;
        _skinnedUnlitShader?.Dispose();
        _skinnedUnlitShader = null;
        DisposeGameplayRendering();
        if (_partQuery != 0)
        {
            GL.DeleteQuery(_partQuery);
            _partQuery = 0;
            _partQueryPending = false;
        }

        // Disposed last: the UI renderers reference the shared atlas but do not own it.
        _fonts?.Dispose();
        _fonts = null;
    }

    public void Dispose() => Shutdown();

    // ---- Project session hooks (used by EngineApplication's Project Hub flow) ----

    /// <summary>UI drawing for the Project Hub and name prompt on the active backend.</summary>
    internal IUiBackend UiBackend => _uiBackend ?? throw new InvalidOperationException("Renderer is not initialized.");

    internal int LogicalWidth => _width;

    internal int LogicalHeight => _height;

    internal float UiDpiScale => DpiScale;

    /// <summary>The scripts' Lua memory limit for Play sessions (<see cref="Scripting.EditorScriptPreferences"/>).</summary>
    internal long ScriptMemoryLimitBytes
    {
        get => _playMode.ScriptMemoryLimitBytes;
        set => _playMode.ScriptMemoryLimitBytes = value;
    }

    /// <summary>Called by the project menu's Save Project (the application saves the open project).</summary>
    internal Action? SaveRequested { get; set; }

    /// <summary>Called by the project menu's Leave to Hub.</summary>
    internal Action? LeaveRequested { get; set; }

    /// <summary>Called by the project menu's Delete Project… (an open, saved project only).</summary>
    internal Action? DeleteProjectRequested { get; set; }

    /// <summary>Asks the Hub's Delete Project question over the editor; <paramref name="confirmed"/> runs only for Delete Project.</summary>
    internal void ConfirmDeleteProject(string projectName, Action confirmed)
    {
        EndAllEditorDrags();
        _confirm.Open(ProjectHubRenderer.DeleteProjectRequest(projectName, confirmed));
    }

    /// <summary>Asks a question over the editor (Wisko's modal dialog; Escape picks the last button).</summary>
    internal void Ask(ConfirmRequest request)
    {
        EndAllEditorDrags();
        _confirm.Open(request);
    }

    /// <summary>Called by Import Model (Add Meshes or the project menu); the project session picks the file and imports it.</summary>
    internal Action? ImportRequested { get; set; }

    /// <summary>"+ New Script" in the Scripts panel, for the object it lists; the project session names and creates it.</summary>
    internal Action<Part>? NewScriptRequested { get; set; }

    /// <summary>"+ Attach Script" in the Scripts panel, for the object it lists; the project session picks the file.</summary>
    internal Action<Part>? AttachScriptRequested { get; set; }

    /// <summary>The editor mode: Edit, or Playing (scripts running on an in-memory copy of the edit scene's state).</summary>
    internal EditorMode Mode => _playMode.Mode;

    internal bool IsPlaying => _playMode.IsPlaying;

    /// <summary>
    /// Leaves Play Mode and restores the edit scene exactly as it was before Play (object state, order, selection).
    /// Does nothing in Edit mode. Every path that leaves the editor or saves goes through this first.
    /// </summary>
    internal void StopPlay(bool showToast)
    {
        if (!_playMode.IsPlaying || _workspace is null || _selection is null)
        {
            return;
        }

        var result = _playMode.Stop(_workspace);
        _playCamera?.End();
        if (result.MembershipChanged)
        {
            _partRenderer.Sync(_workspace);
        }

        foreach (var part in result.MeshChanged)
        {
            _partRenderer.InvalidatePart(part);
        }

        _selection.SelectMany(result.Selection);
        SyncPartRendererSelection();
        ResetGizmoHovers();
        _pendingScriptMessages.Clear();
        _playStartedThisFrame = false;
        OnPlayStopped();
        if (showToast)
        {
            _toast.Show("Stopped — scene restored");
        }
        else
        {
            _toast.Hide(); // no stale "Playing" message once Play is over
        }
    }

    /// <summary>
    /// Attaches a script reference to an object as one undoable step. False (with a reason) in Play Mode, for an
    /// object no longer in the scene, or when the object already has that script.
    /// </summary>
    internal bool TryAttachScript(Part part, ScriptReference reference, out string? reason)
    {
        reason = null;
        if (_playMode.IsPlaying)
        {
            reason = "Stop Play Mode to change scripts.";
            return false;
        }

        if (_workspace is null || !_workspace.Parts.Contains(part))
        {
            reason = "That object is no longer in the scene";
            return false;
        }

        if (part.Scripts.Any(existing => string.Equals(existing.AssetPath, reference.AssetPath, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"{part.Name} already has {ScriptLabel(reference)}";
            return false;
        }

        var before = PartSnapshot.FromPart(part);
        part.Scripts.Add(reference);
        _editorHistory.Push(EditorCommand.ScriptsChange(part, before, PartSnapshot.FromPart(part)));

        // The World list shows the new script under its object at once.
        _worldExplorer.SetExpanded(part, true);
        Console.WriteLine($"[Scripts] Attached {reference.AssetPath} to {part.Name}");
        return true;
    }

    /// <summary>Display label of a script reference: its path inside Assets/Scripts (usually just the file name).</summary>
    internal static string ScriptLabel(ScriptReference reference) =>
        reference.AssetPath.StartsWith(ProjectAssetPaths.ScriptsFolder + "/", StringComparison.Ordinal)
            ? reference.AssetPath[(ProjectAssetPaths.ScriptsFolder.Length + 1)..]
            : reference.AssetPath;

    internal void ShowToast(string message) => _toast.Show(message);

    internal void ShowToast(string message, float durationSeconds) => _toast.Show(message, durationSeconds);

    /// <summary>
    /// Adds one instance of an imported model in front of the camera, selected, recorded like any other added object
    /// (undo removes the instance; the asset files stay in the project).
    /// </summary>
    internal Part? AddModelInstance(ModelAsset asset, string baseName)
    {
        if (_workspace is null || _selection is null)
        {
            return null;
        }

        EndAllEditorDrags();
        var part = PartSpawner.CreateModelInFrontOfCamera(_workspace, _camera, asset, baseName);
        _workspace.AddPart(part);
        _partRenderer.RegisterPart(part);
        _selection.Select(part);
        SyncPartRendererSelection();
        _toolState.SetTool(EditorToolMode.Select);
        _editorHistory.Push(EditorCommand.Add(part));

        Console.WriteLine(
            $"[AddModel] Created {part.Name} ({asset.AssetPath}) centre=({part.Position.X:F2}, {part.Position.Y:F2}, {part.Position.Z:F2}) size=({part.Size.X:F2}, {part.Size.Y:F2}, {part.Size.Z:F2})");
        return part;
    }

    /// <summary>
    /// Replaces the whole workspace and drops every piece of per-session editor state (drags, keyboard
    /// move, box selection, rename/context menu, selection, undo history). Optionally resets the camera.
    /// <paramref name="removedEnvironment"/>: the host environment objects (the baseplate) this world deleted; none for a new world.
    /// </summary>
    internal void ReplaceWorkspace(IReadOnlyList<Part> parts, bool resetCamera, IReadOnlyCollection<string>? removedEnvironment = null)
    {
        if (_workspace is null || _selection is null)
        {
            return;
        }

        StopPlay(showToast: false);
        CancelPhysicsEdits();
        CancelRotationEdit();
        ResetGuiEditor();
        _activeTab = DocumentTabHit.SceneView;
        CloseCompletion();
        CloseFind();
        _output.Clear();
        EndAllEditorDrags();
        FinalizeKeyboardMove();
        _boxSelection.Cancel();
        while (_worldExplorer.TryConsumeEscape())
        {
        }

        ResetEditorLayout();

        _workspace.Clear();
        foreach (var part in parts)
        {
            _workspace.AddPart(part);
        }

        _workspace.SetRemovedEnvironment(removedEnvironment ?? []);
        _worldExplorer.SelectEnvironment(-1);
        SyncEnvironmentList();
        _partRenderer.Sync(_workspace);
        _universal?.ResetProject(); // the previous project's meshes and textures go
        _selection.Select(null);
        SyncPartRendererSelection();
        _editorHistory.Clear();
        ResetGizmoHovers();

        if (resetCamera)
        {
            _camera.ResetView();
        }
    }

    /// <summary>
    /// Renders the 3D world only (Atmosphere sky, environment, lit parts; no outlines, gizmos or UI) into the back
    /// buffer, reads it back and returns a <paramref name="width"/> x <paramref name="height"/> top-down
    /// RGBA thumbnail. The next regular Render() clears and redraws the frame, so nothing is presented.
    /// </summary>
    internal bool TryCaptureViewportThumbnail(int width, int height, out byte[] rgba) =>
        _backend == RenderBackendKind.Bgfx
            ? TryCaptureViewportThumbnailBgfx(width, height, out rgba)
            : TryCaptureViewportThumbnailGl(width, height, out rgba);

    private bool TryCaptureViewportThumbnailGl(int width, int height, out byte[] rgba)
    {
        rgba = [];
        if (_shader is null || _litShader is null || _fbWidth <= 0 || _fbHeight <= 0)
        {
            return false;
        }

        GL.Viewport(0, 0, _fbWidth, _fbHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

        var aspect = _width / (float)global::System.Math.Max(_height, 1);
        var universal = BeginUniversalFrame();
        RenderWorldGl(_camera.ViewMatrix, _camera.GetProjectionMatrix(aspect), aspect);
        if (universal)
        {
            EndUniversalFrame();
        }

        var pixels = new byte[_fbWidth * _fbHeight * 4];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, _fbWidth, _fbHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GlDebug.CheckError("viewport thumbnail capture");

        rgba = ImageResample.CropAndDownscaleFromFramebuffer(pixels, _fbWidth, _fbHeight, width, height);
        return true;
    }

    /// <summary>Viewport + clear for a frame that draws only screen-space UI (the Project Hub).</summary>
    internal void BeginScreenUiFrame()
    {
        if (_backend == RenderBackendKind.Bgfx)
        {
            BeginScreenUiFrameBgfx();
        }
        else
        {
            BeginScreenUiFrameGl();
        }
    }

    private void BeginScreenUiFrameGl()
    {
        GL.Viewport(0, 0, _fbWidth, _fbHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
    }

    private static int[] ComputePrewarmBuckets(float dpiScale)
    {
        var buckets = new SortedSet<int>();
        foreach (var scale in PrewarmScales)
        {
            buckets.Add(TextLayout.DeviceSizeBucket(scale, dpiScale));
        }

        return buckets.ToArray();
    }

    public bool TryConsumeEscape()
    {
        // A modal question: Escape is its Cancel.
        if (_confirm.IsOpen)
        {
            _confirm.Cancel();
            return true;
        }

        // An open toolbar menu (ToolBench, Add Meshes, project) closes without doing anything.
        if (_toolbar.IsMenuOpen)
        {
            _toolbar.CloseMenu();
            return true;
        }

        // A Part being dragged by its body goes back where it started; nothing is recorded.
        if (_partDragger.CancelDrag())
        {
            SyncPartRendererSelection();
            return true;
        }

        // In a script tab: close the completion list, then the find bar.
        if (ActiveDocument is not null)
        {
            if (_codeState.Completion is not null)
            {
                CloseCompletion();
                return true;
            }

            if (_codeState.FindOpen)
            {
                CloseFind();
                return true;
            }
        }

        // A game TextBox with the keyboard lets go of it first; the next Escape stops Play (scripts cannot refocus a
        // box for a moment after this, so they cannot keep Escape from stopping Play).
        if (_playMode.IsPlaying && _playMode.Gui?.ReleaseFocus(submitted: false, byEscape: true) == true)
        {
            Console.WriteLine("[GUI] Escape: the text box let go of the keyboard");
            return true;
        }

        // Escape always leaves Play Mode (a script cannot keep the editor playing).
        if (_playMode.IsPlaying)
        {
            Console.WriteLine("[PlayMode] Escape: stopping");
            StopPlay(showToast: true);
            return true;
        }

        // A rotate drag in progress is cancelled by Escape (restores the start rotation, records nothing).
        if (_rotateGizmoController.IsDragging && _selection?.SelectedPart is Part rotatingPart)
        {
            _rotateGizmoController.Cancel(rotatingPart);
            return true;
        }

        // A move drag in progress is cancelled by Escape (restores every dragged part, records nothing).
        if (_moveGizmoController.IsDragging)
        {
            _moveGizmoController.Cancel();
            return true;
        }

        // A resize drag in progress is cancelled by Escape (restores size + position, records nothing).
        if (_resizeGizmoController.IsDragging)
        {
            _resizeGizmoController.Cancel();
            return true;
        }

        // A UI panel value being typed is dropped; an Opacity drag goes back; a row drag stops.
        if (CancelGuiPanelEdits())
        {
            return true;
        }

        // A GUI move / resize goes back to exactly where it started (nothing recorded).
        if (CancelGuiDrag())
        {
            return true;
        }

        // A rotation being typed is dropped (nothing changes).
        if (CancelRotationEdit())
        {
            return true;
        }

        // A Physics value being typed is dropped; a Friction drag goes back to where it started.
        if (CancelPhysicsEdits())
        {
            return true;
        }

        if (_worldExplorer.TryConsumeEscape())
        {
            return true;
        }

        if (_colorPanel.TryConsumeEscape())
        {
            SyncSidePanels();
            return true;
        }

        if (TryConsumeEnvironmentEscape())
        {
            return true;
        }

        // Never quit from a script tab with a stray Escape.
        return ActiveDocument is not null;
    }

    /// <summary>Told when a Play script locks or unlocks the mouse (true: hold and hide the cursor).</summary>
    internal Action<bool>? CursorLockChanged { get; set; }

    /// <summary>A Play script has locked the mouse: the game owns the pointer until it lets go or Play stops.</summary>
    internal bool GameHoldsMouse => _playMode.IsPlaying && _playCamera?.MouseLocked == true;

    public bool ShouldBeginRightMouseLook(float mouseX, float mouseY) =>
        !GameHoldsMouse
        && ActiveDocument is null
        && !_confirm.IsOpen
        && !_toolbar.IsMenuOpen
        && !IsOverEditorChrome(mouseX, mouseY)
        && !_worldExplorer.ShouldSuppressRightMouseLook(mouseX, mouseY, _width, _height)
        && !_colorPanel.IsOverPanel(mouseX, mouseY, _width, _height);

    private void StartPlay(InputManager input)
    {
        if (_workspace is null || _selection is null || _playMode.IsPlaying)
        {
            return;
        }

        // Finish any edit in progress first, so the snapshot is the scene as the user left it. An animation preview ends
        // (the model back at rest): Play's own playback takes over.
        EndAllEditorDrags();
        StopAnimationPreview();
        CommitPhysicsEdits();
        CommitRotationEdit(keepOpenWhenInvalid: false);
        CommitGuiDrag();
        CommitGuiPanelEdits();
        FinalizeKeyboardMove();
        _boxSelection.Cancel();
        while (_worldExplorer.TryConsumeEscape())
        {
        }

        // Unsaved scripts are saved first (safely); a script that cannot be saved stops Play from starting.
        if (!SaveScriptsForPlay())
        {
            return;
        }

        _output.Add(new OutputEntry(OutputKind.Separator, $"Play started {DateTime.Now:HH:mm:ss}"));
        // Scripts reach the view camera (Camera) and start from the project's Atmosphere.
        _playCamera ??= new PlayCamera(_camera, () => (_width, _height), locked => CursorLockChanged?.Invoke(locked));
        _playCamera.Begin();
        _playMode.CameraHost = _playCamera;
        _playMode.AtmosphereSource = _atmosphere.Settings;
        _playMode.GraphicsSource = _graphicsState.Settings;
        _playMode.CameraFeedsSupported = SupportsCameraFeeds;
        if (!_playMode.TryStart(ProjectRoot, _workspace, _selection.SelectedParts, input.ScriptInput, out var error))
        {
            _playCamera.End();
            _toast.Show($"Couldn't start Play Mode: {error}", ScriptToastSeconds);
            return;
        }

        _playMode.Gui!.ButtonClicked += OnGuiButtonClicked;
        ApplyPlayMembershipChanges();
        ResetGizmoHovers();
        _playStartedThisFrame = true;
        _pendingScriptMessages.Clear();
        _scriptToastCooldown = 0f;
        _scriptToastsShown = 0;
        var scriptCount = _playMode.Runtime?.Scripts.Count ?? 0;
        _toast.Show(scriptCount == 0 ? "Playing — no scripts attached · Esc to stop" : "Playing — Esc to stop");

        // Missing files and load/Start failures are known right away.
        CollectScriptMessages();
        if (_pendingScriptMessages.Count > 0)
        {
            _scriptToastCooldown = 0.8f; // let "Playing" be seen first
        }
    }

    // One Play frame: every script's Update(dt) with this frame's input, then any new failures as toasts. While the
    // keyboard belongs to the code editor (or a dialog), scripts get no key or mouse input.
    private void UpdatePlayMode(InputManager input, float deltaTime, bool suppressScriptInput)
    {
        if (!_playMode.IsPlaying)
        {
            return;
        }

        if (_playStartedThisFrame)
        {
            // Start ran this frame; Update begins next frame.
            _playStartedThisFrame = false;
        }
        else if (!_playMode.TryUpdate(deltaTime, suppressScriptInput ? NoScriptInput : RuntimeTextBoxOwnsKeyboard ? TypingScriptInput(input) : input.ScriptInput, out var error))
        {
            StopPlay(showToast: false);
            _toast.Show($"Play stopped: script runtime error ({error})", ScriptToastSeconds);
            return;
        }

        ApplyPlayMembershipChanges();

        CollectScriptMessages();
        _scriptToastCooldown -= global::System.Math.Min(deltaTime, 0.1f);
        if (_pendingScriptMessages.Count == 0 || _scriptToastCooldown > 0f)
        {
            return;
        }

        // One problem per toast, oldest first, each saying how many are still waiting; past the cap, one last
        // pointer to the log (every problem is always in the log).
        if (_scriptToastsShown < MaxScriptToastsPerPlay)
        {
            var message = _pendingScriptMessages.Dequeue();
            var more = _pendingScriptMessages.Count;
            _toast.Show(more > 0 ? $"{message} (+{more} more)" : message, ScriptToastSeconds);
        }
        else
        {
            _toast.Show("More script errors — see the log", ScriptToastSeconds);
            _pendingScriptMessages.Clear();
        }

        _scriptToastsShown++;
        _scriptToastCooldown = ScriptToastInterval;
    }

    // Parts scripts created get drawn and destroyed ones stop being drawn (and leave the selection). A Part created and
    // destroyed since the last frame is never drawn.
    private void ApplyPlayMembershipChanges()
    {
        var changes = _playMode.TakeMembershipChanges();
        if (changes.Count == 0)
        {
            return;
        }

        var net = new Dictionary<Part, int>(ReferenceEqualityComparer.Instance);
        var order = new List<Part>();
        foreach (var (part, added) in changes)
        {
            if (!net.TryGetValue(part, out var count))
            {
                order.Add(part);
            }

            net[part] = count + (added ? 1 : -1);
        }

        var removedSelected = false;
        foreach (var part in order)
        {
            switch (net[part])
            {
                case > 0:
                    _partRenderer.RegisterPart(part);
                    break;
                case < 0:
                    _partRenderer.UnregisterPart(part);
                    removedSelected |= _selection?.SelectedParts.Contains(part) == true;
                    break;
            }
        }

        if (removedSelected && _selection is not null)
        {
            _selection.SelectMany(_selection.SelectedParts.Where(part => net.GetValueOrDefault(part) >= 0).ToList());
            SyncPartRendererSelection();
        }
    }

    private void CollectScriptMessages()
    {
        // Past the toast cap nothing more is shown (the log has everything): stop queueing.
        var queueing = _scriptToastsShown <= MaxScriptToastsPerPlay;
        foreach (var fault in _playMode.TakeNewFaults())
        {
            if (queueing)
            {
                _pendingScriptMessages.Enqueue(FormatFault(fault));
            }
        }

        foreach (var warning in _playMode.TakeWarnings())
        {
            if (queueing)
            {
                _pendingScriptMessages.Enqueue(Shorten($"{Path.GetFileName(warning.ScriptName)}: {warning.Text}"));
            }
        }
    }

    // "Spin.lua line 8: attempt to perform arithmetic on a nil value (global 'x')"; the full detail is in the log.
    private static string FormatFault(PlayScriptFault fault)
    {
        var diagnostic = fault.Diagnostic;
        var file = Path.GetFileName(fault.Script.Reference.AssetPath);
        var where = diagnostic.Line is int line ? $"{file} line {line}" : file;
        var message = diagnostic.Kind switch
        {
            ScriptDiagnosticKind.MissingScript => "script file not found",
            ScriptDiagnosticKind.InstructionBudgetExceeded => "stopped, it ran too long (endless loop?)",
            ScriptDiagnosticKind.MemoryLimitExceeded => "stopped, it used more Lua memory than allowed",
            _ => diagnostic.Message
        };
        return Shorten($"{where}: {message}");
    }

    private static string Shorten(string message) =>
        message.Length <= MaxScriptToastLength ? message : message[..(MaxScriptToastLength - 3)].TrimEnd() + "...";

    private void ResetGizmoHovers()
    {
        _moveHover = GizmoAxis.None;
        _resizeHover = ResizeHandle.None;
        _resizeCornerHover = ResizeCornerHandle.None;
        _rotateHover = RotateHandle.None;
    }

    // The Scripts section's content for the current selection (Play: each script's run state; Edit: file present?).
    private ScriptsPanelView BuildScriptsPanelView()
    {
        if (_selection is null || _selection.SelectedParts.Count == 0)
        {
            return _worldExplorer.SelectedEnvironment is { } environment ? EnvironmentPanelView(environment) : ScriptsPanelView.Empty;
        }

        if (_selection.SelectedParts.Count > 1)
        {
            return new ScriptsPanelView(null, "Select one object to see its scripts", [], _playMode.IsPlaying) { Rotation = BuildRotationView() };
        }

        var part = _selection.SelectedParts[0];
        var running = _playMode.IsPlaying ? _playMode.ScriptsOf(part).ToList() : null;
        var rows = new ScriptRowView[part.Scripts.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var reference = part.Scripts[i];
            ScriptRowStatus status;
            if (running is not null)
            {
                status = i < running.Count && running[i].State != ScriptInstanceState.Faulted
                    ? ScriptRowStatus.Running
                    : ScriptRowStatus.Faulted;
            }
            else
            {
                status = ProjectRoot is not null
                         && ProjectAssetPaths.TryResolve(ProjectRoot, reference.AssetPath, out var fullPath)
                         && File.Exists(fullPath)
                    ? ScriptRowStatus.Attached
                    : ScriptRowStatus.Missing;
            }

            rows[i] = new ScriptRowView(ScriptLabel(reference), status);
        }

        return new ScriptsPanelView(part, null, rows, _playMode.IsPlaying) { Rotation = BuildRotationView() };
    }

    // The selected Baseplate's facts (the box the editor draws and Play Mode collides with); it can only be deleted.
    private static ScriptsPanelView EnvironmentPanelView(Physics.PhysicsEnvironmentBox environment)
    {
        var culture = global::System.Globalization.CultureInfo.InvariantCulture;
        string Number(float value) => value.ToString("0.##", culture);
        var size = environment.Size;
        var center = environment.Center;
        return new ScriptsPanelView(null, null, [], readOnly: true)
        {
            Environment = new EnvironmentDetailsView(environment.Name,
            [
                $"Size {Number(size.X)} x {Number(size.Y)} x {Number(size.Z)}",
                $"Top surface at Y {Number(center.Y + size.Y * 0.5f)}",
                $"Centre {Number(center.X)}, {Number(center.Y)}, {Number(center.Z)}",
                "Solid collider (the same box)",
                $"Delete removes it ({ShortcutConvention.Current.PrimaryLabel}+Z undoes)"
            ])
        };
    }

    // Removing a script reference never deletes the script file.
    private void DetachScript(Part part, int index)
    {
        if (index < 0 || index >= part.Scripts.Count)
        {
            return;
        }

        var before = PartSnapshot.FromPart(part);
        var reference = part.Scripts[index];
        part.Scripts.RemoveAt(index);
        _editorHistory.Push(EditorCommand.ScriptsChange(part, before, PartSnapshot.FromPart(part)));
        Console.WriteLine($"[Scripts] Detached {reference.AssetPath} from {part.Name} (file kept)");
        _toast.Show($"Removed {ScriptLabel(reference)} from {part.Name} (file kept)");
    }

    private void HandleSaveScene() => SaveRequested?.Invoke();

    // Save Project from the project menu or its keyboard shortcut. Play Mode changes are temporary: only the edit scene
    // is ever saved.
    private void RequestSaveProject()
    {
        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode before saving.");
            return;
        }

        HandleSaveScene();
    }

    private void HandleAddShape()
    {
        if (_workspace is null || _selection is null)
        {
            return;
        }

        var shape = _shapeState.SelectedShape;
        var part = PartSpawner.CreateInFrontOfCamera(_workspace, _camera, shape);
        _workspace.AddPart(part);
        _partRenderer.RegisterPart(part);
        _selection.Select(part);
        SyncPartRendererSelection();
        _toolState.SetTool(EditorToolMode.Select);
        _editorHistory.Push(EditorCommand.Add(part));

        Console.WriteLine(
            $"[AddPart] Created {part.Name} ({shape}) at ({part.Position.X:F2}, {part.Position.Y:F2}, {part.Position.Z:F2})");
    }

    private void HandleDeleteSelectedPart(InputManager input)
    {
        // The UI section: Delete removes the selected GUI element (its subtree), never a Part.
        if (_toolBench.Current == ToolBenchSection.UI && input.DeleteSelectedPartPressed && !_worldExplorer.IsRenaming && !PhysicsPanelOwnsKeyboard)
        {
            DeleteGuiSelection();
            return;
        }

        if (_worldExplorer.IsRenaming
            || PhysicsPanelOwnsKeyboard
            || _toolBench.Current == ToolBenchSection.UI
            || !input.DeleteSelectedPartPressed
            || _workspace is null
            || _selection is null)
        {
            return;
        }

        var selectedParts = _selection.SelectedParts.ToArray();
        if (selectedParts.Length == 0)
        {
            // The baseplate selected (its World panel row or Ctrl+click): Delete deletes it like any object.
            if (_worldExplorer.SelectedEnvironment is { } environment)
            {
                DeleteEnvironmentObject(environment.Name);
            }

            return;
        }

        DeleteSelectedParts();
    }

    // The Delete key: every selected Part goes (refused in Play or when one is locked).
    private void DeleteSelectedParts()
    {
        if (_workspace is null || _selection is null || _selection.SelectedParts.Count == 0)
        {
            return;
        }

        var selectedParts = _selection.SelectedParts.ToArray();
        if (_playMode.IsPlaying)
        {
            _toast.Show(PlayBlockedMessage);
            return;
        }

        if (SelectionHasLocked())
        {
            RefuseLockedEdit();
            return;
        }

        foreach (var selected in selectedParts)
        {
            _editorHistory.Push(EditorCommand.Delete(selected));
            _workspace.RemovePart(selected);
            _partRenderer.UnregisterPart(selected);
        }

        _selection.Select(null);
        SyncPartRendererSelection();
        EndAllEditorDrags();

        Console.WriteLine(
            selectedParts.Length == 1
                ? $"[DeletePart] Deleted {selectedParts[0].Name}"
                : $"[DeletePart] Deleted {selectedParts.Length} parts");
    }

    private void HandleWorldExplorer(InputManager input)
    {
        if (_workspace is null || _selection is null)
        {
            return;
        }

        if (_worldExplorer.HandleRenameKeyboard(input))
        {
            _toolbarConsumedClick = true;
            return;
        }

        // Mouse wheel over the Workspace list scrolls it (not the camera).
        if (!_wheelConsumed && input.ScrollDelta != 0f
            && _worldExplorer.IsOverList(input.MouseX, input.MouseY, _width, _height, BuildScriptsPanelView()))
        {
            _worldExplorer.ScrollList(-input.ScrollDelta * 40f, _width, _height, BuildScriptsPanelView(), _workspace);
            _wheelConsumed = true;
        }

        // A press on a row that may become a drag (or, on release, a deferred click): follow it until the button is up.
        if (_explorerPress is { } press)
        {
            HandleExplorerPress(input, press);
            return;
        }

        // A click the toolbar (or its open menu) already took is not for the sidebar.
        if (_toolbarConsumedClick)
        {
            return;
        }

        if (input.RightMousePressed)
        {
            OpenExplorerContextMenu(input);
            return;
        }

        if (!input.LeftMousePressed)
        {
            return;
        }

        if (_worldExplorer.TryHitContextMenu(input.MouseX, input.MouseY, out var menuItem))
        {
            _toolbarConsumedClick = true;
            RunExplorerCommand(menuItem);
            return;
        }

        if (_worldExplorer.IsContextMenuOpen && !_worldExplorer.IsOverContextMenu(input.MouseX, input.MouseY))
        {
            _worldExplorer.CloseContextMenu();
        }

        if (!_worldExplorer.IsOverSidebar(input.MouseX, input.MouseY, _width, _height))
        {
            return;
        }

        _toolbarConsumedClick = true;

        var scripts = BuildScriptsPanelView();
        if (_worldExplorer.TryHitScriptsPanel(input.MouseX, input.MouseY, _width, _height, scripts, out var scriptsHit))
        {
            if (scriptsHit.Action == ScriptsPanelAction.RotationField)
            {
                BeginRotationEdit(scriptsHit.ScriptIndex);
                return;
            }

            if (scriptsHit.Action == ScriptsPanelAction.None || scripts.Target is not { } scriptTarget)
            {
                return;
            }

            // Opening a script (double-click its row) is fine while playing: only attachments are read-only.
            if (scriptsHit.Action == ScriptsPanelAction.Row)
            {
                HandleScriptRowClick(scriptTarget, scriptsHit.ScriptIndex);
                return;
            }

            if (_playMode.IsPlaying)
            {
                _toast.Show("Stop Play Mode to change scripts.");
                return;
            }

            Console.WriteLine($"[Scripts] {scriptsHit.Action} for {scriptTarget.Name}");
            switch (scriptsHit.Action)
            {
                case ScriptsPanelAction.NewScript:
                    NewScriptRequested?.Invoke(scriptTarget);
                    break;
                case ScriptsPanelAction.AttachScript:
                    AttachScriptRequested?.Invoke(scriptTarget);
                    break;
                case ScriptsPanelAction.Detach:
                    DetachScript(scriptTarget, scriptsHit.ScriptIndex);
                    break;
            }

            return;
        }

        if (!_worldExplorer.TryHitRow(input.MouseX, input.MouseY, _width, _height, _workspace, scripts, out var row))
        {
            return;
        }

        switch (row.Kind)
        {
            case ExplorerRowKind.Environment:
                // The Baseplate: selected for its details only. No Part is selected, so the tools, Delete, Rename and
                // the colour panel have nothing to act on.
                _selection.Select(null);
                _worldExplorer.SelectedGroup = null;
                SyncPartRendererSelection();
                EndAllEditorDrags();
                _worldExplorer.SelectEnvironment(row.EnvironmentIndex);
                Console.WriteLine($"[WorldExplorer] Selected {_worldExplorer.SelectedEnvironment?.Name} (environment, read-only)");
                break;
            case ExplorerRowKind.Disclosure when row.Part is { } disclosed:
                // Only shows or hides the Part's scripts: the selection, tabs and scene stay as they are.
                _worldExplorer.SetExpanded(disclosed, !_worldExplorer.IsExpanded(disclosed));
                Console.WriteLine($"[WorldExplorer] {(_worldExplorer.IsExpanded(disclosed) ? "Expanded" : "Collapsed")} {disclosed.Name}");
                break;
            case ExplorerRowKind.GroupDisclosure when row.Group is { } folded:
                // Only shows or hides the group's contents.
                _worldExplorer.SetGroupExpanded(folded, !_worldExplorer.IsGroupExpanded(folded));
                Console.WriteLine($"[WorldExplorer] {(_worldExplorer.IsGroupExpanded(folded) ? "Expanded" : "Collapsed")} group {folded.Name}");
                break;
            case ExplorerRowKind.Script when row.Part is { } owner:
                // Opens it in the code editor (fine while playing too: only attachments are read-only there).
                if (row.ScriptIndex < owner.Scripts.Count)
                {
                    var reference = owner.Scripts[row.ScriptIndex];
                    Console.WriteLine($"[WorldExplorer] Open {reference.AssetPath} (on {owner.Name})");
                    OpenScript(reference);
                }

                break;
            case ExplorerRowKind.Group when row.Group is { } group:
                // A group row selects everything in it (so the Move tool moves the group as a unit) and can be dragged.
                SelectGroup(group);
                _explorerPress = new ExplorerPress(input.MouseX, input.MouseY, null, group, null);
                break;
            case ExplorerRowKind.Part when row.Part is { } clicked:
                _explorerPress = new ExplorerPress(input.MouseX, input.MouseY, clicked, null, ClickPartRow(clicked, input));
                break;
        }
    }

    // A pressed World panel row: where the press began, what was pressed, and (for a plain click on a Part already in a
    // larger selection) the Part to select alone if the press ends without a drag.
    private sealed record ExplorerPress(float X, float Y, Part? Part, PartGroup? Group, Part? SelectOnRelease);

    private ExplorerPress? _explorerPress;
    private Part? _explorerAnchor;
    private PartGroup? _explorerContextGroup;

    // Selection from a Part row click: Ctrl (or Command on macOS) toggles it, Shift selects the visible range from the last clicked Part, a
    // plain click selects it alone (unless it is already part of a larger selection: then that stays, so it can be
    // dragged as a whole, and it is selected alone on release). Returns the Part to select on release, if any.
    private Part? ClickPartRow(Part clicked, InputManager input)
    {
        _worldExplorer.SelectEnvironment(-1);
        _worldExplorer.SelectedGroup = null;
        EndAllEditorDrags();
        var current = _selection!.SelectedParts;
        Part? deferred = null;
        if (input.SelectionToggleHeld)
        {
            var toggled = current.ToList();
            if (!toggled.Remove(clicked))
            {
                toggled.Add(clicked);
            }

            _selection.SelectMany(toggled);
            _explorerAnchor = clicked;
        }
        else if (input.ShiftHeld && _explorerAnchor is { } anchor && _workspace!.Parts.Contains(anchor))
        {
            _selection.SelectMany(_worldExplorer.PartsBetween(anchor, clicked, _workspace));
        }
        else if (current.Count > 1 && current.Contains(clicked))
        {
            deferred = clicked;
            _explorerAnchor = clicked;
        }
        else
        {
            _selection.Select(clicked);
            _explorerAnchor = clicked;
            Console.WriteLine($"[WorldExplorer] Selected {clicked.Name}");
        }

        SyncPartRendererSelection();
        return deferred;
    }

    private void SelectGroup(PartGroup group)
    {
        _worldExplorer.SelectEnvironment(-1);
        _worldExplorer.SelectedGroup = group;
        _selection!.SelectMany(PartGroups.PartsIn(_workspace!.Parts, group));
        SyncPartRendererSelection();
        EndAllEditorDrags();
        Console.WriteLine($"[WorldExplorer] Selected group {group.Name}");
    }

    // A held press on a row: past a few pixels it becomes a drag (not while playing); on release a drag drops its rows
    // where they hover (into a group, a Part's group, or the Workspace below the list), a plain press finishes its click.
    private void HandleExplorerPress(InputManager input, ExplorerPress press)
    {
        _toolbarConsumedClick = true;
        var scripts = BuildScriptsPanelView();
        if (input.IsLeftMouseHeld)
        {
            var moved = MathF.Abs(input.MouseX - press.X) + MathF.Abs(input.MouseY - press.Y) > 6f;
            if (!_worldExplorer.IsDragging && moved && !_playMode.IsPlaying)
            {
                var count = press.Group is null ? _selection!.SelectedParts.Count : 0;
                _worldExplorer.BeginDrag(press.Group, press.Group is { } group ? group.Name : count == 1 ? _selection!.SelectedParts[0].Name : $"{count} objects");
            }

            if (_worldExplorer.IsDragging)
            {
                _worldExplorer.UpdateDrag(input.MouseX, input.MouseY, _width, _height, _workspace!, scripts);
            }

            return;
        }

        _explorerPress = null;
        if (_worldExplorer.IsDragging)
        {
            _worldExplorer.UpdateDrag(input.MouseX, input.MouseY, _width, _height, _workspace!, scripts);
            var droppable = _worldExplorer.TryGetDropTarget(out var target);
            _worldExplorer.EndDrag();
            if (!droppable)
            {
                return;
            }

            string? error;
            var command = press.Group is { } dragged
                ? WorldGrouping.MoveGroup(_workspace!, dragged, target, _selection!.SelectedParts, out error)
                : WorldGrouping.MoveParts(_workspace!, _selection!.SelectedParts, target, _selection.SelectedParts, out error);
            FinishOrganize(command, error, target);
            return;
        }

        if (press.SelectOnRelease is { } single)
        {
            _selection!.Select(single);
            SyncPartRendererSelection();
            Console.WriteLine($"[WorldExplorer] Selected {single.Name}");
        }
    }

    // Right-click: a Part row (selected first when it is not in the selection) or a group row offers what applies.
    private void OpenExplorerContextMenu(InputManager input)
    {
        if (!_worldExplorer.IsVisible
            || !_worldExplorer.TryHitRow(input.MouseX, input.MouseY, _width, _height, _workspace!, BuildScriptsPanelView(), out var hit))
        {
            if (_worldExplorer.IsContextMenuOpen)
            {
                _worldExplorer.CloseContextMenu();
            }

            return;
        }

        List<string> items;
        _explorerContextEnvironment = null;
        if (hit.Kind is ExplorerRowKind.Group or ExplorerRowKind.GroupDisclosure && hit.Group is { } group)
        {
            SelectGroup(group);
            _explorerContextGroup = group;
            items = ["Rename", "Group", "Ungroup"];
        }
        else if (hit.Kind is ExplorerRowKind.Part or ExplorerRowKind.Disclosure && hit.Part is { } part)
        {
            if (!_selection!.SelectedParts.Contains(part))
            {
                ClickPartRow(part, input);
            }

            _explorerContextGroup = null;
            var selected = _selection.SelectedParts;
            items = selected.Count == 1 ? ["Rename", "Group"] : ["Group"];
            if (selected.Any(p => p.Group is not null))
            {
                items.Add("Remove from Group");
            }
        }
        else if (hit.Kind == ExplorerRowKind.Environment && hit.EnvironmentIndex >= 0 && hit.EnvironmentIndex < _worldExplorer.Environment.Count)
        {
            // The baseplate's row: selected (no Part), and deletable like any object.
            _selection!.Select(null);
            _worldExplorer.SelectedGroup = null;
            SyncPartRendererSelection();
            _worldExplorer.SelectEnvironment(hit.EnvironmentIndex);
            _explorerContextGroup = null;
            _explorerContextEnvironment = _worldExplorer.Environment[hit.EnvironmentIndex].Name;
            items = ["Delete"];
        }
        else
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show("Stop Play Mode to rename, organise or delete objects.");
            return;
        }

        _worldExplorer.OpenContextMenu(items, input.MouseX, input.MouseY, _width, _height);
        Console.WriteLine($"[WorldExplorer] Context menu: {string.Join(", ", items)}");
    }

    // A context menu item or shortcut: Rename, Group, Ungroup, Remove from Group, Delete (the baseplate's row).
    private void RunExplorerCommand(string item)
    {
        if (_workspace is null || _selection is null || _playMode.IsPlaying)
        {
            return;
        }

        // The baseplate row's Delete (Parts are deleted with the Delete key, as before).
        if (item == "Delete")
        {
            if (_explorerContextEnvironment is { } environment)
            {
                DeleteEnvironmentObject(environment);
            }

            _explorerContextEnvironment = null;
            return;
        }

        var group = _explorerContextGroup;
        string? error = null;
        EditorCommand? command = null;
        PartGroup? reveal = null;
        switch (item)
        {
            case "Rename" when group is not null:
                _worldExplorer.BeginRenameGroup(group);
                return;
            case "Rename" when _selection.SelectedParts.Count == 1:
                _worldExplorer.BeginRename(_selection.SelectedParts[0]);
                return;
            case "Group" when group is not null:
                // The group goes inside a new group where it was.
                var outer = new PartGroup(PartGroups.UniqueName(_workspace.Parts, group.Parent), group.Parent);
                command = WorldGrouping.MoveGroup(_workspace, group, outer, _selection.SelectedParts, out error);
                reveal = outer;
                break;
            case "Group":
                command = WorldGrouping.Group(_workspace, _selection.SelectedParts, _selection.SelectedParts, out var created, out error);
                reveal = created;
                break;
            case "Ungroup" when group is not null:
                command = WorldGrouping.Ungroup(_workspace, group, _selection.SelectedParts, out error);
                break;
            case "Remove from Group":
                command = WorldGrouping.MoveParts(_workspace, _selection.SelectedParts, null, _selection.SelectedParts, out error);
                break;
            default:
                return;
        }

        FinishOrganize(command, error, reveal);
    }

    // Records an organisation step (or shows why there was none) and opens the group it concerns.
    private void FinishOrganize(EditorCommand? command, string? error, PartGroup? reveal)
    {
        if (command is null)
        {
            if (error is not null)
            {
                _toast.Show(error);
            }

            return;
        }

        _editorHistory.Push(command);
        for (var group = reveal; group is not null; group = group.Parent)
        {
            _worldExplorer.SetGroupExpanded(group, true);
        }

        _worldExplorer.SelectedGroup = null;
        _explorerContextGroup = null;
    }

    // Ctrl+G / Ctrl+Shift+G in the 3D view: group the selection / dissolve the selected group.
    private void HandleGroupShortcuts(InputManager input)
    {
        if (_playMode.IsPlaying || _worldExplorer.IsRenaming || ActiveDocument is not null || _selection is null)
        {
            return;
        }

        if (input.GroupPressed && _selection.SelectedParts.Count > 0)
        {
            _explorerContextGroup = null;
            RunExplorerCommand("Group");
        }
        else if (input.UngroupPressed && _worldExplorer.SelectedGroup is { } group)
        {
            _explorerContextGroup = group;
            RunExplorerCommand("Ungroup");
        }
    }

    private void HandleColorPanel(InputManager input)
    {
        if (_selection is null || !_colorPanel.IsVisible || !input.LeftMousePressed || _toolbarConsumedClick)
        {
            return;
        }

        if (!_colorPanel.IsOverPanel(input.MouseX, input.MouseY, _width, _height))
        {
            return;
        }

        _toolbarConsumedClick = true;

        if (_selection.SelectedParts.Count == 0)
        {
            return;
        }

        if (!_colorPanel.TryHitColor(input.MouseX, input.MouseY, _width, _height, out var paletteColor))
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(PlayBlockedMessage);
            return;
        }

        foreach (var selected in _selection.SelectedParts.ToArray())
        {
            var beforeColor = PartSnapshot.FromPart(selected);
            selected.Color = paletteColor.Rgb;
            var afterColor = PartSnapshot.FromPart(selected);
            _editorHistory.Push(EditorCommand.ColorChange(selected, beforeColor, afterColor));
            _partRenderer.InvalidatePart(selected);
        }

        SyncPartRendererSelection();
        Console.WriteLine(
            _selection.SelectedParts.Count == 1
                ? $"[ColorPart] {_selection.SelectedPart!.Name} -> {paletteColor.Name}"
                : $"[ColorPart] {_selection.SelectedParts.Count} parts -> {paletteColor.Name}");
    }

    // Ctrl+S (Command+S on macOS) in the 3D View: Save Project, exactly as the project menu's Save Project. Not while a
    // name, value or game text box has the keyboard (a script tab handles its own Ctrl/Command+S: Update never calls this
    // there), and not in Play: the keys belong to the game then (a game may use Ctrl with S), silently, as R does.
    private void HandleSaveShortcut(InputManager input)
    {
        if (!input.SaveProjectPressed || _playMode.IsPlaying || _worldExplorer.IsRenaming || PhysicsPanelOwnsKeyboard)
        {
            return;
        }

        RequestSaveProject();
    }

    private void HandleRotateSelectedPart(InputManager input)
    {
        // R is a common game key: while playing it belongs to the scripts, silently.
        if (!input.RotatePartPressed
            || _worldExplorer.IsRenaming
            || PhysicsPanelOwnsKeyboard
            || _toolBench.Current == ToolBenchSection.UI
            || _playMode.IsPlaying
            || _toolState.ActiveTool != EditorToolMode.Rotate
            || _selection?.SelectedPart is not Part part)
        {
            return;
        }

        if (SelectionHasLocked())
        {
            RefuseLockedEdit();
            return;
        }

        var beforeRotate = PartSnapshot.FromPart(part);
        PartRotator.RotateAroundY(part, input.RotatePartDeltaY);
        var afterRotate = PartSnapshot.FromPart(part);
        _editorHistory.Push(EditorCommand.Rotate(part, beforeRotate, afterRotate));
        var sign = input.RotatePartDeltaY >= 0f ? "+" : string.Empty;
        Console.WriteLine($"[RotatePart] {part.Name} -> Y {sign}{input.RotatePartDeltaY:0}°");
    }

    private void HandleDuplicateSelectedPart(InputManager input)
    {
        if (!input.DuplicateSelectedPartPressed || _worldExplorer.IsRenaming || PhysicsPanelOwnsKeyboard || _workspace is null || _selection is null)
        {
            return;
        }

        // The UI section: Ctrl+D duplicates the selected GUI element, never a Part.
        if (_toolBench.Current == ToolBenchSection.UI)
        {
            DuplicateGuiSelection();
            return;
        }

        var selectedParts = _selection.SelectedParts.ToArray();
        if (selectedParts.Length == 0)
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(PlayBlockedMessage);
            return;
        }

        var duplicates = new List<Part>(selectedParts.Length);
        foreach (var selected in selectedParts)
        {
            var duplicate = PartDuplicator.Duplicate(selected, _workspace);
            _workspace.AddPart(duplicate);
            _partRenderer.RegisterPart(duplicate);
            _editorHistory.Push(EditorCommand.Duplicate(selected, duplicate));
            duplicates.Add(duplicate);
        }

        _selection.SelectMany(duplicates);
        SyncPartRendererSelection();
        EndAllEditorDrags();

        Console.WriteLine(
            selectedParts.Length == 1
                ? $"[DuplicatePart] Duplicated {selectedParts[0].Name} -> {duplicates[0].Name}"
                : $"[DuplicatePart] Duplicated {selectedParts.Length} parts");
    }

    private void HandleViewportInput(InputManager input, float deltaTime)
    {
        if (_workspace is null || _selection is null)
        {
            return;
        }

        if (_toolbarConsumedClick)
        {
            return;
        }

        if (_toolbar.IsOverToolbarUi(input.MouseX, input.MouseY, _width))
        {
            return;
        }

        if (_worldExplorer.BlocksViewportInput(input.MouseX, input.MouseY, _width, _height)
            || _colorPanel.IsOverPanel(input.MouseX, input.MouseY, _width, _height)
            || IsOverEditorChrome(input.MouseX, input.MouseY))
        {
            return;
        }

        var ray = MouseRay.FromScreen(input.MouseX, input.MouseY, _width, _height, _camera);

        if (_playMode.IsPlaying)
        {
            HandlePlayViewportClick(input, ray);
            return;
        }

        _rotateHover = _toolState.ActiveTool == EditorToolMode.Rotate
                       && !_rotateGizmoController.IsDragging
                       && _selection.SelectedPart is Part rotateHoverPart
            ? _rotateGizmoController.PickHover(ray, rotateHoverPart, _camera, input.MouseX, input.MouseY, _width, _height)
            : RotateHandle.None;

        _moveHover = _toolState.ActiveTool == EditorToolMode.Move
                     && !_moveGizmoController.IsDragging
                     && _selection.SelectedPart is Part moveHoverPart
            ? _moveGizmoController.PickHover(ray, moveHoverPart, _camera, input.MouseX, input.MouseY, _width, _height)
            : GizmoAxis.None;

        if (_toolState.ActiveTool == EditorToolMode.Resize
            && !_resizeGizmoController.IsDragging
            && _selection.SelectedPart is Part resizeHoverPart)
        {
            var allowResizeCorners = _selection.SelectedParts.Count <= 1;
            _resizeGizmoController.PickHover(ray, resizeHoverPart, _camera, input.MouseX, input.MouseY, _width, _height, allowResizeCorners, out _resizeHover, out _resizeCornerHover);
        }
        else
        {
            _resizeHover = ResizeHandle.None;
            _resizeCornerHover = ResizeCornerHandle.None;
        }

        // A locked selection keeps its gizmo, but pressing a handle is refused (the whole selection: nothing half-moves).
        if (input.LeftMousePressed && SelectionHasLocked() && IsOverGizmoHandle())
        {
            RefuseLockedEdit();
            Console.WriteLine($"[Lock] {_toolState.ActiveTool} refused: the selection is locked");
        }
        else if (input.LeftMousePressed)
        {
            var gizmoHandled = false;
            if (_toolState.ActiveTool == EditorToolMode.Move && _selection.SelectedPart is Part selectedForMove)
            {
                gizmoHandled = _moveGizmoController.TryBeginDrag(
                    ray,
                    selectedForMove,
                    _selection.SelectedParts,
                    _camera,
                    input.MouseX,
                    input.MouseY,
                    _width,
                    _height,
                    out _);
            }
            else if (_toolState.ActiveTool == EditorToolMode.Resize && _selection.SelectedPart is Part selectedForResize)
            {
                gizmoHandled = _resizeGizmoController.TryBeginDrag(
                    ray,
                    selectedForResize,
                    _selection.SelectedParts,
                    _camera,
                    input.MouseX,
                    input.MouseY,
                    _width,
                    _height,
                    out _);
            }
            else if (_toolState.ActiveTool == EditorToolMode.Rotate && _selection.SelectedPart is Part selectedForRotate)
            {
                gizmoHandled = _rotateGizmoController.TryBeginDrag(
                    ray,
                    selectedForRotate,
                    _camera,
                    input.MouseX,
                    input.MouseY,
                    _width,
                    _height,
                    out _);
            }

            if (gizmoHandled)
            {
                _partDragger.EndDrag();
            }
            else
            {
                // Locked Parts are looked through: a click selects what is behind them (or starts a box selection).
                var hit = PartPicker.Pick(ray, _workspace, out var hitPoint, skipLocked: true);
                if (hit is not null)
                {
                    _boxSelection.Cancel();

                    // Pressing a Part of a (movable) multi-selection drags the whole selection; a click that does not
                    // move still selects just that Part (on release). The Rotate and Resize tools drag a Part's body
                    // like Select does (their handles were tried first, above) and stay the active tool.
                    var bodyDrag = BodyDragTool(_toolState.ActiveTool);
                    var dragSelection = bodyDrag
                        && _selection.SelectedParts.Count > 1
                        && _selection.IsSelected(hit)
                        && !SelectionHasLocked();
                    if (!dragSelection)
                    {
                        _selection.Select(hit);
                        SyncPartRendererSelection();
                    }

                    if (bodyDrag)
                    {
                        _partDragger.BeginDrag(hit, ray, hitPoint, input.MouseX, input.MouseY,
                            dragSelection ? _selection.SelectedParts : null);
                    }
                    else
                    {
                        _partDragger.EndDrag();
                    }
                }
                else
                {
                    // Empty space: box selection under every tool (a plain click clears the selection on release; a
                    // Ctrl+click on the baseplate selects it instead).
                    _boxSelection.Begin(input.MouseX, input.MouseY);
                    _partDragger.EndDrag();
                    NoteEnvironmentPress(input, ray);
                }
            }
        }

        if (input.IsLeftMouseHeld && !input.LeftMousePressed)
        {
            if (_boxSelection.IsActive)
            {
                _boxSelection.Update(input.MouseX, input.MouseY);
            }
            else if (_toolState.ActiveTool == EditorToolMode.Move
                && _moveGizmoController.IsDragging
                && _selection.SelectedPart is not null)
            {
                _moveGizmoController.UpdateDrag(
                    _selection.SelectedPart,
                    _selection.SelectedParts,
                    _workspace,
                    ray);
            }
            else if (_toolState.ActiveTool == EditorToolMode.Resize
                && _resizeGizmoController.IsDragging
                && _selection.SelectedPart is not null)
            {
                _resizeGizmoController.UpdateDrag(
                    _selection.SelectedPart,
                    _selection.SelectedParts,
                    _workspace,
                    _camera,
                    ray);
            }
            else if (_toolState.ActiveTool == EditorToolMode.Rotate
                && _rotateGizmoController.IsDragging
                && _selection.SelectedPart is not null)
            {
                _rotateGizmoController.UpdateDrag(
                    _selection.SelectedPart,
                    _camera,
                    ray,
                    input.MouseX,
                    input.MouseY,
                    _width,
                    _height,
                    input.MoveFast);
            }
            else if (BodyDragTool(_toolState.ActiveTool) && _partDragger.IsDragging)
            {
                _partDragger.UpdateDrag(ray, _workspace, input.MouseX, input.MouseY);
            }
        }

        if (input.LeftMouseReleased)
        {
            if (_boxSelection.IsActive)
            {
                FinalizeBoxSelection();
                _boxSelection.Cancel();
            }
            else
            {
                // A press on a selected Part that moved nothing was a click: it selects that Part alone.
                var clickedInSelection = _partDragger.IsDragging && _partDragger.Group.Count > 1
                    && _partDragger.DraggedPart is { } pressed && PartSnapshot.NearlyEqual(_partDragger.DragStartPosition, pressed.Position)
                        ? pressed
                        : null;
                EndAllEditorDrags();
                if (clickedInSelection is not null)
                {
                    _selection.Select(clickedInSelection);
                    SyncPartRendererSelection();
                }
            }
        }

        if (_toolState.ActiveTool == EditorToolMode.Select
            && _selection.SelectedPart is not null
            && !_partDragger.IsDragging
            && !_moveGizmoController.IsDragging
            && !_resizeGizmoController.IsDragging)
        {
            var partMove = new Vector3(input.PartMoveX, input.PartMoveY, input.PartMoveZ);
            var hasKeyboardMove = partMove.Length > 1e-6f && !PhysicsPanelOwnsKeyboard;
            if (hasKeyboardMove && SelectionHasLocked())
            {
                RefuseLockedEdit();
            }
            else if (hasKeyboardMove && _selection.SelectedPart is Part keyboardPart)
            {
                if (!ReferenceEquals(_keyboardMovePart, keyboardPart))
                {
                    FinalizeKeyboardMove();
                    _keyboardMovePart = keyboardPart;
                    _keyboardMoveStart = keyboardPart.Position;
                }

                PartMover.TryMove(keyboardPart, partMove, deltaTime, input.MoveFast, _workspace);
            }
            else
            {
                FinalizeKeyboardMove();
            }
        }
    }

    // Play Mode: a click selects (or clears the selection) to inspect an object; nothing is dragged or edited.
    private void HandlePlayViewportClick(InputManager input, Ray ray)
    {
        if (!input.LeftMousePressed || _workspace is null || _selection is null)
        {
            return;
        }

        _selection.Select(PartPicker.Pick(ray, _workspace, out _));
        SyncPartRendererSelection();
    }

    private void HandleUndoRedo(InputManager input)
    {
        if (!input.UndoPressed && !input.RedoPressed)
        {
            return;
        }

        if (_playMode.IsPlaying)
        {
            _toast.Show(PlayBlockedMessage);
            return;
        }

        // A field being edited, a GUI drag in progress or no world: the keys belong to that, not to the history.
        if (_worldExplorer.IsRenaming || PhysicsPanelOwnsKeyboard || _guiDrag is not null || _guiSlider is not null || _guiTreeDrag is not null
            || _workspace is null || _selection is null)
        {
            return;
        }

        if (input.UndoPressed)
        {
            FinalizeKeyboardMove();
            EndAllEditorDrags();
            if (_editorHistory.TryUndo(_workspace, _partRenderer, _selection, out var undoMessage))
            {
                Console.WriteLine($"[Undo] {undoMessage}");
            }
        }

        if (input.RedoPressed)
        {
            FinalizeKeyboardMove();
            EndAllEditorDrags();
            if (_editorHistory.TryRedo(_workspace, _partRenderer, _selection, out var redoMessage))
            {
                Console.WriteLine($"[Redo] {redoMessage}");
            }
        }
    }

    private void SyncPartRendererSelection()
    {
        if (_selection is null)
        {
            return;
        }

        _partRenderer.SetSelected(_selection.SelectedParts);
    }

    private void FinalizeBoxSelection()
    {
        if (_workspace is null || _selection is null)
        {
            return;
        }

        if (_boxSelection.TryGetSelectionRect(out var rectX, out var rectY, out var rectWidth, out var rectHeight))
        {
            var parts = PartBoxSelect.CollectPartsInRect(
                _workspace,
                _camera,
                _width,
                _height,
                rectX,
                rectY,
                rectWidth,
                rectHeight);
            _selection.SelectMany(parts.Where(part => !part.Locked).ToList());
            _environmentPress = null;
        }
        else if (!TrySelectPressedEnvironment())
        {
            // A plain click on the ground or the sky: nothing selected (not the baseplate either).
            _selection.Select(null);
            _worldExplorer.SelectEnvironment(-1);
        }

        SyncPartRendererSelection();
        EndAllEditorDrags();
    }

    private void EndAllEditorDrags()
    {
        RecordMoveGizmoDrag();
        RecordResizeGizmoDrag();
        RecordRotateGizmoDrag();
        RecordPartDraggerDrag();
        _partDragger.EndDrag();
        _moveGizmoController.EndDrag();
        _resizeGizmoController.EndDrag();
        _rotateGizmoController.EndDrag();
    }

    private void RecordRotateGizmoDrag()
    {
        if (!_rotateGizmoController.IsDragging || _selection?.SelectedPart is not Part part)
        {
            return;
        }

        var before = PartSnapshot.FromPart(part) with { Rotation = _rotateGizmoController.DragStartRotation };
        var after = PartSnapshot.FromPart(part);
        if (before.SameTransform(after))
        {
            return;
        }

        _editorHistory.Push(EditorCommand.Rotate(part, before, after));
    }

    private void RecordMoveGizmoDrag()
    {
        if (!_moveGizmoController.IsDragging || _selection?.SelectedPart is not Part leader)
        {
            return;
        }

        var groupStarts = _moveGizmoController.GroupDragStartPositions;
        if (groupStarts.Count > 1)
        {
            var parts = groupStarts.Keys.ToArray();
            var before = new List<PartSnapshot>(parts.Length);
            var after = new List<PartSnapshot>(parts.Length);
            var moved = false;

            foreach (var part in parts)
            {
                var start = groupStarts[part];
                if (!PartSnapshot.NearlyEqual(start, part.Position))
                {
                    moved = true;
                }

                before.Add(PartSnapshot.FromPart(part) with { Position = start });
                after.Add(PartSnapshot.FromPart(part));
            }

            if (!moved)
            {
                return;
            }

            _editorHistory.Push(EditorCommand.GroupMove(parts, before, after));
            return;
        }

        var leaderStart = _moveGizmoController.DragStartPosition;
        if (PartSnapshot.NearlyEqual(leaderStart, leader.Position))
        {
            return;
        }

        var leaderBefore = PartSnapshot.FromPart(leader) with { Position = leaderStart };
        var leaderAfter = PartSnapshot.FromPart(leader);
        _editorHistory.Push(EditorCommand.Move(leader, leaderBefore, leaderAfter));
    }

    private void RecordResizeGizmoDrag()
    {
        if (!_resizeGizmoController.IsDragging || _selection?.SelectedPart is not Part leader)
        {
            return;
        }

        var groupStarts = _resizeGizmoController.GroupDragStartStates;
        if (groupStarts.Count > 1)
        {
            var parts = groupStarts.Keys.ToArray();
            var before = new List<PartSnapshot>(parts.Length);
            var after = new List<PartSnapshot>(parts.Length);
            var changed = false;

            foreach (var part in parts)
            {
                var start = groupStarts[part];
                var partBefore = PartSnapshot.FromPart(part) with
                {
                    Position = start.Position,
                    Size = start.Size
                };
                var partAfter = PartSnapshot.FromPart(part);
                if (!partBefore.SameTransform(partAfter))
                {
                    changed = true;
                }

                before.Add(partBefore);
                after.Add(partAfter);
            }

            if (!changed)
            {
                return;
            }

            _editorHistory.Push(EditorCommand.GroupResize(parts, before, after));
            return;
        }

        var leaderBefore = PartSnapshot.FromPart(leader) with
        {
            Position = _resizeGizmoController.DragStartPosition,
            Size = _resizeGizmoController.DragStartSize
        };
        var leaderAfter = PartSnapshot.FromPart(leader);
        if (leaderBefore.SameTransform(leaderAfter))
        {
            return;
        }

        _editorHistory.Push(EditorCommand.Resize(leader, leaderBefore, leaderAfter));
    }

    // The tools whose press on a Part's body drags it (Move keeps its own axis handles only).
    private static bool BodyDragTool(EditorToolMode tool) => tool is EditorToolMode.Select or EditorToolMode.Rotate or EditorToolMode.Resize;

    private void RecordPartDraggerDrag()
    {
        if (!_partDragger.IsDragging || _partDragger.DraggedPart is not Part part)
        {
            return;
        }

        // A dragged selection is one undo step for all of it.
        if (_partDragger.Group.Count > 1)
        {
            var parts = _partDragger.Group.ToArray();
            var starts = _partDragger.GroupStartPositions;
            if (PartSnapshot.NearlyEqual(starts[0], parts[0].Position))
            {
                return;
            }

            var groupBefore = parts.Select((member, i) => PartSnapshot.FromPart(member) with { Position = starts[i] }).ToArray();
            _editorHistory.Push(EditorCommand.GroupMove(parts, groupBefore, parts.Select(PartSnapshot.FromPart).ToArray()));
            return;
        }

        var start = _partDragger.DragStartPosition;
        if (PartSnapshot.NearlyEqual(start, part.Position))
        {
            return;
        }

        var before = PartSnapshot.FromPart(part) with { Position = start };
        var after = PartSnapshot.FromPart(part);
        _editorHistory.Push(EditorCommand.Move(part, before, after));
    }

    private void FinalizeKeyboardMove()
    {
        if (_keyboardMovePart is null)
        {
            return;
        }

        var part = _keyboardMovePart;
        _keyboardMovePart = null;
        if (!PartSnapshot.NearlyEqual(_keyboardMoveStart, part.Position))
        {
            var before = PartSnapshot.FromPart(part) with { Position = _keyboardMoveStart };
            var after = PartSnapshot.FromPart(part);
            _editorHistory.Push(EditorCommand.Move(part, before, after));
        }
    }

    private void HandleCamera(InputManager input, float deltaTime)
    {
        // A script placed the Play camera: it owns the view until Play stops.
        if (_worldExplorer.IsRenaming || PhysicsPanelOwnsKeyboard || (_playMode.IsPlaying && _playCamera is { ScriptControlled: true }))
        {
            return;
        }

        if (input.LookDeltaX != 0f || input.LookDeltaY != 0f)
        {
            _camera.Look(input.LookDeltaX, input.LookDeltaY);
        }

        if (input.ScrollDelta != 0f && !_wheelConsumed)
        {
            _camera.ScrollFly(input.ScrollDelta);
        }

        // While playing, WASD/Space/C belong to the scripts; the camera flies with them only while right-mouse looking.
        if (_playMode.IsPlaying && !input.IsRightMouseHeld)
        {
            return;
        }

        var move = Vector3.Zero;
        if (input.MoveForward)
        {
            move += _camera.Forward;
        }

        if (input.MoveBackward)
        {
            move -= _camera.Forward;
        }

        if (input.MoveRight)
        {
            move += _camera.Right;
        }

        if (input.MoveLeft)
        {
            move -= _camera.Right;
        }

        if (input.MoveUp)
        {
            move += Vector3.UnitY;
        }

        if (input.MoveDown)
        {
            move -= Vector3.UnitY;
        }

        _camera.Move(move, deltaTime, input.MoveFast);
    }
}
