using OpenTK.Graphics.OpenGL4;
using Wisko.Engine.Atmosphere;
using Wisko.Engine.Graphics;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Rendering.Universal;

/// <summary>Counters and timings of the last Universal frame (diagnostics, benchmarks, the Graphics panel).</summary>
internal sealed class UniversalStats
{
    public int Items;
    public int ItemsDrawn;
    public int ItemsCulled;
    public int Lights;
    public int SurfaceDrawCalls;
    public int ShadowDrawCalls;
    public int PostPasses;
    public int ShadowMapsRendered;
    public int Triangles;
    public bool PostProcessing;
    public double CollectMs;
    public double SceneCpuMs;

    public int DrawCalls => SurfaceDrawCalls + ShadowDrawCalls + PostPasses;
}

/// <summary>
/// Wisko's Universal renderer (OpenGL 3.3): forward-lit PBR, Simple, Unlit and Toon materials with
/// textures; the Atmosphere sun with cascaded shadows; point and spot lights with shadows; sky / ground ambient
/// light with screen-space ambient occlusion; sky reflections; HDR with bloom, white balance, saturation, contrast,
/// tone mapping, vignette and FXAA. Two outputs:
/// <list type="bullet">
/// <item>Post-processing (Medium and up): the scene into HDR targets (multisampled with MSAA), then composited into
/// the window; the scene's depth is copied back and the selection stencil re-marked, so the editor's outlines, gizmos
/// and labels draw on top exactly as with the Classic renderer.</item>
/// <item>Direct (Low, camera feeds, thumbnails): the same shaders straight into the bound framebuffer, graded and
/// tone-mapped per pixel, no HDR targets.</item>
/// </list>
/// Everything here is OpenGL-only; the Classic renderer stays the fallback (a shader or target that fails to compile
/// or allocate disables the Universal renderer for the session, with the reason logged and shown).
/// </summary>
internal sealed class UniversalRenderer : IDisposable
{
    private const int LightBindingPoint = 1;
    private const int UnitBase = 0, UnitMetalRough = 1, UnitNormal = 2, UnitOcclusion = 3, UnitEmissive = 4;
    private const int UnitSunShadow = 5, UnitSpotShadow = 6, UnitPoint0 = 7, UnitPoint1 = 8, UnitEnvironment = 9, UnitBrdf = 10, UnitAo = 11;

    private readonly ShaderLibrary _shaders = new();
    private readonly UniversalMeshCache _meshes = new();
    private readonly TextureCache _textures = new();
    private readonly SceneCollector _scene = new();
    private readonly ShadowRenderer _shadows = new();
    private readonly EnvironmentLighting _environment = new();
    private readonly BoneUniformBuffer _bones = new();
    private readonly ViewTargets _targets = new();
    private readonly MaterialResolver _materials;
    private readonly float[] _atmosphere = AtmosphereUniforms.Create();
    private readonly float[] _lightData = new float[SceneCollector.MaxLights * 16];
    private readonly int[] _lightIndexScratch = new int[16];
    private readonly float[] _matrix3 = new float[9];
    private readonly List<int> _opaque = [];
    private readonly List<int> _transparent = [];
    private readonly float[] _ssaoKernel = new float[16 * 3];
    private readonly int[] _boundTextures = new int[16];

    private GlProgram _surface = null!, _surfaceSkinned = null!, _surfaceDirect = null!, _surfaceSkinnedDirect = null!;
    private GlProgram _sky = null!, _skyDirect = null!, _outline = null!, _outlineSkinned = null!, _outlineDirect = null!, _outlineSkinnedDirect = null!;
    private GlProgram _particle = null!, _particleDirect = null!;
    private GlProgram _composite = null!, _bloom = null!, _fxaa = null!, _ssao = null!, _ssaoBlur = null!, _depthCopy = null!;
    private GlProgram _envPrefilter = null!, _brdf = null!;
    private DepthDrawer _depth = null!;
    private readonly TextureSamplers _samplers = new();
    private int _lightUbo;
    private int _fullscreenVao;
    private int _fullscreenVbo;
    private GlTexture _white = null!, _flatNormal = null!;
    private int _dummySunShadow, _dummySpotShadow, _dummyPointShadow, _dummyEnvironment;

    private GraphicsSettings _settings = GraphicsSettings.Default;
    private QualitySettings _quality = QualitySettings.Medium;
    private AtmosphereSettings _atmosphereSettings = AtmosphereSettings.Default;
    private Vector3 _towardSun;
    private Vector3 _sunRadiance;
    private Vector3 _ambientUp;
    private Vector3 _ambientDown;
    private float _exposure = 1f;
    private float _dielectricReflection = 1f;

    public UniversalRenderer() => _materials = new MaterialResolver(_textures);

    /// <summary>False when start-up failed (shaders, targets): the Classic renderer is used instead.</summary>
    public bool Healthy { get; private set; }

    /// <summary>Whether <see cref="Initialize"/> has run (it runs once, when a project first needs this renderer).</summary>
    public bool Started { get; private set; }

    public string? FailureReason { get; private set; }

    public UniversalStats Stats { get; } = new();

    public TextureCache Textures => _textures;

    public ShaderLibrary Shaders => _shaders;

    /// <summary>GPU memory of the renderer's own resources (targets, shadow maps, environment, meshes, textures).</summary>
    public long GpuBytes => _targets.Bytes + _shadows.Bytes + _environment.Bytes + UniversalMesh.TotalBytes + GlTexture.TotalBytes;

    public bool PostProcessingActive => _quality.PostProcessing;

    public QualitySettings Quality => _quality;

    public GraphicsSettings Settings => _settings;

    /// <summary>
    /// Compiles every program and creates the shared resources. False (and <see cref="FailureReason"/>) on failure. Runs
    /// once: later calls answer the first result.
    /// </summary>
    public bool Initialize()
    {
        if (Started)
        {
            return Healthy;
        }

        Started = true;
        try
        {
            GlProgram Need(string vertex, string fragment, params string[] defines) =>
                _shaders.Get(vertex, fragment, defines) ?? throw new InvalidOperationException(_shaders.Errors.LastOrDefault() ?? $"{vertex}+{fragment}");

            _surface = Need("surface.vert", "surface.frag");
            _surfaceSkinned = Need("surface.vert", "surface.frag", "SKINNED");
            _surfaceDirect = Need("surface.vert", "surface.frag", "DIRECT_OUTPUT");
            _surfaceSkinnedDirect = Need("surface.vert", "surface.frag", "SKINNED", "DIRECT_OUTPUT");
            _sky = Need("fullscreen.vert", "sky.frag");
            _skyDirect = Need("fullscreen.vert", "sky.frag", "DIRECT_OUTPUT");
            _outline = Need("outline.vert", "outline.frag");
            _outlineSkinned = Need("outline.vert", "outline.frag", "SKINNED");
            _outlineDirect = Need("outline.vert", "outline.frag", "DIRECT_OUTPUT");
            _outlineSkinnedDirect = Need("outline.vert", "outline.frag", "SKINNED", "DIRECT_OUTPUT");
            _particle = Need("particle.vert", "particle.frag");
            _particleDirect = Need("particle.vert", "particle.frag", "DIRECT_OUTPUT");
            _composite = Need("fullscreen.vert", "composite.frag");
            _bloom = Need("fullscreen.vert", "bloom.frag");
            _fxaa = Need("fullscreen.vert", "fxaa.frag");
            _ssao = Need("fullscreen.vert", "ssao.frag");
            _ssaoBlur = Need("fullscreen.vert", "ssao_blur.frag");
            _depthCopy = Need("fullscreen.vert", "depth_copy.frag");
            _envPrefilter = Need("fullscreen.vert", "env_prefilter.frag");
            _brdf = Need("fullscreen.vert", "brdf_lut.frag");
            _samplers.Initialize();
            _depth = new DepthDrawer(
                Need("depth.vert", "depth.frag"), Need("depth.vert", "depth.frag", "SKINNED"),
                Need("depth.vert", "depth.frag", "DISTANCE_DEPTH"), Need("depth.vert", "depth.frag", "SKINNED", "DISTANCE_DEPTH"), _bones, _samplers);

            foreach (var program in new[] { _surface, _surfaceSkinned, _surfaceDirect, _surfaceSkinnedDirect })
            {
                program.Sampler("uBaseColorTex", UnitBase);
                program.Sampler("uMetalRoughTex", UnitMetalRough);
                program.Sampler("uNormalTex", UnitNormal);
                program.Sampler("uOcclusionTex", UnitOcclusion);
                program.Sampler("uEmissiveTex", UnitEmissive);
                program.Sampler("uSunShadowMap", UnitSunShadow);
                program.Sampler("uSpotShadowAtlas", UnitSpotShadow);
                program.Sampler("uPointShadow0", UnitPoint0);
                program.Sampler("uPointShadow1", UnitPoint1);
                program.Sampler("uEnvironment", UnitEnvironment);
                program.Sampler("uBrdfLut", UnitBrdf);
                program.Sampler("uAmbientOcclusion", UnitAo);
                program.BindBlock("LightBlock", LightBindingPoint);
                program.BindBlock(BoneUniformBuffer.BlockName, BoneUniformBuffer.BindingPoint);
            }

            foreach (var program in new[] { _outlineSkinned, _outlineSkinnedDirect })
            {
                program.BindBlock(BoneUniformBuffer.BlockName, BoneUniformBuffer.BindingPoint);
            }

            _bones.Initialize();
            _lightUbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.UniformBuffer, _lightUbo);
            GL.BufferData(BufferTarget.UniformBuffer, _lightData.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.UniformBuffer, 0);

            float[] corners = [-1f, -1f, 3f, -1f, -1f, 3f];
            _fullscreenVao = GL.GenVertexArray();
            _fullscreenVbo = GL.GenBuffer();
            GL.BindVertexArray(_fullscreenVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _fullscreenVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), corners, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);

            _white = GlTexture.Solid(255, 255, 255, 255, srgb: false);
            _flatNormal = GlTexture.Solid(128, 128, 255, 255, srgb: false);
            CreateDummies();
            BuildSsaoKernel();
            if (!_environment.Initialize(_brdf, _fullscreenVao))
            {
                throw new InvalidOperationException("the environment BRDF table could not be created");
            }

            Healthy = true;
        }
        catch (InvalidOperationException ex)
        {
            Healthy = false;
            FailureReason = ex.Message.Trim();
            Console.WriteLine($"[Graphics] Universal renderer unavailable, using Classic: {FailureReason}");
        }

        GlDebug.CheckError("universal renderer initialization");
        return Healthy;
    }

    /// <summary>The settings, quality and environment for this frame.</summary>
    public void Configure(GraphicsSettings settings, QualitySettings quality, AtmosphereSettings atmosphere, string? projectRoot)
    {
        if (!ReferenceEquals(settings, _settings) && settings != _settings)
        {
            _shadows.Invalidate();
        }

        _settings = settings;
        _quality = quality;
        _atmosphereSettings = atmosphere;
        _materials.ProjectRoot = projectRoot;
        if (atmosphere.Enabled)
        {
            // Calibrated to the Classic renderer, which adds sun and ambient light in display space: a surface facing
            // the sun shows Perceptual(ambient + sun), one in shade Perceptual(ambient), so Part colours look alike.
            var ambient = atmosphere.AmbientColor.ToVector3() * atmosphere.AmbientIntensity;
            var sun = atmosphere.SunColor.ToVector3() * atmosphere.SunIntensity;
            _towardSun = atmosphere.SunDirectionToSun();
            _ambientUp = Perceptual(ambient, 1f);
            _sunRadiance = Max0(Perceptual(ambient + sun, 1f) - _ambientUp);
            _ambientDown = atmosphere.AmbientMode == AtmosphereAmbientMode.Sky
                ? Perceptual(atmosphere.GroundColor.ToVector3(), atmosphere.AmbientIntensity)
                : _ambientUp;

            // Sky reflections on rough non-metals stay in proportion to the ambient light (the sky is usually several times
            // brighter than the ambient fill, and would otherwise wash shaded colours out); glossy ones and metals reflect it
            // fully (the shader blends by smoothness).
            var sky = (Perceptual(atmosphere.SkyColor.ToVector3(), atmosphere.SkyIntensity)
                + Perceptual(atmosphere.HorizonColor.ToVector3(), atmosphere.SkyIntensity)) * 0.5f;
            _dielectricReflection = global::System.Math.Clamp(Luminance(_ambientUp) / MathF.Max(Luminance(sky), 1e-4f), 0.2f, 1f);

            // Classic multiplies the displayed colour by 2^Exposure; in linear light that is (2^Exposure)^2.2.
            _exposure = MathF.Pow(2f, atmosphere.Exposure * 2.2f);
        }
        else
        {
            var plain = new Vector3(AtmosphereUniforms.PlainAmbient, AtmosphereUniforms.PlainAmbient, AtmosphereUniforms.PlainAmbient);
            _towardSun = AtmosphereUniforms.PlainLightDirection * -1f;
            _ambientUp = _ambientDown = Perceptual(plain, 1f);
            _sunRadiance = Max0(Perceptual(plain + AtmosphereUniforms.PlainLightColor, 1f) - _ambientUp);
            _dielectricReflection = 1f;
            _exposure = 1f;
        }
    }

    private static Vector3 Max0(Vector3 v) => new(MathF.Max(v.X, 0f), MathF.Max(v.Y, 0f), MathF.Max(v.Z, 0f));

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>
    /// Atmosphere intensities are perceptual, as in the Classic renderer (a display-space multiplier): colour x
    /// intensity converted from display to linear light (power 2.2 above 1), so a project looks alike in both renderers.
    /// </summary>
    public static Vector3 Perceptual(Vector3 srgbColor, float intensity)
    {
        static float Channel(float c, float i)
        {
            var v = c * i;
            return v <= 1f ? ColorSpace.SrgbToLinear(v) : MathF.Pow(v, 2.2f);
        }

        return new Vector3(Channel(srgbColor.X, intensity), Channel(srgbColor.Y, intensity), Channel(srgbColor.Z, intensity));
    }

    /// <summary>Gathers this frame's draws and lights, uploads the lights and renders the light shadow maps (view-independent).</summary>
    public void BeginFrame(IReadOnlyList<Part> parts, IReadOnlyList<SceneObject> environment, IReadOnlyCollection<Part> selected, Vector3 cameraPosition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _shadows.BeginFrame();
        _meshes.RetainEnvironment(environment);
        _scene.Collect(parts, environment, selected, _materials, _meshes, _quality.MaxLightsPerObject, cameraPosition);
        Stats.Items = _scene.Items.Count;
        Stats.Lights = _scene.Lights.Count;
        Stats.SurfaceDrawCalls = Stats.ShadowDrawCalls = Stats.PostPasses = Stats.ItemsDrawn = Stats.ItemsCulled = Stats.Triangles = 0;
        Stats.CollectMs = clock.Elapsed.TotalMilliseconds;

        var shadowsOn = _settings.ShadowsEnabled;
        _shadows.RenderLights(_depth, _scene.Lights, _scene.Items,
            shadowsOn ? _quality.MaxSpotShadows : 0, _quality.SpotShadowResolution,
            shadowsOn && _settings.PointLightShadows ? _quality.MaxPointShadows : 0, _quality.PointShadowResolution);
        UploadLights();
    }

    /// <summary>Releases what nothing used this frame (model meshes, idle textures).</summary>
    public void EndFrame()
    {
        _meshes.ReleaseUnused();
        _textures.EndFrame();
        Stats.ShadowMapsRendered = _shadows.MapsRendered;
    }

    /// <summary>A new project (or none): every project resource goes; shaders and shared resources stay.</summary>
    public void ResetProject()
    {
        _meshes.Clear();
        _textures.Clear();
        _shadows.Invalidate();
    }

    // ---- views ----

    /// <summary>
    /// The main view into the window (framebuffer 0, already cleared). With post-processing it ends with the window
    /// holding the final image, the scene's depth and the selection stencil; otherwise the scene is drawn directly.
    /// <paramref name="drawParticles"/> draws the scripts' particles with the given program (null: none).
    /// </summary>
    public void RenderMain(in ViewSetup view, int windowWidth, int windowHeight, Action<GlProgram>? drawParticles)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Stats.PostProcessing = _quality.PostProcessing;
        if (!_quality.PostProcessing)
        {
            RenderDirect(view, drawParticles, writeSelection: true);
            Stats.SceneCpuMs = clock.Elapsed.TotalMilliseconds;
            return;
        }

        var scale = _quality.RenderScale;
        var width = global::System.Math.Max(1, (int)MathF.Round(windowWidth * scale));
        var height = global::System.Math.Max(1, (int)MathF.Round(windowHeight * scale));
        var samples = _settings.AntiAliasing is AntiAliasing.Msaa or AntiAliasing.MsaaAndFxaa ? MaxSamples() : 1;
        var bloomLevels = _settings.BloomEnabled ? _quality.BloomLevels : 0;
        if (!_targets.Matches(width, height, samples, _quality.AmbientOcclusionHalfResolution, bloomLevels, windowWidth, windowHeight)
            && !_targets.Create(width, height, samples, _quality.AmbientOcclusionHalfResolution, bloomLevels, windowWidth, windowHeight))
        {
            // Out of memory or an unsupported format: draw directly this frame (logged by the target).
            RenderDirect(view, drawParticles, writeSelection: true);
            return;
        }

        var aoOn = _settings.AmbientOcclusionEnabled && _quality.AmbientOcclusionSamples > 0;
        var scaled = view with { PixelWidth = width, PixelHeight = height };
        UpdateEnvironment(view);
        RenderSunShadows(scaled);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _targets.SceneFbo);
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.ClearStencil(0);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        SetBaselineState();
        Classify(scaled);

        if (aoOn)
        {
            // Depth prepass (opaque, alpha-masked cut out), then occlusion from its depth.
            GL.ColorMask(false, false, false, false);
            _depth.Draw(_scene.Items, scaled.ViewProjection, item => !item.Material.Transparent && Visible(item, scaled), cull: true);
            Stats.ShadowDrawCalls += _depth.TakeDrawCalls();
            GL.ColorMask(true, true, true, true);
            _targets.Resolve(color: false, depth: true);
            RenderAmbientOcclusion(scaled);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _targets.SceneFbo);
            GL.Viewport(0, 0, width, height);
            GL.DepthFunc(DepthFunction.Lequal);
        }

        DrawScene(scaled, direct: false, aoOn, writeSelection: false, drawParticles);

        // Resolve, bloom, then the final image into the window.
        _targets.Resolve(color: true, depth: !aoOn);
        if (aoOn)
        {
            _targets.Resolve(color: false, depth: true);
        }

        if (bloomLevels > 0)
        {
            RenderBloom();
        }

        var fxaa = _settings.AntiAliasing is AntiAliasing.Fxaa or AntiAliasing.MsaaAndFxaa;
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.BindVertexArray(_fullscreenVao);
        _composite.Use();
        _composite.Sampler("uScene", 0);
        _composite.Sampler("uBloom", 1);
        SetOutputUniforms(_composite);
        _composite.Set("uBloomOn", bloomLevels > 0 ? 1 : 0);
        _composite.Set("uBloomIntensity", _settings.BloomIntensity);
        _composite.Set("uVignette", _settings.VignetteEnabled ? _settings.VignetteIntensity : 0f, 0.5f);
        _composite.Set("uSourceRect", 0f, 0f, 1f, 1f);
        _composite.Set("uQuadDepth", 0f);
        BindTexture2D(0, _targets.HdrTexture);
        BindTexture2D(1, bloomLevels > 0 ? _targets.Bloom[0].Texture : _white.Id);
        if (fxaa)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _targets.LdrFbo);
            GL.Viewport(0, 0, windowWidth, windowHeight);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _fxaa.Use();
            _fxaa.Sampler("uSource", 0);
            _fxaa.Set("uSourceTexel", 1f / windowWidth, 1f / windowHeight);
            _fxaa.Set("uQuadDepth", 0f);
            BindTexture2D(0, _targets.LdrTexture);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stats.PostPasses += 2;
        }
        else
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, windowWidth, windowHeight);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stats.PostPasses++;
        }

        // The scene's depth into the window (overlays are hidden behind objects as before).
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Always);
        GL.DepthMask(true);
        GL.ColorMask(false, false, false, false);
        _depthCopy.Use();
        _depthCopy.Sampler("uDepth", 0);
        _depthCopy.Set("uSourceRect", 0f, 0f, 1f, 1f);
        _depthCopy.Set("uQuadDepth", 0f);
        BindTexture2D(0, _targets.DepthTexture);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Stats.PostPasses++;
        GL.ColorMask(true, true, true, true);
        GL.DepthFunc(DepthFunction.Less);

        // The selection stencil (ref 1) the outline pass expects: every selected object's silhouette.
        MarkSelection(view with { PixelWidth = windowWidth, PixelHeight = windowHeight });
        GL.BindVertexArray(0);
        SetBaselineState();
        Stats.SceneCpuMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The scene straight into the bound framebuffer (cleared by the caller): graded and tone-mapped per pixel, no
    /// post-processing (Low quality, camera feeds, thumbnails).
    /// </summary>
    public void RenderDirect(in ViewSetup view, Action<GlProgram>? drawParticles, bool writeSelection)
    {
        GL.GetInteger(GetPName.DrawFramebufferBinding, out var target);
        GL.GetInteger(GetPName.Viewport, _viewportScratch);
        UpdateEnvironment(view);
        RenderSunShadows(view);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, target);
        GL.Viewport(_viewportScratch[0], _viewportScratch[1], _viewportScratch[2], _viewportScratch[3]);
        SetBaselineState();
        Classify(view);
        DrawScene(view, direct: true, aoOn: false, writeSelection, drawParticles);
        SetBaselineState();
    }

    private readonly int[] _viewportScratch = new int[4];

    // ---- passes ----

    private void UpdateEnvironment(in ViewSetup view)
    {
        WriteAtmosphere(view);
        _environment.Update(_sky, _envPrefilter, _atmosphere, _quality.ReflectionResolution, _fullscreenVao);
    }

    private void RenderSunShadows(in ViewSetup view)
    {
        var on = _settings.ShadowsEnabled && _atmosphereSettings.Enabled && _sunRadiance.Length > 0.001f;
        if (!on)
        {
            _shadows.RenderSun(_depth, _scene.Items, view, _towardSun, 0, 0, 0f);
            return;
        }

        _shadows.RenderSun(_depth, _scene.Items, view, _towardSun, _quality.ShadowCascades, _quality.ShadowResolution,
            _settings.ShadowDistance * _quality.ShadowDistanceScale);
        Stats.ShadowDrawCalls += _depth.TakeDrawCalls();
    }

    // Splits the items into visible opaque (scene order: rigid first, then skinned) and transparent (back to front).
    private void Classify(in ViewSetup view)
    {
        _opaque.Clear();
        _transparent.Clear();
        var items = _scene.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!Visible(item, view))
            {
                Stats.ItemsCulled++;
                continue;
            }

            if (item.Material.Transparent)
            {
                var center = (item.WorldMin + item.WorldMax) * 0.5f;
                item.SortKey = (center - view.Position).Length;
                items[i] = item;
                _transparent.Add(i);
            }
            else
            {
                _opaque.Add(i);
            }
        }

        _opaque.Sort((a, b) => items[a].Mesh.Skinned.CompareTo(items[b].Mesh.Skinned) is var c && c != 0 ? c : a.CompareTo(b));
        _transparent.Sort((a, b) => items[b].SortKey.CompareTo(items[a].SortKey) is var c && c != 0 ? c : a.CompareTo(b));
    }

    private static bool Visible(in DrawItem item, in ViewSetup view)
    {
        // Conservative frustum test of the item's bounds (clip-space box corners against the planes).
        var vp = view.ViewProjection;
        Span<Vector3> corners = stackalloc Vector3[8];
        var min = item.WorldMin;
        var max = item.WorldMax;
        for (var i = 0; i < 8; i++)
        {
            corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        }

        // For each plane, if all corners are outside, the box is outside.
        for (var plane = 0; plane < 6; plane++)
        {
            var outside = 0;
            foreach (var p in corners)
            {
                var x = vp.M11 * p.X + vp.M12 * p.Y + vp.M13 * p.Z + vp.M14;
                var y = vp.M21 * p.X + vp.M22 * p.Y + vp.M23 * p.Z + vp.M24;
                var z = vp.M31 * p.X + vp.M32 * p.Y + vp.M33 * p.Z + vp.M34;
                var w = vp.M41 * p.X + vp.M42 * p.Y + vp.M43 * p.Z + vp.M44;
                var d = plane switch { 0 => w + x, 1 => w - x, 2 => w + y, 3 => w - y, 4 => w + z, _ => w - z };
                if (d < 0f)
                {
                    outside++;
                }
            }

            if (outside == 8)
            {
                return false;
            }
        }

        return true;
    }

    private void DrawScene(in ViewSetup view, bool direct, bool aoOn, bool writeSelection, Action<GlProgram>? drawParticles)
    {
        var items = _scene.Items;
        if (writeSelection)
        {
            GL.Enable(EnableCap.StencilTest);
            GL.StencilFunc(StencilFunction.Always, 1, 0xFF);
            GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
        }

        // Opaque and alpha-masked surfaces.
        GlProgram? current = null;
        foreach (var index in _opaque)
        {
            var item = items[index];
            var program = item.Mesh.Skinned && item.Palette is { Length: > 0 }
                ? (direct ? _surfaceSkinnedDirect : _surfaceSkinned)
                : (direct ? _surfaceDirect : _surface);
            if (!ReferenceEquals(program, current))
            {
                PrepareSurfaceProgram(program, view, direct, aoOn);
                current = program;
            }

            if (writeSelection)
            {
                GL.StencilMask(item.Selected ? 0xFF : 0x00);
            }

            DrawSurface(program, item);
        }

        if (writeSelection)
        {
            GL.StencilMask(0x00);
        }

        TextureSamplers.Unbind(UnitBase, UnitEmissive + 1);
        DrawGameplayOutlines(view, direct);

        // Sky behind everything that was drawn (only where the depth is still the far plane).
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        var sky = direct ? _skyDirect : _sky;
        sky.Use();
        sky.SetVec4s("u_atmo", _atmosphere);
        SetOutputUniforms(sky);
        var tanY = MathF.Tan(view.VerticalFovRadians * 0.5f);
        sky.Set("uRayForward", view.Forward);
        sky.Set("uRayRight", view.Right * (tanY * view.Aspect));
        sky.Set("uRayUp", view.Up * tanY);
        sky.Set("uSunDiscOn", 1);
        sky.Set("uQuadDepth", 1f);
        GL.BindVertexArray(_fullscreenVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Stats.SurfaceDrawCalls++;

        // Transparent surfaces, back to front, blended, no depth writes.
        if (_transparent.Count > 0)
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
            GL.DepthFunc(DepthFunction.Less);
            current = null;
            foreach (var index in _transparent)
            {
                var item = items[index];
                var program = item.Mesh.Skinned && item.Palette is { Length: > 0 }
                    ? (direct ? _surfaceSkinnedDirect : _surfaceSkinned)
                    : (direct ? _surfaceDirect : _surface);
                if (!ReferenceEquals(program, current))
                {
                    PrepareSurfaceProgram(program, view, direct, aoOn);
                    current = program;
                }

                DrawSurface(program, item);
            }

            TextureSamplers.Unbind(UnitBase, UnitEmissive + 1);
            GL.Disable(EnableCap.Blend);
        }

        // Script particles (rain, snow), lit like their surroundings, fogged.
        if (drawParticles is not null)
        {
            var particle = direct ? _particleDirect : _particle;
            particle.Use();
            particle.Set("uViewProjection", view.ViewProjection);
            particle.SetVec4s("u_atmo", _atmosphere);
            SetOutputUniforms(particle);
            var light = _ambientUp * 1.6f + _sunRadiance * (0.45f * MathF.Max(_towardSun.Y, 0f) + 0.15f);
            particle.Set("uParticleLight", light);
            GL.DepthFunc(DepthFunction.Lequal);
            drawParticles(particle);
        }

        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        if (writeSelection)
        {
            GL.StencilMask(0xFF);
            GL.Disable(EnableCap.StencilTest);
        }
    }

    private void PrepareSurfaceProgram(GlProgram p, in ViewSetup view, bool direct, bool aoOn)
    {
        p.Use();
        p.Set("uViewProjection", view.ViewProjection);
        p.Set("uCameraPosition", view.Position);
        p.Set("uCameraForward", view.Forward);
        p.SetVec4s("u_atmo", _atmosphere);
        if (direct)
        {
            SetOutputUniforms(p);
        }

        // Sun and its cascades.
        p.Set("uSunDirection", _towardSun);
        p.Set("uSunRadiance", _sunRadiance);
        p.Set("uSunShadowOn", _shadows.SunActive ? 1 : 0);
        if (_shadows.SunActive)
        {
            p.SetMatrices("uSunShadowMatrix", _shadows.SunMatrices);
            p.Set("uCascadeFar", _shadows.CascadeFar[0], _shadows.CascadeFar[1], _shadows.CascadeFar[2], _shadows.CascadeFar[3]);
            p.Set("uCascadeTexel", _shadows.CascadeTexel[0], _shadows.CascadeTexel[1], _shadows.CascadeTexel[2], _shadows.CascadeTexel[3]);
            p.Set("uCascadeCount", _shadows.CascadeCount);
        }

        p.Set("uShadowParams", _settings.ShadowBias, _settings.ShadowNormalBias, _quality.ShadowFilterSamples, _shadows.FadeStart);
        p.SetMatrices("uSpotShadowMatrix", _shadows.SpotMatrices);
        p.SetVec4s("uSpotShadowRect", _shadows.SpotRects);
        p.SetVec4s("uSpotShadowTexel", _shadows.SpotTexels);

        // Ambient, reflections, ambient occlusion.
        p.Set("uAmbientUp", _ambientUp);
        p.Set("uAmbientDown", _ambientDown);
        p.Set("uEnvironmentMaxLod", _environment.MaxLod);
        p.Set("uReflectionIntensity", _atmosphereSettings.Enabled ? _atmosphereSettings.ReflectionIntensity : 0.5f);
        p.Set("uDielectricReflection", _dielectricReflection);
        p.Set("uAmbientOcclusionOn", aoOn ? 1 : 0);
        p.Set("uViewportOrigin", 0f, 0f);
        p.Set("uViewportSize", view.PixelWidth, view.PixelHeight);

        BindTexture(UnitSunShadow, TextureTarget.Texture2DArray, _shadows.SunActive ? _shadows.SunTexture : _dummySunShadow);
        BindTexture(UnitSpotShadow, TextureTarget.Texture2D, _shadows.SpotAtlas != 0 ? _shadows.SpotAtlas : _dummySpotShadow);
        BindTexture(UnitPoint0, TextureTarget.TextureCubeMap, _shadows.PointCube(0) != 0 ? _shadows.PointCube(0) : _dummyPointShadow);
        BindTexture(UnitPoint1, TextureTarget.TextureCubeMap, _shadows.PointCube(1) != 0 ? _shadows.PointCube(1) : _dummyPointShadow);
        BindTexture(UnitEnvironment, TextureTarget.TextureCubeMap, _environment.PrefilteredCube != 0 ? _environment.PrefilteredCube : _dummyEnvironment);
        BindTexture(UnitBrdf, TextureTarget.Texture2D, _environment.BrdfLut);
        BindTexture(UnitAo, TextureTarget.Texture2D, aoOn ? _targets.AoBlurTexture : _white.Id);
        GL.BindBufferBase(BufferRangeTarget.UniformBuffer, LightBindingPoint, _lightUbo);
    }

    private void DrawSurface(GlProgram p, in DrawItem item)
    {
        var m = item.Material;
        p.Set("uModel", item.Model);
        item.Normal.CopyTo(_matrix3);
        p.SetMatrix3("uNormalMatrix", _matrix3);
        p.Set("uBaseColor", m.BaseR, m.BaseG, m.BaseB, m.Opacity);
        p.Set("uEmissive", m.Emissive);
        p.Set("uMaterialParams", m.Metallic, m.Roughness, m.NormalStrength, m.OcclusionStrength);
        p.Set("uMaterialParams2", m.AlphaCutoff, m.ToonBands, (int)m.Mode, (int)m.AlphaMode);
        p.Set("uMaterialFlags", m.Flags);
        p.Set("uUvMode", m.UvMode == UvMode.Studs ? 1 : 0);
        p.Set("uUvScale", m.UvScaleX, m.UvScaleY);
        p.Set("uObjectSize", item.ObjectSize);
        BindTexture(UnitBase, TextureTarget.Texture2D, m.BaseTexture?.Id ?? _white.Id);
        BindTexture(UnitMetalRough, TextureTarget.Texture2D, m.MetalRoughTexture?.Id ?? _white.Id);
        BindTexture(UnitNormal, TextureTarget.Texture2D, m.NormalTexture?.Id ?? _flatNormal.Id);
        BindTexture(UnitOcclusion, TextureTarget.Texture2D, m.OcclusionTexture?.Id ?? _white.Id);
        BindTexture(UnitEmissive, TextureTarget.Texture2D, m.EmissiveTexture?.Id ?? _white.Id);
        for (var slot = UnitBase; slot <= UnitEmissive; slot++)
        {
            _samplers.Bind(slot, m.WrapOf(slot));
        }

        var count = global::System.Math.Min(item.LightCount, 16);
        for (var i = 0; i < count; i++)
        {
            _lightIndexScratch[i] = _scene.LightIndices[item.LightStart + i];
        }

        p.Set("uLightCount", count);
        if (count > 0)
        {
            p.SetInts("uLightIndex", _lightIndexScratch.AsSpan(0, count));
        }

        if (m.Cull)
        {
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
            GL.FrontFace(item.Mirrored ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
        }
        else
        {
            GL.Disable(EnableCap.CullFace);
        }

        if (item.Mesh.Skinned && item.Palette is { Length: > 0 } palette)
        {
            _bones.Upload(palette);
        }

        item.Mesh.Draw();
        Stats.SurfaceDrawCalls++;
        Stats.ItemsDrawn++;
        Stats.Triangles += item.Mesh.IndexCount / 3;
    }

    private void DrawGameplayOutlines(in ViewSetup view, bool direct)
    {
        var any = false;
        foreach (var index in _opaque)
        {
            if (_scene.Items[index].Material.Outline)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Front);
        GlProgram? current = null;
        foreach (var index in _opaque)
        {
            var item = _scene.Items[index];
            if (!item.Material.Outline)
            {
                continue;
            }

            var skinned = item.Mesh.Skinned && item.Palette is { Length: > 0 };
            var program = skinned ? (direct ? _outlineSkinnedDirect : _outlineSkinned) : (direct ? _outlineDirect : _outline);
            if (!ReferenceEquals(program, current))
            {
                program.Use();
                program.Set("uViewProjection", view.ViewProjection);
                program.SetVec4s("u_atmo", _atmosphere);
                SetOutputUniforms(program);
                current = program;
            }

            program.Set("uModel", item.Model);
            item.Normal.CopyTo(_matrix3);
            program.SetMatrix3("uNormalMatrix", _matrix3);
            program.Set("uThickness", item.Material.OutlineThickness);
            program.Set("uOutlineColor", item.Material.OutlineColor);
            GL.FrontFace(item.Mirrored ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            if (skinned)
            {
                _bones.Upload(item.Palette!);
            }

            item.Mesh.Draw();
            Stats.SurfaceDrawCalls++;
        }

        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Disable(EnableCap.CullFace);
    }

    private void RenderAmbientOcclusion(in ViewSetup view)
    {
        var projection = view.Projection;
        var inverse = InvertProjection(projection);
        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(_fullscreenVao);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _targets.AoFbo);
        GL.Viewport(0, 0, _targets.AoWidth, _targets.AoHeight);
        _ssao.Use();
        _ssao.Sampler("uDepth", 0);
        _ssao.Set("uProjection", projection);
        _ssao.Set("uInverseProjection", inverse);
        _ssao.Set("uDepthTexel", 1f / _targets.Width, 1f / _targets.Height);
        _ssao.Set("uParams", _settings.AmbientOcclusionRadius, _settings.AmbientOcclusionIntensity, _quality.AmbientOcclusionSamples, 1.6f);
        _ssao.SetVec3s("uKernel", _ssaoKernel);
        _ssao.Set("uDepthRect", 0f, 0f, 1f, 1f);
        _ssao.Set("uQuadDepth", 0f);
        BindTexture2D(0, _targets.DepthTexture);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _targets.AoBlurFbo);
        GL.Viewport(0, 0, _targets.Width, _targets.Height);
        _ssaoBlur.Use();
        _ssaoBlur.Sampler("uSource", 0);
        _ssaoBlur.Sampler("uDepth", 1);
        _ssaoBlur.Set("uSourceTexel", 1f / _targets.AoWidth, 1f / _targets.AoHeight);
        _ssaoBlur.Set("uDepthRect", 0f, 0f, 1f, 1f);
        _ssaoBlur.Set("uNearFar", view.Near, view.Far);
        _ssaoBlur.Set("uQuadDepth", 0f);
        BindTexture2D(0, _targets.AoTexture);
        BindTexture2D(1, _targets.DepthTexture);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Stats.PostPasses += 2;

        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
    }

    private void RenderBloom()
    {
        var levels = _targets.Bloom;
        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(_fullscreenVao);
        _bloom.Use();
        _bloom.Sampler("uSource", 0);
        _bloom.Set("uQuadDepth", 0f);
        var knee = MathF.Max(_settings.BloomThreshold * 0.5f, 0.05f);
        var radius = 0.35f + 0.65f * _settings.BloomRadius;

        // Down: prefilter into level 0 (half resolution), then halve.
        for (var level = 0; level < levels.Count; level++)
        {
            var (fbo, _, w, h) = levels[level];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.Viewport(0, 0, w, h);
            int sourceTexture;
            float sw, sh;
            if (level == 0)
            {
                sourceTexture = _targets.HdrTexture;
                sw = _targets.Width;
                sh = _targets.Height;
            }
            else
            {
                (_, sourceTexture, var pw, var ph) = levels[level - 1];
                sw = pw;
                sh = ph;
            }

            _bloom.Set("uMode", level == 0 ? 0 : 1);
            _bloom.Set("uSourceTexel", 1f / sw, 1f / sh);
            _bloom.Set("uThreshold", _settings.BloomThreshold, knee, 64f, radius);
            BindTexture2D(0, sourceTexture);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stats.PostPasses++;
        }

        // Up: each level adds the blurred smaller one (additive), ending at level 0.
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _bloom.Set("uMode", 2);
        for (var level = levels.Count - 1; level > 0; level--)
        {
            var (fbo, _, w, h) = levels[level - 1];
            var (_, sourceTexture, sw, sh) = levels[level];
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.Viewport(0, 0, w, h);
            _bloom.Set("uSourceTexel", 1f / sw, 1f / sh);
            BindTexture2D(0, sourceTexture);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stats.PostPasses++;
        }

        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
    }

    private void MarkSelection(in ViewSetup view)
    {
        var any = false;
        foreach (var item in _scene.Items)
        {
            if (item.Selected)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        GL.Enable(EnableCap.StencilTest);
        GL.StencilFunc(StencilFunction.Always, 1, 0xFF);
        GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);
        GL.StencilMask(0xFF);
        GL.ColorMask(false, false, false, false);
        GL.DepthMask(false);
        GL.Disable(EnableCap.DepthTest);
        _depth.Draw(_scene.Items, view.ViewProjection, item => item.Selected);
        _depth.TakeDrawCalls();
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.StencilTest);
    }

    // ---- uniforms and state ----

    private void SetOutputUniforms(GlProgram p)
    {
        var atmosphere = _atmosphereSettings;
        var saturation = atmosphere.Enabled ? atmosphere.Saturation : 1f;
        var contrast = atmosphere.Enabled ? atmosphere.Contrast : 1f;
        p.Set("uOutput", _exposure, saturation, contrast, (int)_settings.ToneMapping);
        p.Set("uWhiteBalance", WhiteBalance(_settings.Temperature, _settings.Tint));
    }

    /// <summary>Per-channel gains for a simple white balance (temperature: blue to orange; tint: green to magenta), luminance kept.</summary>
    public static Vector3 WhiteBalance(float temperature, float tint)
    {
        var gains = new Vector3(1f + 0.18f * temperature, 1f - 0.12f * tint, 1f - 0.18f * temperature);
        var luminance = 0.2126f * gains.X + 0.7152f * gains.Y + 0.0722f * gains.Z;
        return gains / MathF.Max(luminance, 1e-3f);
    }

    // The Atmosphere parameters with every colour in linear light (the sky disc boosted so it can bloom).
    private void WriteAtmosphere(in ViewSetup view)
    {
        var atmosphereView = new AtmosphereView(view.Position, view.Forward, view.Right, view.Up, view.VerticalFovRadians, view.Aspect);
        AtmosphereUniforms.Write(_atmosphereSettings, atmosphereView, _atmosphere);
        foreach (var slot in (ReadOnlySpan<int>)[1, 2, 3, 4, 5, 8, 12])
        {
            for (var c = 0; c < 3; c++)
            {
                var v = _atmosphere[slot * 4 + c];
                _atmosphere[slot * 4 + c] = v <= 1f ? ColorSpace.SrgbToLinear(v) : MathF.Pow(v, 2.2f);
            }
        }

        // The sun disc: brighter than the sky so it reads as the light source (and blooms when Bloom is on).
        for (var c = 0; c < 3; c++)
        {
            _atmosphere[48 + c] *= 6f;
        }
    }

    private void UploadLights()
    {
        Array.Clear(_lightData);
        var lights = _scene.Lights;
        for (var i = 0; i < lights.Count && i < SceneCollector.MaxLights; i++)
        {
            var l = lights[i];
            var o = i * 16;
            _lightData[o] = l.Position.X;
            _lightData[o + 1] = l.Position.Y;
            _lightData[o + 2] = l.Position.Z;
            _lightData[o + 3] = l.Range;
            _lightData[o + 4] = l.Radiance.X;
            _lightData[o + 5] = l.Radiance.Y;
            _lightData[o + 6] = l.Radiance.Z;
            _lightData[o + 7] = (int)l.Type switch { 0 => 0f, 1 => 1f, _ => 2f };
            _lightData[o + 8] = l.Direction.X;
            _lightData[o + 9] = l.Direction.Y;
            _lightData[o + 10] = l.Direction.Z;
            _lightData[o + 11] = l.CosOuter;
            _lightData[o + 12] = l.CosInner;
            _lightData[o + 13] = l.ShadowSlot;
        }

        GL.BindBuffer(BufferTarget.UniformBuffer, _lightUbo);
        GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, lights.Count * 16 * sizeof(float), _lightData);
        GL.BindBuffer(BufferTarget.UniformBuffer, 0);
    }

    private static void SetBaselineState()
    {
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.Disable(EnableCap.Blend);
        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.StencilTest);
        GL.StencilMask(0xFF);
    }

    private void BindTexture(int unit, TextureTarget target, int id)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(target, id);
        _boundTextures[unit] = id;
    }

    private void BindTexture2D(int unit, int id)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, id);
        _boundTextures[unit] = id;
    }

    private static int MaxSamples()
    {
        GL.GetInteger(GetPName.MaxSamples, out var max);
        return global::System.Math.Clamp(max, 1, 4);
    }

    private static Matrix4 InvertProjection(in Matrix4 p)
    {
        var m = new System.Numerics.Matrix4x4(
            p.M11, p.M12, p.M13, p.M14,
            p.M21, p.M22, p.M23, p.M24,
            p.M31, p.M32, p.M33, p.M34,
            p.M41, p.M42, p.M43, p.M44);
        if (!System.Numerics.Matrix4x4.Invert(m, out var inv))
        {
            return Matrix4.Identity;
        }

        return new Matrix4(
            inv.M11, inv.M12, inv.M13, inv.M14,
            inv.M21, inv.M22, inv.M23, inv.M24,
            inv.M31, inv.M32, inv.M33, inv.M34,
            inv.M41, inv.M42, inv.M43, inv.M44);
    }

    private void BuildSsaoKernel()
    {
        // Hemisphere samples (z up), deterministic, denser near the centre.
        var random = new Random(1234);
        for (var i = 0; i < 16; i++)
        {
            var v = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 0.9f + 0.1f).Normalized();
            var scale = i / 16f;
            scale = 0.15f + 0.85f * scale * scale;
            v *= scale;
            _ssaoKernel[i * 3] = v.X;
            _ssaoKernel[i * 3 + 1] = v.Y;
            _ssaoKernel[i * 3 + 2] = v.Z;
        }
    }

    private void CreateDummies()
    {
        // Valid textures of every sampler type, so unused shadow and environment samplers never read unit 0's texture.
        _dummySpotShadow = GlResources.DepthTexture2D(1, 1, stencil: false, compare: true);

        _dummySunShadow = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, _dummySunShadow);
        GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.DepthComponent24, 1, 1, 1, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)All.Lequal);

        _dummyPointShadow = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, _dummyPointShadow);
        for (var face = 0; face < 6; face++)
        {
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.DepthComponent24, 1, 1, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, IntPtr.Zero);
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureCompareFunc, (int)All.Lequal);

        _dummyEnvironment = GL.GenTexture();
        GL.BindTexture(TextureTarget.TextureCubeMap, _dummyEnvironment);
        var black = new byte[] { 0, 0, 0, 255 };
        for (var face = 0; face < 6; face++)
        {
            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, black);
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindTexture(TextureTarget.TextureCubeMap, 0);
        GL.BindTexture(TextureTarget.Texture2DArray, 0);
    }

    public void Dispose()
    {
        _targets.Dispose();
        _shadows.Dispose();
        _environment.Dispose();
        _meshes.Dispose();
        _textures.Dispose();
        _bones.Dispose();
        _samplers.Dispose();
        _shaders.Dispose();
        _white?.Dispose();
        _flatNormal?.Dispose();
        GlResources.DeleteTexture(ref _dummySpotShadow);
        foreach (var id in new[] { _dummySunShadow, _dummyPointShadow, _dummyEnvironment })
        {
            if (id != 0)
            {
                GL.DeleteTexture(id);
            }
        }

        _dummySunShadow = _dummyPointShadow = _dummyEnvironment = 0;
        if (_lightUbo != 0)
        {
            GL.DeleteBuffer(_lightUbo);
            _lightUbo = 0;
        }

        if (_fullscreenVao != 0)
        {
            GL.DeleteVertexArray(_fullscreenVao);
            GL.DeleteBuffer(_fullscreenVbo);
            _fullscreenVao = _fullscreenVbo = 0;
        }

        Healthy = false;
    }
}
