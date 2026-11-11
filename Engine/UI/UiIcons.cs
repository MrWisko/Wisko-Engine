using Wisko.Engine.Math;
using Wisko.Engine.Projects;

namespace Wisko.Engine.UI;

/// <summary>
/// Small monochrome icons built from rects (the screen UI batch only draws rects and text).
/// Shared by the Project Hub and the editor toolbar.
/// </summary>
internal static class UiIcons
{
    // Ring with a center dot. The ring hole is filled with the background color behind the icon.
    public static void DrawHome(IUiRenderer ui, float cx, float cy, Vector3 color, Vector3 background)
    {
        FillCircle(ui, cx, cy, 10f, color);
        FillCircle(ui, cx, cy, 8f, background);
        FillCircle(ui, cx, cy, 3f, color);
    }

    // Sheet of paper with three text lines.
    public static void DrawHistory(IUiRenderer ui, float cx, float cy, Vector3 color)
    {
        const float width = 16f;
        const float height = 20f;
        var x = MathF.Round(cx - width * 0.5f);
        var y = MathF.Round(cy - height * 0.5f);
        ui.DrawBorderRect(x, y, width, height, color, 2f);
        ui.DrawRect(x + 4f, y + 5f, 8f, 1.5f, color);
        ui.DrawRect(x + 4f, y + 9.25f, 8f, 1.5f, color);
        ui.DrawRect(x + 4f, y + 13.5f, 5f, 1.5f, color);
    }

    // A folder (the Projects library): a tab on the back left, then the folder body.
    public static void DrawProjects(IUiRenderer ui, float cx, float cy, Vector3 color)
    {
        const float width = 20f;
        const float height = 15f;
        var x = MathF.Round(cx - width * 0.5f);
        var y = MathF.Round(cy - height * 0.5f) + 1f;
        ui.DrawRect(x, y - 3f, 8f, 3f, color);
        ui.DrawBorderRect(x, y, width, height, color, 2f);
        ui.DrawRect(x, y + 3f, width, 2f, color);
    }

    // A small trash can: a lid with a handle, then the can with two ribs.
    public static void DrawTrash(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        var width = MathF.Round(size * 0.72f);
        var height = MathF.Round(size * 0.68f);
        var x = MathF.Round(cx - width * 0.5f);
        var top = MathF.Round(cy - size * 0.5f);
        ui.DrawRect(x + width * 0.32f, top, width * 0.36f, 1.5f, color);
        ui.DrawRect(x - 1.5f, top + 2f, width + 3f, 1.5f, color);
        var canTop = top + size * 0.32f;
        ui.DrawBorderRect(x, canTop, width, height, color, 1.5f);
        ui.DrawRect(x + width * 0.33f - 0.5f, canTop + 3f, 1.2f, height - 6f, color);
        ui.DrawRect(x + width * 0.67f - 0.5f, canTop + 3f, 1.2f, height - 6f, color);
    }

    // Left-pointing arrow: a shaft plus two diagonal strokes meeting at the tip.
    public static void DrawBackArrow(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float thickness = 2f;
        const float step = 0.5f;
        var tipX = cx - size * 0.5f;
        var head = size * 0.42f;
        ui.DrawRect(tipX + 1f, cy - thickness * 0.5f, size - 1f, thickness, color);
        for (var i = 0f; i <= head; i += step)
        {
            ui.DrawRect(tipX + i, cy - i - thickness * 0.5f, thickness, thickness, color);
            ui.DrawRect(tipX + i, cy + i - thickness * 0.5f, thickness, thickness, color);
        }
    }

    // Classic: an empty horizon with a sun. Testing: three cubes standing on the ground line.
    public static void DrawTemplate(IUiRenderer ui, WorldTemplate template, float x, float y, float width, float height, Vector3 color)
    {
        var line = MathF.Max(1.5f, height * 0.09f);
        var groundY = y + height - line;
        ui.DrawRect(x, groundY, width, line, color);

        if (template == WorldTemplate.Testing)
        {
            var cube = height * 0.46f;
            var gap = (width - cube * 3f) * 0.25f;
            for (var i = 0; i < 3; i++)
            {
                ui.DrawRect(x + gap + i * (cube + gap), groundY - cube - line * 0.5f, cube, cube, color);
            }

            return;
        }

        var radius = height * 0.2f;
        FillCircle(ui, x + width * 0.68f, y + height * 0.35f, radius, color);
    }

    // Small "x" made of two diagonal strokes.
    public static void DrawCross(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float thickness = 1.5f;
        const float step = 0.5f;
        var half = size * 0.5f;
        for (var i = -half; i <= half; i += step)
        {
            ui.DrawRect(cx + i - thickness * 0.5f, cy + i - thickness * 0.5f, thickness, thickness, color);
            ui.DrawRect(cx + i - thickness * 0.5f, cy - i - thickness * 0.5f, thickness, thickness, color);
        }
    }

    // Check mark (a ticked checkbox): a short stroke down to the bottom point, then a long one up to the right.
    public static void DrawCheck(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float thickness = 2f;
        const float step = 0.5f;
        var bottom = (X: cx - size * 0.12f, Y: cy + size * 0.28f);
        void Stroke(float x0, float y0, float x1, float y1)
        {
            var count = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)) / step);
            for (var i = 0; i <= count; i++)
            {
                var t = count == 0 ? 0f : i / (float)count;
                ui.DrawRect(x0 + (x1 - x0) * t - thickness * 0.5f, y0 + (y1 - y0) * t - thickness * 0.5f, thickness, thickness, color);
            }
        }

        Stroke(cx - size * 0.42f, cy - size * 0.02f, bottom.X, bottom.Y);
        Stroke(bottom.X, bottom.Y, cx + size * 0.45f, cy - size * 0.38f);
    }

    // Right-pointing solid triangle (Play), size = height; built from thin horizontal strips.
    public static void DrawPlay(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float strip = 0.5f;
        var half = size * 0.5f;
        var width = size * 0.9f;
        var left = cx - width * 0.45f;
        for (var y = -half; y < half; y += strip)
        {
            var mid = y + strip * 0.5f;
            var length = width * (1f - MathF.Abs(mid) / half);
            ui.DrawRect(left, cy + y, MathF.Max(length, 0f), strip, color);
        }
    }

    // Solid square (Stop).
    public static void DrawStop(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        var half = MathF.Round(size * 0.5f);
        ui.DrawRect(MathF.Round(cx) - half, MathF.Round(cy) - half, half * 2f, half * 2f, color);
    }

    // Four small squares in a 2x2 grid (ToolBench: groups of tools).
    public static void DrawGrid(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        var gap = MathF.Max(2f, MathF.Round(size * 0.18f));
        var cell = MathF.Round((size - gap) * 0.5f);
        var x = MathF.Round(cx - (cell * 2f + gap) * 0.5f);
        var y = MathF.Round(cy - (cell * 2f + gap) * 0.5f);
        ui.DrawRect(x, y, cell, cell, color);
        ui.DrawRect(x + cell + gap, y, cell, cell, color);
        ui.DrawRect(x, y + cell + gap, cell, cell, color);
        ui.DrawRect(x + cell + gap, y + cell + gap, cell, cell, color);
    }

    // Small downward-pointing solid triangle (a menu opens below); width = size, built from thin horizontal strips.
    public static void DrawCaretDown(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float strip = 0.5f;
        var height = size * 0.5f;
        var top = cy - height * 0.5f;
        for (var y = 0f; y < height; y += strip)
        {
            var width = size * (1f - (y + strip * 0.5f) / height);
            ui.DrawRect(cx - width * 0.5f, top + y, MathF.Max(width, 0f), strip, color);
        }
    }

    // Small right-pointing solid triangle (a closed disclosure arrow); height = size, built from thin vertical strips.
    public static void DrawCaretRight(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float strip = 0.5f;
        var width = size * 0.5f;
        var left = cx - width * 0.5f;
        for (var x = 0f; x < width; x += strip)
        {
            var height = size * (1f - (x + strip * 0.5f) / width);
            ui.DrawRect(left + x, cy - height * 0.5f, strip, MathF.Max(height, 0f), color);
        }
    }

    // Small left-pointing solid triangle (step back through choices); the mirror of DrawCaretRight.
    public static void DrawCaretLeft(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        const float strip = 0.5f;
        var width = size * 0.5f;
        var right = cx + width * 0.5f;
        for (var x = 0f; x < width; x += strip)
        {
            var height = size * (1f - (x + strip * 0.5f) / width);
            ui.DrawRect(right - x - strip, cy - height * 0.5f, strip, MathF.Max(height, 0f), color);
        }
    }

    // A script: a small page outline with three lines of "code" (the last one shorter).
    public static void DrawScript(IUiRenderer ui, float cx, float cy, float size, Vector3 color)
    {
        var width = size * 0.78f;
        var x = cx - width * 0.5f;
        var y = cy - size * 0.5f;
        ui.DrawBorderRect(x, y, width, size, color, 1f);
        var lineX = x + width * 0.22f;
        var lineWidth = width * 0.56f;
        ui.DrawRect(lineX, y + size * 0.28f, lineWidth, 1f, color);
        ui.DrawRect(lineX, y + size * 0.48f, lineWidth, 1f, color);
        ui.DrawRect(lineX, y + size * 0.68f, lineWidth * 0.6f, 1f, color);
    }

    public static void FillCircle(IUiRenderer ui, float cx, float cy, float radius, Vector3 color)
    {
        const float strip = 0.5f;
        for (var y = -radius; y < radius; y += strip)
        {
            var mid = y + strip * 0.5f;
            var halfWidth = MathF.Sqrt(MathF.Max(radius * radius - mid * mid, 0f));
            ui.DrawRect(cx - halfWidth, cy + y, halfWidth * 2f, strip, color);
        }
    }
}
