namespace Wisko.Engine.Text;

/// <summary>One glyph quad in LOGICAL pixels (top-left origin) plus its atlas UV rect.</summary>
public readonly struct PositionedGlyph
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public AtlasUv Uv { get; init; }
    public bool IsFallback { get; init; }
}

/// <summary>Measured extent of a text run, in LOGICAL pixels.</summary>
public readonly struct TextMeasurement
{
    public float Width { get; init; }
    public float Height { get; init; }   // line height
    public float Baseline { get; init; } // top → baseline
    public float Ascent { get; init; }
    public float Descent { get; init; }  // negative
}

/// <summary>
/// Pure text layout. Converts a string into a measurement or baseline-relative positioned glyph
/// quads, expressed in LOGICAL pixels, while rasterizing glyphs at DEVICE pixels through the
/// <see cref="FontLibrary"/>. This keeps layout resolution-independent while text stays crisp on
/// HiDPI: a glyph rasterized at <c>logicalHeight × dpiScale</c> device px is emitted at its logical
/// size (<c>devicePx / dpiScale</c>).
///
/// Spacing is advance-only (no pair kerning): stb reads only the legacy <c>kern</c> table and Inter's
/// kerning lives in GPOS, so kerning is deferred. The legacy scale value maps as
/// <c>logicalPixelHeight = 8 × legacyScale</c> — the single approved conversion; no new public
/// pixel-size API is introduced.
/// </summary>
public static class TextLayout
{
    /// <summary>Approved legacy mapping: logical pixel height per unit of the old <c>scale</c> value.</summary>
    public const float LogicalPixelHeightPerScaleUnit = 8f;

    public static float LogicalPixelHeight(float legacyScale) => LogicalPixelHeightPerScaleUnit * legacyScale;

    /// <summary>Device-pixel size bucket used as the glyph-cache key (rasterization resolution).</summary>
    public static int DeviceSizeBucket(float legacyScale, float dpiScale) =>
        global::System.Math.Max(1, (int)MathF.Round(LogicalPixelHeight(legacyScale) * dpiScale));

    public static TextMeasurement Measure(
        FontLibrary fonts,
        FontWeight weight,
        string text,
        float legacyScale,
        float dpiScale)
    {
        var face = fonts.Face(weight);
        var device = DeviceSizeBucket(legacyScale, dpiScale);
        var deviceScale = face.ScaleForPixelHeight(device);
        var toLogical = 1f / dpiScale;

        var widthDevice = 0f;
        foreach (var rune in text.EnumerateRunes())
        {
            widthDevice += fonts.EnsureGlyph(weight, rune.Value, device).AdvancePx;
        }

        return new TextMeasurement
        {
            Width = widthDevice * toLogical,
            Height = face.LineHeightPx(deviceScale) * toLogical,
            Baseline = face.AscentPx(deviceScale) * toLogical,
            Ascent = face.AscentPx(deviceScale) * toLogical,
            Descent = face.DescentPx(deviceScale) * toLogical,
        };
    }

    /// <summary>
    /// Produces baseline-relative glyph quads with (<paramref name="originX"/>, <paramref name="originY"/>)
    /// as the TOP-LEFT of the text block. Whitespace advances the pen but emits no quad.
    /// </summary>
    public static List<PositionedGlyph> BuildQuads(
        FontLibrary fonts,
        FontWeight weight,
        string text,
        float legacyScale,
        float dpiScale,
        float originX,
        float originY)
    {
        var face = fonts.Face(weight);
        var device = DeviceSizeBucket(legacyScale, dpiScale);
        var deviceScale = face.ScaleForPixelHeight(device);
        var toLogical = 1f / dpiScale;
        var baseline = originY + face.AscentPx(deviceScale) * toLogical;

        var quads = new List<PositionedGlyph>(text.Length);
        var penX = originX;
        // By code point: a surrogate pair is one glyph (an unpaired surrogate becomes U+FFFD).
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = fonts.EnsureGlyph(weight, rune.Value, device);
            if (glyph.HasPixels)
            {
                quads.Add(new PositionedGlyph
                {
                    X = penX + glyph.BearingXPx * toLogical,
                    Y = baseline - glyph.BearingYPx * toLogical,
                    Width = glyph.PixelWidth * toLogical,
                    Height = glyph.PixelHeight * toLogical,
                    Uv = glyph.Uv,
                    IsFallback = glyph.IsFallback,
                });
            }

            penX += glyph.AdvancePx * toLogical;
        }

        return quads;
    }
}
