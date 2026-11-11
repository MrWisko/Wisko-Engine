using System.Runtime.InteropServices;
using StbTrueTypeSharp;

namespace Wisko.Engine.Text;

/// <summary>
/// Identifies a bundled face: the Inter weights used by the editor UI, or the monospace code font.
/// </summary>
public enum FontWeight
{
    Regular,
    SemiBold,

    /// <summary>JetBrains Mono Regular: fixed-width text for the script editor.</summary>
    Monospace
}

/// <summary>
/// A single typeface (e.g. Inter Regular). Owns the pinned TTF bytes and the StbTrueTypeSharp
/// <c>stbtt_fontinfo</c> for its whole lifetime and frees the pin on <see cref="Dispose"/>.
/// This type is pure font data + metrics + CPU rasterization; it performs no OpenGL work.
///
/// Kerning note: StbTrueTypeSharp only reads the legacy <c>kern</c> table. Inter stores its
/// kerning in GPOS, which stb does not parse, so <see cref="AdvancePx"/> is unkerned. Layout is
/// therefore advance-only for now (see <see cref="TextLayout"/>); GPOS shaping is deferred.
/// </summary>
public sealed class FontFace : IDisposable
{
    private readonly byte[] _ttf;
    private GCHandle _pin;
    private readonly StbTrueType.stbtt_fontinfo _info;
    private readonly int _ascentUnits;
    private readonly int _descentUnits;
    private readonly int _lineGapUnits;
    private bool _disposed;

    private FontFace(byte[] ttf, GCHandle pin, StbTrueType.stbtt_fontinfo info, int ascent, int descent, int lineGap)
    {
        _ttf = ttf;
        _pin = pin;
        _info = info;
        _ascentUnits = ascent;
        _descentUnits = descent;
        _lineGapUnits = lineGap;
    }

    /// <summary>Loads a face from raw TTF bytes. The bytes are pinned for the face's lifetime.</summary>
    public static FontFace Load(byte[] ttf)
    {
        var pin = GCHandle.Alloc(ttf, GCHandleType.Pinned);
        var info = new StbTrueType.stbtt_fontinfo();
        int ascent, descent, lineGap;
        unsafe
        {
            var data = (byte*)pin.AddrOfPinnedObject();
            if (StbTrueType.stbtt_InitFont(info, data, StbTrueType.stbtt_GetFontOffsetForIndex(data, 0)) == 0)
            {
                pin.Free();
                throw new InvalidOperationException("stbtt_InitFont failed for the supplied font bytes.");
            }

            StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &lineGap);
        }

        return new FontFace(ttf, pin, info, ascent, descent, lineGap);
    }

    /// <summary>stb scale factor mapping font units to the given device pixel height.</summary>
    public float ScaleForPixelHeight(float devicePixelHeight) =>
        StbTrueType.stbtt_ScaleForPixelHeight(_info, devicePixelHeight);

    public float AscentPx(float scale) => _ascentUnits * scale;

    /// <summary>Descent in pixels (negative: below the baseline).</summary>
    public float DescentPx(float scale) => _descentUnits * scale;

    public float LineHeightPx(float scale) => (_ascentUnits - _descentUnits + _lineGapUnits) * scale;

    /// <summary>True when the face actually contains a glyph for the codepoint (index != 0).</summary>
    public bool HasGlyph(int codepoint) => StbTrueType.stbtt_FindGlyphIndex(_info, codepoint) != 0;

    public float AdvancePx(int codepoint, float scale)
    {
        int advance, leftSideBearing;
        unsafe
        {
            StbTrueType.stbtt_GetCodepointHMetrics(_info, codepoint, &advance, &leftSideBearing);
        }

        return advance * scale;
    }

    /// <summary>
    /// Rasterizes a codepoint at the given stb scale into a fresh top-down coverage buffer
    /// (row 0 = glyph top, one byte per pixel). Returns false for whitespace / empty glyphs
    /// (no pixels), in which case <paramref name="advance"/> is still valid.
    /// <paramref name="bearingX"/> is the left side bearing (px); <paramref name="bearingY"/> is
    /// the distance from the baseline UP to the glyph top (px, positive up).
    /// </summary>
    public bool Rasterize(
        int codepoint,
        float scale,
        out byte[] coverage,
        out int width,
        out int height,
        out float bearingX,
        out float bearingY,
        out float advance)
    {
        int x0, y0, x1, y1, advanceUnits, leftSideBearing;
        unsafe
        {
            StbTrueType.stbtt_GetCodepointBitmapBox(_info, codepoint, scale, scale, &x0, &y0, &x1, &y1);
            StbTrueType.stbtt_GetCodepointHMetrics(_info, codepoint, &advanceUnits, &leftSideBearing);
        }

        advance = advanceUnits * scale;
        width = x1 - x0;
        height = y1 - y0;
        bearingX = x0;   // left edge of the ink relative to the pen
        bearingY = -y0;  // y0 is negative above the baseline; top-above-baseline = -y0

        if (width <= 0 || height <= 0)
        {
            coverage = Array.Empty<byte>();
            width = 0;
            height = 0;
            return false;
        }

        coverage = new byte[width * height];
        unsafe
        {
            fixed (byte* destination = coverage)
            {
                StbTrueType.stbtt_MakeCodepointBitmap(_info, destination, width, height, width, scale, scale, codepoint);
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}
