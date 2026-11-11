namespace Wisko.Engine.Text;

/// <summary>
/// UV rectangle of a glyph within the atlas, in normalized [0,1] texture space.
/// Top-down convention: <see cref="V0"/> (top) is smaller than <see cref="V1"/> (bottom),
/// because inserted coverage rows go top-first (row 0 = glyph top).
/// </summary>
public readonly struct AtlasUv
{
    public AtlasUv(float u0, float v0, float u1, float v1)
    {
        U0 = u0;
        V0 = v0;
        U1 = u1;
        V1 = v1;
    }

    public float U0 { get; }
    public float V0 { get; }
    public float U1 { get; }
    public float V1 { get; }
}

/// <summary>
/// Shared fixed-size single-channel (R8) glyph atlas with a deterministic shelf packer. Packing is CPU-side;
/// glyph pixels go to a backend texture through <see cref="IGlyphAtlasSurface"/> (OpenGL by default, which
/// requires an active GL context for every mutating operation).
///
/// Deferred by design: dynamic growth and eviction. When the current shelves are exhausted,
/// <see cref="TryInsert"/> returns false rather than growing — callers treat that as a
/// (currently unreachable at the pre-warm sizes) capacity limit.
/// </summary>
public sealed class GlyphAtlas : IDisposable
{
    public const int Size = 1024;
    private const int Padding = 1; // 1px gutter so bilinear sampling never bleeds between glyphs

    private readonly IGlyphAtlasSurface _surface;
    private int _shelfX;
    private int _shelfY;
    private int _shelfHeight;
    private bool _initialized;
    private bool _disposed;

    public GlyphAtlas()
        : this(new GlGlyphAtlasSurface())
    {
    }

    internal GlyphAtlas(IGlyphAtlasSurface surface)
    {
        _surface = surface;
    }

    public int TextureHandle => _surface.Handle;

    /// <summary>Rows consumed so far (top of the current shelf plus its height).</summary>
    public int UsedRows => _shelfY + _shelfHeight;

    public long UsedPixels { get; private set; }

    /// <summary>Allocates the R8 texture (clamp-to-edge, bilinear) on the atlas surface.</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _surface.Create(Size);
        _initialized = true;
    }

    /// <summary>
    /// Uploads a top-down coverage bitmap into free atlas space and returns its UV rect.
    /// Existing glyphs are never moved, so previously returned UVs remain valid.
    /// Returns false only when the atlas is full (growth deferred).
    /// </summary>
    public bool TryInsert(byte[] coverage, int width, int height, out AtlasUv uv)
    {
        uv = default;
        if (!_initialized || width <= 0 || height <= 0)
        {
            return false;
        }

        if (_shelfX + width + Padding > Size)
        {
            _shelfX = 0;
            _shelfY += _shelfHeight + Padding;
            _shelfHeight = 0;
        }

        if (_shelfY + height + Padding > Size)
        {
            return false; // atlas full — dynamic growth is intentionally deferred
        }

        var x = _shelfX;
        var y = _shelfY;

        _surface.Upload(x, y, width, height, coverage);

        _shelfX += width + Padding;
        if (height > _shelfHeight)
        {
            _shelfHeight = height;
        }

        UsedPixels += (long)width * height;
        uv = new AtlasUv(x / (float)Size, y / (float)Size, (x + width) / (float)Size, (y + height) / (float)Size);
        return true;
    }

    /// <summary>
    /// Empties the atlas: the texture is zeroed (so the padding gutters of new glyphs never sample old pixels) and
    /// packing starts again at the top. Every UV handed out before is invalid afterwards, so the owner may only do
    /// this between frames, before any quad using the atlas is emitted.
    /// </summary>
    public void Clear()
    {
        if (!_initialized)
        {
            return;
        }

        _surface.Upload(0, 0, Size, Size, new byte[Size * Size]);
        _shelfX = 0;
        _shelfY = 0;
        _shelfHeight = 0;
        UsedPixels = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.Dispose();
    }
}
