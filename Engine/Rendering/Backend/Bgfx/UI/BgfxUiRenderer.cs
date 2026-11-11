using Wisko.Engine.Math;
using Wisko.Engine.Text;
using Wisko.Engine.UI;

namespace Wisko.Engine.Rendering.Backend.Bgfx.UI;

/// <summary>
/// bgfx IUiRenderer: same batching and text layout as the GL ScreenUiRenderer (rects, then Inter glyph
/// quads from TextLayout), submitted as transient geometry on flush. Text uses <paramref name="fonts"/> (the
/// backend's shared library, or another one such as the game GUI's).
/// </summary>
internal sealed class BgfxUiRenderer(BgfxUiBackend backend, FontLibrary fonts) : IUiRenderer
{
    private readonly List<BgfxUiVertex> _solid = new();
    private readonly List<BgfxUiVertex> _glyphs = new();
    private float _dpiScale = 1f;
    private UiClip? _clip;

    public void BeginBatch(float dpiScale)
    {
        _solid.Clear();
        _glyphs.Clear();
        _dpiScale = dpiScale > 0f ? dpiScale : 1f;
        _clip = null;
    }

    public void SetClip(UiClip? clip) => _clip = clip;

    public void DrawRect(float x, float y, float width, float height, Vector3 color, float alpha = 1f) =>
        AddClippedQuad(_solid, x, y, width, height, 0f, 0f, 0f, 0f, color, alpha);

    public void DrawBorderRect(float x, float y, float width, float height, Vector3 color, float thickness, float alpha = 1f)
    {
        DrawRect(x, y, width, thickness, color, alpha);
        DrawRect(x, y + height - thickness, width, thickness, color, alpha);
        DrawRect(x, y, thickness, height, color, alpha);
        DrawRect(x + width - thickness, y, thickness, height, color, alpha);
    }

    public void DrawString(float x, float y, string text, Vector3 color, float pixelScale = 1.4f, FontWeight weight = FontWeight.Regular)
    {
        foreach (var quad in TextLayout.BuildQuads(fonts, weight, text, pixelScale, _dpiScale, x, y))
        {
            // Atlas is top-down: the top screen edge maps to V0, the bottom to V1 (as in the GL path).
            AddClippedQuad(_glyphs, quad.X, quad.Y, quad.Width, quad.Height, quad.Uv.U0, quad.Uv.V0, quad.Uv.U1, quad.Uv.V1, color, 1f);
        }
    }

    public float MeasureWidth(string text, float pixelScale, FontWeight weight = FontWeight.Regular) =>
        TextLayout.Measure(fonts, weight, text, pixelScale, _dpiScale).Width;

    public float MeasureLineHeight(float pixelScale, FontWeight weight = FontWeight.Regular) =>
        TextLayout.Measure(fonts, weight, string.Empty, pixelScale, _dpiScale).Height;

    public void Flush(int screenWidth, int screenHeight)
    {
        backend.SubmitSolid(_solid);
        backend.SubmitText(_glyphs, fonts);
    }

    public void Dispose()
    {
        // CPU-side only; GPU resources belong to the backend.
    }

    private void AddClippedQuad(List<BgfxUiVertex> target, float x, float y, float width, float height, float u0, float v0, float u1, float v1, Vector3 color, float alpha)
    {
        if (_clip is { } clip && !clip.Apply(ref x, ref y, ref width, ref height, ref u0, ref v0, ref u1, ref v1))
        {
            return;
        }

        AddQuad(target, x, y, width, height, u0, v0, u1, v1, color, alpha);
    }

    internal static void AddQuad(
        List<BgfxUiVertex> target,
        float x,
        float y,
        float width,
        float height,
        float u0,
        float v0,
        float u1,
        float v1,
        Vector3 color,
        float alpha)
    {
        var topLeft = new BgfxUiVertex(x, y, u0, v0, color.X, color.Y, color.Z, alpha);
        var topRight = new BgfxUiVertex(x + width, y, u1, v0, color.X, color.Y, color.Z, alpha);
        var bottomRight = new BgfxUiVertex(x + width, y + height, u1, v1, color.X, color.Y, color.Z, alpha);
        var bottomLeft = new BgfxUiVertex(x, y + height, u0, v1, color.X, color.Y, color.Z, alpha);
        target.Add(topLeft);
        target.Add(topRight);
        target.Add(bottomRight);
        target.Add(topLeft);
        target.Add(bottomRight);
        target.Add(bottomLeft);
    }
}

/// <summary>bgfx IUiImageRenderer: one textured quad per call, submitted immediately.</summary>
internal sealed class BgfxUiImageRenderer(BgfxUiBackend backend) : IUiImageRenderer
{
    private static readonly Vector3 White = new(1f, 1f, 1f);
    private readonly List<BgfxUiVertex> _quad = new(6);

    public void Draw(UiTexture texture, float x, float y, float width, float height, int screenWidth, int screenHeight)
    {
        _quad.Clear();
        BgfxUiRenderer.AddQuad(_quad, x, y, width, height, 0f, 0f, 1f, 1f, White, 1f);
        backend.SubmitImage(_quad, texture);
    }

    public void DrawRegion(UiTexture texture, float x, float y, float width, float height, float u0, float v0, float u1, float v1, int screenWidth, int screenHeight)
    {
        _quad.Clear();
        BgfxUiRenderer.AddQuad(_quad, x, y, width, height, u0, v0, u1, v1, White, 1f);
        backend.SubmitImage(_quad, texture);
    }

    public void Dispose()
    {
    }
}
