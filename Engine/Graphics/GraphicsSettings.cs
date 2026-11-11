using Wisko.Engine.Math;

namespace Wisko.Engine.Graphics;

/// <summary>Which renderer draws a project's world.</summary>
public enum RendererKind
{
    /// <summary>
    /// Wisko's original renderer, unchanged: colours, the Atmosphere sun and ambient, fog and haze. No
    /// textures, lights, shadows or post-processing. Projects saved before Graphics.json existed keep it until switched; the
    /// macOS (bgfx) build always uses it.
    /// </summary>
    Classic,

    /// <summary>Windows (OpenGL): PBR materials and textures, lights, shadows, HDR and post-processing.</summary>
    Universal
}

public enum ToneMapping
{
    /// <summary>Colours above 1 are clipped (exposure still applies).</summary>
    None,

    /// <summary>Keeps colours as authored up to about 0.8 and rolls highlights off softly (Khronos PBR Neutral).</summary>
    Neutral,

    /// <summary>A filmic curve (ACES fitted): more contrast, desaturated bright highlights.</summary>
    Filmic
}

public enum QualityLevel
{
    Low,
    Medium,
    High,
    Ultra,

    /// <summary>The project's own <see cref="GraphicsSettings.Custom"/> values.</summary>
    Custom
}

public enum AntiAliasing
{
    /// <summary>Hardware 4x multisampling (geometry edges); Wisko's default.</summary>
    Msaa,

    /// <summary>FXAA, a post-process edge blur: also smooths edges inside textures and alpha cut-outs, softer overall.</summary>
    Fxaa,

    /// <summary>Both.</summary>
    MsaaAndFxaa,

    None
}

/// <summary>
/// The cost-related rendering values that a <see cref="QualityLevel"/> chooses. Every value can be overridden through
/// <see cref="QualityLevel.Custom"/>; nothing here changes how a project looks apart from detail and smoothness.
/// </summary>
public sealed record QualitySettings
{
    /// <summary>False: the light-weight path (no HDR targets, bloom or ambient occlusion; tone mapping in the shaders).</summary>
    public bool PostProcessing { get; init; } = true;

    /// <summary>Sun shadow map size per cascade (512-4096).</summary>
    public int ShadowResolution { get; init; } = 2048;

    /// <summary>Sun shadow cascades (1-4): more keeps shadows sharp near the camera over a longer distance.</summary>
    public int ShadowCascades { get; init; } = 2;

    /// <summary>Shadow edge filtering: 1 hard, 4, 9 or 16 samples (softer and smoother, more cost).</summary>
    public int ShadowFilterSamples { get; init; } = 4;

    /// <summary>Multiplier on the project's shadow distance (0.25-1).</summary>
    public float ShadowDistanceScale { get; init; } = 0.75f;

    /// <summary>Most spot lights with shadows at once (0-8).</summary>
    public int MaxSpotShadows { get; init; } = 2;

    public int SpotShadowResolution { get; init; } = 512;

    /// <summary>Most point lights with shadows at once (0-2): each costs six extra views of the scene.</summary>
    public int MaxPointShadows { get; init; }

    public int PointShadowResolution { get; init; } = 256;

    /// <summary>Screen-space ambient occlusion samples per pixel (0 off, 4-16).</summary>
    public int AmbientOcclusionSamples { get; init; } = 8;

    public bool AmbientOcclusionHalfResolution { get; init; } = true;

    /// <summary>Bloom blur levels (0 off, 3-6): more spreads glow wider.</summary>
    public int BloomLevels { get; init; } = 4;

    /// <summary>Size of each face of the environment reflection cube map (16-256).</summary>
    public int ReflectionResolution { get; init; } = 64;

    /// <summary>Most lights affecting one object (1-16); the strongest are kept.</summary>
    public int MaxLightsPerObject { get; init; } = 8;

    /// <summary>Resolution of the 3D image relative to the window (0.5-1); the editor and game GUI stay sharp.</summary>
    public float RenderScale { get; init; } = 1f;

    public static QualitySettings Low { get; } = new()
    {
        PostProcessing = false, ShadowResolution = 1024, ShadowCascades = 1, ShadowFilterSamples = 1, ShadowDistanceScale = 0.5f,
        MaxSpotShadows = 0, SpotShadowResolution = 512, MaxPointShadows = 0, PointShadowResolution = 256,
        AmbientOcclusionSamples = 0, AmbientOcclusionHalfResolution = true, BloomLevels = 0, ReflectionResolution = 32,
        MaxLightsPerObject = 4, RenderScale = 1f
    };

    public static QualitySettings Medium { get; } = new();

    public static QualitySettings High { get; } = new()
    {
        PostProcessing = true, ShadowResolution = 2048, ShadowCascades = 3, ShadowFilterSamples = 9, ShadowDistanceScale = 1f,
        MaxSpotShadows = 4, SpotShadowResolution = 1024, MaxPointShadows = 1, PointShadowResolution = 256,
        AmbientOcclusionSamples = 12, AmbientOcclusionHalfResolution = true, BloomLevels = 5, ReflectionResolution = 128,
        MaxLightsPerObject = 8, RenderScale = 1f
    };

    public static QualitySettings Ultra { get; } = new()
    {
        PostProcessing = true, ShadowResolution = 4096, ShadowCascades = 4, ShadowFilterSamples = 16, ShadowDistanceScale = 1f,
        MaxSpotShadows = 8, SpotShadowResolution = 1024, MaxPointShadows = 2, PointShadowResolution = 512,
        AmbientOcclusionSamples = 16, AmbientOcclusionHalfResolution = false, BloomLevels = 6, ReflectionResolution = 256,
        MaxLightsPerObject = 16, RenderScale = 1f
    };

    public static QualitySettings For(QualityLevel level, QualitySettings custom) => level switch
    {
        QualityLevel.Low => Low,
        QualityLevel.Medium => Medium,
        QualityLevel.High => High,
        QualityLevel.Ultra => Ultra,
        _ => custom
    };

    /// <summary>Every value in its range (sizes snapped to powers of two, sample counts to the supported ones). Idempotent.</summary>
    public QualitySettings Sanitized() => new()
    {
        PostProcessing = PostProcessing,
        ShadowResolution = PowerOfTwo(ShadowResolution, 512, 4096),
        ShadowCascades = global::System.Math.Clamp(ShadowCascades, 1, 4),
        ShadowFilterSamples = ShadowFilterSamples switch { <= 1 => 1, <= 4 => 4, <= 9 => 9, _ => 16 },
        ShadowDistanceScale = MathUtil.ClampFinite(ShadowDistanceScale, 0.25f, 1f, 0.75f),
        MaxSpotShadows = global::System.Math.Clamp(MaxSpotShadows, 0, 8),
        SpotShadowResolution = PowerOfTwo(SpotShadowResolution, 256, 2048),
        MaxPointShadows = global::System.Math.Clamp(MaxPointShadows, 0, 2),
        PointShadowResolution = PowerOfTwo(PointShadowResolution, 128, 1024),
        AmbientOcclusionSamples = AmbientOcclusionSamples <= 0 ? 0 : global::System.Math.Clamp(AmbientOcclusionSamples, 4, 16),
        AmbientOcclusionHalfResolution = AmbientOcclusionHalfResolution,
        BloomLevels = BloomLevels <= 0 ? 0 : global::System.Math.Clamp(BloomLevels, 3, 6),
        ReflectionResolution = PowerOfTwo(ReflectionResolution, 16, 256),
        MaxLightsPerObject = global::System.Math.Clamp(MaxLightsPerObject, 1, 16),
        RenderScale = MathUtil.ClampFinite(RenderScale, 0.5f, 1f, 1f)
    };

    private static int PowerOfTwo(int value, int min, int max)
    {
        var v = global::System.Math.Clamp(value, min, max);
        var p = min;
        while (p * 2 <= v)
        {
            p *= 2;
        }

        // Round to the nearer power of two.
        return p * 2 <= max && v - p > p * 2 - v ? p * 2 : p;
    }
}

/// <summary>
/// A project's rendering settings (Graphics.json, next to Scene.json): which renderer, the quality level, and the
/// post-processing and shadow settings of the Universal renderer. Plain data with no renderer types. The environment
/// itself (sky, sun, ambient light, fog, exposure) stays in the Atmosphere.
/// <para><see cref="Default"/> is a new project's starting point: the Universal renderer at Medium quality with
/// neutral tone mapping, sun shadows and soft ambient occlusion, and no bloom or vignette (nothing cinematic is
/// forced). A project without Graphics.json is <see cref="LegacyDefault"/> (Classic), so it looks exactly as before.</para>
/// </summary>
public sealed record GraphicsSettings
{
    public static GraphicsSettings Default { get; } = new();

    /// <summary>An older project (no Graphics.json): the Classic renderer (the other values wait for a switch).</summary>
    public static GraphicsSettings LegacyDefault { get; } = new() { Renderer = RendererKind.Classic };

    public RendererKind Renderer { get; init; } = RendererKind.Universal;

    public QualityLevel Quality { get; init; } = QualityLevel.Medium;

    /// <summary>The values <see cref="QualityLevel.Custom"/> uses (kept while another level is chosen).</summary>
    public QualitySettings Custom { get; init; } = QualitySettings.Medium;

    public AntiAliasing AntiAliasing { get; init; } = AntiAliasing.Msaa;

    public ToneMapping ToneMapping { get; init; } = ToneMapping.Neutral;

    public bool BloomEnabled { get; init; }

    /// <summary>
    /// Brightness above which light blooms (0-10; 1 = brighter than white). The default, 1.5, leaves sunlit white surfaces
    /// alone and blooms glowing materials, the sun and bright highlights.
    /// </summary>
    public float BloomThreshold { get; init; } = 1.5f;

    public float BloomIntensity { get; init; } = 0.5f;

    /// <summary>How far the glow spreads (0-1).</summary>
    public float BloomRadius { get; init; } = 0.6f;

    public bool VignetteEnabled { get; init; }

    public float VignetteIntensity { get; init; } = 0.35f;

    /// <summary>White balance: -1 cooler (blue) to 1 warmer (orange).</summary>
    public float Temperature { get; init; }

    /// <summary>White balance: -1 green to 1 magenta.</summary>
    public float Tint { get; init; }

    public bool AmbientOcclusionEnabled { get; init; } = true;

    /// <summary>Studs: how far around a point occluders are looked for.</summary>
    public float AmbientOcclusionRadius { get; init; } = 1.5f;

    public float AmbientOcclusionIntensity { get; init; } = 1f;

    public bool ShadowsEnabled { get; init; } = true;

    /// <summary>Studs from the camera that sun shadows reach (before the quality's distance scale).</summary>
    public float ShadowDistance { get; init; } = 200f;

    /// <summary>Depth bias against shadow acne (in shadow-map texels).</summary>
    public float ShadowBias { get; init; } = 1f;

    /// <summary>Offset along the surface normal against acne on steep surfaces (in shadow-map texels).</summary>
    public float ShadowNormalBias { get; init; } = 1.5f;

    /// <summary>Point lights may cast shadows at all (each still limited by the quality level).</summary>
    public bool PointLightShadows { get; init; } = true;

    /// <summary>The quality values in effect for a level (the project's own or an editor override).</summary>
    public QualitySettings QualityFor(QualityLevel level) => QualitySettings.For(level, Custom).Sanitized();

    public QualitySettings EffectiveQuality => QualityFor(Quality);

    /// <summary>Every value in range and finite (non-finite: the default). Idempotent.</summary>
    public GraphicsSettings Sanitized()
    {
        var d = Default;
        return new GraphicsSettings
        {
            Renderer = Enum.IsDefined(Renderer) ? Renderer : RendererKind.Classic,
            Quality = Enum.IsDefined(Quality) ? Quality : d.Quality,
            Custom = (Custom ?? QualitySettings.Medium).Sanitized(),
            AntiAliasing = Enum.IsDefined(AntiAliasing) ? AntiAliasing : d.AntiAliasing,
            ToneMapping = Enum.IsDefined(ToneMapping) ? ToneMapping : d.ToneMapping,
            BloomEnabled = BloomEnabled,
            BloomThreshold = MathUtil.ClampFinite(BloomThreshold, 0f, 10f, d.BloomThreshold),
            BloomIntensity = MathUtil.ClampFinite(BloomIntensity, 0f, 5f, d.BloomIntensity),
            BloomRadius = MathUtil.ClampFinite(BloomRadius, 0f, 1f, d.BloomRadius),
            VignetteEnabled = VignetteEnabled,
            VignetteIntensity = MathUtil.ClampFinite(VignetteIntensity, 0f, 1f, d.VignetteIntensity),
            Temperature = MathUtil.ClampFinite(Temperature, -1f, 1f, d.Temperature),
            Tint = MathUtil.ClampFinite(Tint, -1f, 1f, d.Tint),
            AmbientOcclusionEnabled = AmbientOcclusionEnabled,
            AmbientOcclusionRadius = MathUtil.ClampFinite(AmbientOcclusionRadius, 0.1f, 10f, d.AmbientOcclusionRadius),
            AmbientOcclusionIntensity = MathUtil.ClampFinite(AmbientOcclusionIntensity, 0f, 4f, d.AmbientOcclusionIntensity),
            ShadowsEnabled = ShadowsEnabled,
            ShadowDistance = MathUtil.ClampFinite(ShadowDistance, 10f, 2000f, d.ShadowDistance),
            ShadowBias = MathUtil.ClampFinite(ShadowBias, 0f, 10f, d.ShadowBias),
            ShadowNormalBias = MathUtil.ClampFinite(ShadowNormalBias, 0f, 10f, d.ShadowNormalBias),
            PointLightShadows = PointLightShadows
        };
    }
}
