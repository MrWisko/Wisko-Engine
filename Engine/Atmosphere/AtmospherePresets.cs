namespace Wisko.Engine.Atmosphere;

/// <summary>A named starting point: applying it replaces the settings with ordinary, fully editable values.</summary>
public sealed record AtmospherePreset(string Name, AtmosphereSettings Settings);

/// <summary>
/// The built-in Atmosphere starting points, from bright and stylized to dark. Nothing remembers which preset was
/// applied: the project only keeps the resulting settings.
/// </summary>
public static class AtmospherePresets
{
    public static IReadOnlyList<AtmospherePreset> All { get; } =
    [
        new("Neutral", AtmosphereSettings.Default),
        new("Bright Stylized", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(64, 140, 255),
            HorizonColor = AtmosphereColor.FromBytes(186, 226, 255),
            SkyIntensity = 1.05f,
            SunColor = AtmosphereColor.FromBytes(255, 242, 210),
            SunIntensity = 1.05f,
            SunElevation = 60f,
            SunHeading = 40f,
            SunDiscSize = 1.4f,
            AmbientColor = AtmosphereColor.FromBytes(215, 225, 255),
            AmbientIntensity = 0.42f,
            Haze = 0.12f,
                        Saturation = 1.3f,
            Contrast = 1.05f
        }.Sanitized()),
        new("Grounded", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(110, 146, 196),
            HorizonColor = AtmosphereColor.FromBytes(206, 214, 220),
            SkyIntensity = 0.95f,
            SunColor = AtmosphereColor.FromBytes(255, 238, 214),
            SunIntensity = 1.05f,
            SunElevation = 38f,
            SunHeading = 60f,
            SunDiscSize = 0.45f,
            AmbientColor = AtmosphereColor.FromBytes(190, 198, 210),
            AmbientIntensity = 0.3f,
            FogEnabled = true,
            FogColor = AtmosphereColor.FromBytes(196, 204, 212),
            FogDensity = 0.35f,
            FogStart = 180f,
            FogEnd = 1400f,
            Haze = 0.45f,
            Saturation = 0.88f,
            Contrast = 1.08f
        }.Sanitized()),
        new("Golden Hour", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(88, 120, 190),
            HorizonColor = AtmosphereColor.FromBytes(255, 186, 128),
            SunColor = AtmosphereColor.FromBytes(255, 176, 104),
            SunIntensity = 1.25f,
            SunElevation = 12f,
            SunHeading = 75f,
            SunDiscSize = 1.3f,
            AmbientColor = AtmosphereColor.FromBytes(190, 160, 170),
            AmbientIntensity = 0.45f,
            FogEnabled = true,
            FogColor = AtmosphereColor.FromBytes(240, 190, 150),
            FogDensity = 0.25f,
            FogStart = 200f,
            FogEnd = 1600f,
            Haze = 0.4f,
            Exposure = 0.05f,
            Saturation = 1.12f,
            Contrast = 1.04f
        }.Sanitized()),
        new("Night", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(8, 14, 34),
            HorizonColor = AtmosphereColor.FromBytes(26, 38, 70),
            SunColor = AtmosphereColor.FromBytes(150, 170, 230),
            SunIntensity = 0.5f,
            SunElevation = 35f,
            SunHeading = 210f,
            SunDiscSize = 0.6f,
            AmbientColor = AtmosphereColor.FromBytes(100, 120, 190),
            AmbientIntensity = 0.3f,
            FogEnabled = true,
            FogColor = AtmosphereColor.FromBytes(14, 20, 38),
            FogDensity = 0.55f,
            FogStart = 80f,
            FogEnd = 900f,
            Haze = 0.2f,
            Exposure = 0.2f,
            Saturation = 0.85f,
            Contrast = 1.05f
        }.Sanitized()),
        new("Moody", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(92, 100, 112),
            HorizonColor = AtmosphereColor.FromBytes(150, 154, 158),
            SkyIntensity = 0.85f,
            SunColor = AtmosphereColor.FromBytes(210, 214, 225),
            SunIntensity = 0.55f,
            SunElevation = 28f,
            SunHeading = 120f,
            SunDiscSize = 0f,
            AmbientColor = AtmosphereColor.FromBytes(150, 158, 172),
            AmbientIntensity = 0.38f,
            FogEnabled = true,
            FogColor = AtmosphereColor.FromBytes(120, 126, 134),
            FogDensity = 0.7f,
            FogStart = 40f,
            FogEnd = 650f,
            Haze = 0.5f,
            Exposure = -0.3f,
            Saturation = 0.65f,
            Contrast = 1.12f
        }.Sanitized()),
        new("Dreamlike", new AtmosphereSettings
        {
            SkyColor = AtmosphereColor.FromBytes(140, 90, 220),
            HorizonColor = AtmosphereColor.FromBytes(255, 190, 220),
            SkyIntensity = 1.05f,
            SunColor = AtmosphereColor.FromBytes(230, 255, 220),
            SunElevation = 45f,
            SunHeading = 300f,
            SunDiscSize = 2.2f,
            AmbientColor = AtmosphereColor.FromBytes(200, 170, 255),
            AmbientIntensity = 0.45f,
            FogEnabled = true,
            FogColor = AtmosphereColor.FromBytes(250, 200, 230),
            FogDensity = 0.3f,
            FogStart = 150f,
            FogEnd = 1200f,
            Haze = 0.35f,
            Exposure = 0.1f,
            Saturation = 1.2f,
            Contrast = 0.95f
        }.Sanitized())
    ];

    public static AtmospherePreset? Find(string name) =>
        All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
}
