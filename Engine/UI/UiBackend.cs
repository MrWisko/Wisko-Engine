using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>
/// A clip rectangle in logical px. UI geometry is axis-aligned quads, so clipping is exact on the CPU: a quad is cut
/// to the rectangle and its texture coordinates are cut by the same fractions. Identical on every backend (no
/// scissor state).
/// </summary>
internal readonly record struct UiClip(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    /// <summary>
    /// Cuts the quad (and its UVs) to this rectangle. False when nothing of it remains visible.
    /// </summary>
    public bool Apply(ref float x, ref float y, ref float width, ref float height, ref float u0, ref float v0, ref float u1, ref float v1)
    {
        var left = MathF.Max(x, X);
        var top = MathF.Max(y, Y);
        var right = MathF.Min(x + width, Right);
        var bottom = MathF.Min(y + height, Bottom);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        if (width > 0f && height > 0f)
        {
            var du = u1 - u0;
            var dv = v1 - v0;
            var newU0 = u0 + du * ((left - x) / width);
            var newU1 = u0 + du * ((right - x) / width);
            var newV0 = v0 + dv * ((top - y) / height);
            var newV1 = v0 + dv * ((bottom - y) / height);
            u0 = newU0;
            u1 = newU1;
            v0 = newV0;
            v1 = newV1;
        }

        x = left;
        y = top;
        width = right - left;
        height = bottom - top;
        return true;
    }

    /// <summary>The overlap of two clips (an empty clip when they do not overlap).</summary>
    public UiClip Intersect(UiClip other)
    {
        var left = MathF.Max(X, other.X);
        var top = MathF.Max(Y, other.Y);
        var right = MathF.Min(Right, other.Right);
        var bottom = MathF.Min(Bottom, other.Bottom);
        return new UiClip(left, top, MathF.Max(0f, right - left), MathF.Max(0f, bottom - top));
    }
}

/// <summary>Backend texture id for a UI image (a GL texture name or a bgfx texture handle index).</summary>
internal readonly record struct UiTexture(int Id);

/// <summary>
/// Batched 2D drawing in logical px (top-left origin) used by the Project Hub and name prompt.
/// Rects and text accumulate between <see cref="BeginBatch"/> and <see cref="Flush"/>; on flush rects
/// draw first, then text.
/// </summary>
internal interface IUiRenderer : IDisposable
{
    void BeginBatch(float dpiScale);

    /// <summary>
    /// Restricts what the following draws of this batch may cover to <paramref name="clip"/> (null: no restriction).
    /// A new batch starts unclipped.
    /// </summary>
    void SetClip(UiClip? clip);

    void DrawRect(float x, float y, float width, float height, Vector3 color, float alpha = 1f);

    void DrawBorderRect(float x, float y, float width, float height, Vector3 color, float thickness, float alpha = 1f);

    void DrawString(float x, float y, string text, Vector3 color, float pixelScale = 1.4f, FontWeight weight = FontWeight.Regular);

    float MeasureWidth(string text, float pixelScale, FontWeight weight = FontWeight.Regular);

    float MeasureLineHeight(float pixelScale, FontWeight weight = FontWeight.Regular);

    void Flush(int screenWidth, int screenHeight);
}

/// <summary>Draws one RGBA texture into a logical-px rect, immediately (after any flushed batch).</summary>
internal interface IUiImageRenderer : IDisposable
{
    void Draw(UiTexture texture, float x, float y, float width, float height, int screenWidth, int screenHeight);

    /// <summary>Draws only the part u0..u1 x v0..v1 (0 to 1, from the top-left) of the texture into the rect.</summary>
    void DrawRegion(UiTexture texture, float x, float y, float width, float height, float u0, float v0, float u1, float v1, int screenWidth, int screenHeight);
}

/// <summary>Image drawing helpers shared by the screen UI.</summary>
internal static class UiImages
{
    /// <summary>
    /// Draws the texture into the rect, but only the part inside <paramref name="clip"/> (the rest of the image is cut
    /// away, like clipped rects and text): for images in scrolled lists.
    /// </summary>
    public static void DrawClipped(IUiImageRenderer images, UiTexture texture, float x, float y, float width, float height, UiClip clip,
        int screenWidth, int screenHeight)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var left = MathF.Max(x, clip.X);
        var top = MathF.Max(y, clip.Y);
        var right = MathF.Min(x + width, clip.X + clip.Width);
        var bottom = MathF.Min(y + height, clip.Y + clip.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        if (left == x && top == y && right == x + width && bottom == y + height)
        {
            images.Draw(texture, x, y, width, height, screenWidth, screenHeight);
            return;
        }

        images.DrawRegion(texture, left, top, right - left, bottom - top,
            (left - x) / width, (top - y) / height, (right - x) / width, (bottom - y) / height, screenWidth, screenHeight);
    }
}

/// <summary>Image files on disk mapped to backend textures, reloaded only when a file changes.</summary>
internal interface IUiTextureCache : IDisposable
{
    void Refresh(IEnumerable<string> paths);

    bool TryGet(string path, out UiTexture texture);
}

/// <summary>Creates and destroys RGBA textures for <see cref="TextureFileCache"/>.</summary>
internal interface IUiTextureFactory
{
    UiTexture Create(byte[] rgba, int width, int height);

    void Destroy(UiTexture texture);
}

/// <summary>Creates the UI drawing objects for one rendering backend (OpenGL or bgfx).</summary>
internal interface IUiBackend
{
    /// <summary>A renderer drawing text with the backend's shared (editor) font library.</summary>
    IUiRenderer CreateRenderer();

    /// <summary>A renderer drawing text with <paramref name="fonts"/> (its own glyph atlas, e.g. the game GUI's).</summary>
    IUiRenderer CreateRenderer(FontLibrary fonts);

    /// <summary>
    /// A new font library with its own glyph atlas on this backend, initialized; the caller owns and disposes it (after
    /// the renderers using it). The backend context must be current.
    /// </summary>
    FontLibrary CreateFontLibrary();

    IUiImageRenderer CreateImageRenderer();

    IUiTextureCache CreateTextureCache();

    /// <summary>A texture from RGBA pixels already in memory (e.g. an embedded picture); destroy it with <see cref="DestroyTexture"/>.</summary>
    UiTexture CreateTexture(byte[] rgba, int width, int height);

    void DestroyTexture(UiTexture texture);
}

/// <summary>Text helpers shared by the screen UI.</summary>
internal static class UiText
{
    /// <summary>
    /// The text shortened with "..." to fit <paramref name="maxWidth"/> (binary search on the length, so long messages
    /// cost a handful of measurements, not one per character).
    /// </summary>
    public static string Fit(IUiRenderer ui, string text, float maxWidth, float scale, FontWeight weight = FontWeight.Regular)
    {
        if (maxWidth <= 0f)
        {
            return string.Empty;
        }

        if (ui.MeasureWidth(text, scale, weight) <= maxWidth)
        {
            return text;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ui.MeasureWidth(string.Concat(text.AsSpan(0, mid), "..."), scale, weight) <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Never end on half of a surrogate pair.
        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return string.Concat(text.AsSpan(0, low).TrimEnd(), "...");
    }
}
