
namespace Wisko.Engine.Text;

/// <summary>Cache key: a glyph is uniquely identified by face, codepoint and device-pixel size.</summary>
public readonly struct GlyphKey : IEquatable<GlyphKey>
{
    public GlyphKey(FontWeight weight, int codepoint, int devicePixelSize)
    {
        Weight = weight;
        Codepoint = codepoint;
        DevicePixelSize = devicePixelSize;
    }

    public FontWeight Weight { get; }
    public int Codepoint { get; }
    public int DevicePixelSize { get; }

    public bool Equals(GlyphKey other) =>
        Weight == other.Weight && Codepoint == other.Codepoint && DevicePixelSize == other.DevicePixelSize;

    public override bool Equals(object? obj) => obj is GlyphKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)Weight, Codepoint, DevicePixelSize);
}

/// <summary>
/// A resident glyph: its atlas UV rect plus device-pixel size / bearing / advance metrics.
/// <see cref="HasPixels"/> is false for whitespace (advance only). <see cref="IsFallback"/> marks
/// the visible box substituted for codepoints the face does not contain.
/// </summary>
public readonly struct Glyph
{
    public AtlasUv Uv { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public float BearingXPx { get; init; }
    public float BearingYPx { get; init; }
    public float AdvancePx { get; init; }
    public bool HasPixels { get; init; }
    public bool IsFallback { get; init; }
}

/// <summary>
/// Shared production font resource. Owns the bundled faces (Inter Regular/SemiBold, JetBrains Mono) and one <see cref="GlyphAtlas"/>,
/// and caches rasterized glyphs by <see cref="GlyphKey"/>. Glyphs are inserted on demand (and via
/// <see cref="PrewarmAscii"/>) while free atlas space remains. Codepoints the face lacks resolve to
/// a visible fallback box — never a silent drop.
///
/// GL note: <see cref="Initialize"/>, <see cref="PrewarmAscii"/> and <see cref="EnsureGlyph"/> upload
/// to the GL atlas and require an active GL context. Stage 1 constructs and validates this type
/// independently; it is NOT owned or initialized by the renderer yet.
/// </summary>
public sealed class FontLibrary : IDisposable
{
    private const string RegularResource = "Engine.Fonts.Inter-Regular.ttf";
    private const string SemiBoldResource = "Engine.Fonts.Inter-SemiBold.ttf";
    private const string MonospaceResource = "Engine.Fonts.JetBrainsMono-Regular.ttf";
    private const int FallbackCodepoint = -1; // sentinel cache codepoint for the per-size fallback box

    private readonly FontFace _regular;
    private readonly FontFace _semiBold;
    private readonly FontFace _monospace;
    private readonly GlyphAtlas _atlas;
    private readonly Dictionary<GlyphKey, Glyph> _cache = new();
    private bool _initialized;
    private bool _disposed;
    private bool _capacityWarned;

    public FontLibrary()
        : this(new GlyphAtlas())
    {
    }

    /// <summary>Font library whose glyph atlas lives on the given backend surface (e.g. a bgfx texture).</summary>
    internal FontLibrary(IGlyphAtlasSurface atlasSurface)
        : this(new GlyphAtlas(atlasSurface))
    {
    }

    private FontLibrary(GlyphAtlas atlas)
    {
        _atlas = atlas;
        _regular = FontFace.Load(LoadResource(RegularResource));
        _semiBold = FontFace.Load(LoadResource(SemiBoldResource));
        _monospace = FontFace.Load(LoadResource(MonospaceResource));
    }

    public int CachedGlyphCount => _cache.Count;
    public int UsedAtlasRows => _atlas.UsedRows;
    public double AtlasAreaUsedPercent => 100.0 * _atlas.UsedPixels / ((double)GlyphAtlas.Size * GlyphAtlas.Size);
    public int DroppedInsertions { get; private set; }
    public int AtlasTexture => _atlas.TextureHandle;

    /// <summary>A glyph did not fit since the atlas was last cleared (it was drawn as a fallback box).</summary>
    public bool AtlasOverflowed { get; private set; }

    /// <summary>How many times <see cref="ClearAtlas"/> emptied the atlas.</summary>
    public int AtlasClears { get; private set; }

    /// <summary>Creates the GL atlas texture. Requires an active GL context.</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _atlas.Initialize();
        _initialized = true;
    }

    public FontFace Face(FontWeight weight) => weight switch
    {
        FontWeight.SemiBold => _semiBold,
        FontWeight.Monospace => _monospace,
        _ => _regular
    };

    /// <summary>Pre-rasterizes printable ASCII (0x20–0x7E) at each device-pixel size, for both faces.</summary>
    public void PrewarmAscii(IReadOnlyList<int> devicePixelSizes)
    {
        foreach (var weight in new[] { FontWeight.Regular, FontWeight.SemiBold })
        {
            foreach (var size in devicePixelSizes)
            {
                for (var codepoint = 0x20; codepoint <= 0x7E; codepoint++)
                {
                    EnsureGlyph(weight, codepoint, size);
                }

                // Keep a resident fallback box per pre-warmed size so that even if the atlas
                // later fills up, a missing/undrawable glyph can still show something visible.
                EnsureFallback(weight, size);
            }
        }
    }

    /// <summary>
    /// Returns a resident glyph, rasterizing and inserting it on first use. Codepoints the face
    /// does not contain return a visible fallback box (cached, and aliased so repeat lookups are cheap).
    /// </summary>
    public Glyph EnsureGlyph(FontWeight weight, int codepoint, int devicePixelSize)
    {
        var key = new GlyphKey(weight, codepoint, devicePixelSize);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var face = Face(weight);

        // Code text the monospace face cannot draw borrows Inter's glyph (wider scripts) before falling back to a box.
        if (weight == FontWeight.Monospace && codepoint != FallbackCodepoint && !face.HasGlyph(codepoint) && _regular.HasGlyph(codepoint))
        {
            face = _regular;
        }

        if (codepoint != FallbackCodepoint && !face.HasGlyph(codepoint))
        {
            var fallback = EnsureFallback(weight, devicePixelSize);
            _cache[key] = fallback; // alias the missing codepoint to the fallback box
            return fallback;
        }

        var scale = face.ScaleForPixelHeight(devicePixelSize);
        Glyph glyph;
        if (face.Rasterize(codepoint, scale, out var coverage, out var width, out var height,
                out var bearingX, out var bearingY, out var advance))
        {
            if (_atlas.TryInsert(coverage, width, height, out var uv))
            {
                glyph = new Glyph
                {
                    Uv = uv,
                    PixelWidth = width,
                    PixelHeight = height,
                    BearingXPx = bearingX,
                    BearingYPx = bearingY,
                    AdvancePx = advance,
                    HasPixels = true,
                };
            }
            else
            {
                glyph = OnAtlasFull(weight, devicePixelSize, advance);
            }
        }
        else
        {
            glyph = new Glyph { AdvancePx = advance, HasPixels = false }; // whitespace / empty
        }

        _cache[key] = glyph;
        return glyph;
    }

    /// <summary>
    /// Atlas is out of space for a real glyph. Rather than emit invisible advance-only text, reuse
    /// an already-resident fallback box (kept for pre-warmed sizes) so the character stays visible,
    /// preserving the glyph's true advance for stable layout. Emits a one-time capacity diagnostic.
    /// </summary>
    private Glyph OnAtlasFull(FontWeight weight, int devicePixelSize, float advance)
    {
        DroppedInsertions++;
        AtlasOverflowed = true;
        WarnCapacityOnce();

        var fallbackKey = new GlyphKey(weight, FallbackCodepoint, devicePixelSize);
        if (_cache.TryGetValue(fallbackKey, out var fallback) && fallback.HasPixels)
        {
            return fallback with { AdvancePx = advance };
        }

        return new Glyph { AdvancePx = advance, HasPixels = false };
    }

    private void WarnCapacityOnce()
    {
        if (_capacityWarned)
        {
            return;
        }

        _capacityWarned = true;
        Console.WriteLine(
            $"[FontLibrary] Glyph atlas capacity reached ({GlyphAtlas.Size}x{GlyphAtlas.Size}); " +
            "further glyphs render as fallback boxes. Dynamic growth is deferred (Stage 1 scope).");
    }

    private Glyph EnsureFallback(FontWeight weight, int devicePixelSize)
    {
        var key = new GlyphKey(weight, FallbackCodepoint, devicePixelSize);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Synthesize a hollow box roughly at cap height so missing glyphs are clearly visible.
        var face = Face(weight);
        var scale = face.ScaleForPixelHeight(devicePixelSize);
        var height = global::System.Math.Max(4, (int)MathF.Round(face.AscentPx(scale) * 0.66f));
        var width = global::System.Math.Max(3, (int)MathF.Round(height * 0.6f));
        var coverage = new byte[width * height];
        for (var x = 0; x < width; x++)
        {
            coverage[x] = 255;                       // top edge
            coverage[(height - 1) * width + x] = 255; // bottom edge
        }

        for (var y = 0; y < height; y++)
        {
            coverage[y * width] = 255;                // left edge
            coverage[y * width + (width - 1)] = 255;  // right edge
        }

        Glyph glyph;
        if (_atlas.TryInsert(coverage, width, height, out var uv))
        {
            glyph = new Glyph
            {
                Uv = uv,
                PixelWidth = width,
                PixelHeight = height,
                BearingXPx = 0f,
                BearingYPx = height,
                AdvancePx = width + 2f,
                HasPixels = true,
                IsFallback = true,
            };
        }
        else
        {
            DroppedInsertions++;
            AtlasOverflowed = true;
            WarnCapacityOnce();
            glyph = new Glyph { AdvancePx = width + 2f, HasPixels = false, IsFallback = true };
        }

        _cache[key] = glyph;
        return glyph;
    }

    /// <summary>
    /// Forgets every glyph and empties the atlas so glyphs in use now can be rasterized again. Only for a library
    /// whose owner draws nothing with old glyphs afterwards (call it between frames, before emitting any text); the
    /// editor's shared library is never cleared.
    /// </summary>
    public void ClearAtlas()
    {
        _atlas.Clear();
        _cache.Clear();
        AtlasOverflowed = false;
        _capacityWarned = false;
        AtlasClears++;
    }

    private static byte[] LoadResource(string logicalName)
    {
        var assembly = typeof(FontLibrary).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded font resource not found: {logicalName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _atlas.Dispose();
        _regular.Dispose();
        _semiBold.Dispose();
        _monospace.Dispose();
        _cache.Clear();
    }
}
