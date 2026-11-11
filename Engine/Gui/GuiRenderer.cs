using Wisko.Engine.Text;
using Wisko.Engine.UI;

namespace Wisko.Engine.Gui;

/// <summary>What one <see cref="GuiRenderer.Draw"/> emitted: batches flushed, rectangles and text runs.</summary>
internal readonly record struct GuiDrawStats(int Batches, int Rects, int Texts);

/// <summary>
/// Draws an Image element's picture (a project image, by its project-relative path) into its rectangle, cut to the clip,
/// immediately; false when it has no picture to draw (missing or unreadable file).
/// </summary>
internal interface IGuiImageSource
{
    bool TryDraw(string image, GuiRect rect, GuiRect clip, int screenWidth, int screenHeight);
}

/// <summary>
/// Draws laid-out GUI entries through an <see cref="IUiRenderer"/> (the same 2D path on OpenGL and bgfx), in the
/// layout's draw order and nothing else: geometry comes from <see cref="GuiLayout"/>, never recomputed here.
/// <para>
/// Order: an IUiRenderer batch draws all its rectangles before all its text. A rectangle that comes after text in draw
/// order and overlaps it therefore starts a new batch (otherwise it would end up under that text); one that overlaps no
/// text of the batch can join it (where nothing overlaps, the order is invisible). Text always joins the batch.
/// </para>
/// <para>
/// Clipping: everything is clipped to the canvas (the game view); text is also clipped to its own element. Children
/// are not clipped to their parent (no masking).
/// </para>
/// </summary>
internal sealed class GuiRenderer
{
    private static readonly GuiColor DisabledMix = new(128, 128, 128);

    // Where the current batch's text can draw (each run's clip); reused between frames.
    private readonly List<GuiRect> _batchText = [];

    /// <summary>Text size passed to the UI renderer for a font size in pixels (the shared text layout's unit is 8 px).</summary>
    public static float TextScale(int fontSize) => fontSize / TextLayout.LogicalPixelHeightPerScaleUnit;

    /// <summary>A hovered button's background: 12% of the way to white.</summary>
    public static GuiColor Hovered(GuiColor color) => new(
        (byte)MathF.Round(color.R + (255 - color.R) * 0.12f),
        (byte)MathF.Round(color.G + (255 - color.G) * 0.12f),
        (byte)MathF.Round(color.B + (255 - color.B) * 0.12f));

    /// <summary>A pressed button's background: 18% darker.</summary>
    public static GuiColor Pressed(GuiColor color) => new(
        (byte)MathF.Round(color.R * 0.82f),
        (byte)MathF.Round(color.G * 0.82f),
        (byte)MathF.Round(color.B * 0.82f));

    /// <summary>A disabled button's colours: halfway to mid grey (a fixed, muted look).</summary>
    public static GuiColor Disabled(GuiColor color) => new(
        (byte)((color.R + DisabledMix.R + 1) / 2),
        (byte)((color.G + DisabledMix.G + 1) / 2),
        (byte)((color.B + DisabledMix.B + 1) / 2));

    /// <summary>
    /// Draws the entries; <paramref name="runtime"/> (when given) supplies buttons' hovered / pressed looks.
    /// <paramref name="zoom"/> scales text with an editor preview drawn smaller than its layout size (entries already
    /// scaled); 1 for the game.
    /// </summary>
    public GuiDrawStats Draw(IUiRenderer ui, IReadOnlyList<GuiLayoutEntry> entries, GuiRect canvas, float dpiScale, int screenWidth, int screenHeight, GuiRuntime? runtime = null, float zoom = 1f,
        IGuiImageSource? images = null)
    {
        if (entries.Count == 0 || canvas.Width <= 0f || canvas.Height <= 0f)
        {
            return default;
        }

        _batchText.Clear();
        var batch = new Batch(ui, new UiClip(canvas.X, canvas.Y, canvas.Width, canvas.Height), _batchText, dpiScale, screenWidth, screenHeight) { Zoom = zoom };
        ui.BeginBatch(dpiScale);
        foreach (var entry in entries)
        {
            // Hidden (itself or an ancestor) or no area: nothing of this element (its children are entries of their own).
            if (!entry.Visible || entry.Rect.Width <= 0f || entry.Rect.Height <= 0f)
            {
                continue;
            }

            switch (entry.Element)
            {
                case GuiFrame frame:
                    DrawFrame(ref batch, frame, entry.Rect);
                    break;
                case GuiText text:
                    DrawLabel(ref batch, entry.Rect, text.Text, text.TextColor, text.FontSize, text.HorizontalAlignment, text.VerticalAlignment);
                    break;
                case GuiButton button:
                    DrawButton(ref batch, button, entry.Rect, runtime?.VisualOf(button) ?? GuiButtonVisual.Normal);
                    break;
                case GuiImage image when images is not null && image.Image.Length > 0:
                    // Pictures draw at once: what came before is flushed first, what comes after starts a new batch,
                    // so the draw order (ZOrder) holds across pictures, rectangles and text.
                    batch.Interrupt();
                    images.TryDraw(image.Image, entry.Rect, canvas, screenWidth, screenHeight);
                    break;
                case GuiTextBox box:
                    DrawTextBox(ref batch, box, entry.Rect, runtime);
                    break;
            }
        }

        ui.SetClip(null);
        ui.Flush(screenWidth, screenHeight);
        return new GuiDrawStats(batch.Batches, batch.Rects, batch.Texts);
    }

    private static void DrawFrame(ref Batch batch, GuiFrame frame, GuiRect rect)
    {
        if (frame.Opacity > 0f)
        {
            batch.Rect(rect, frame.BackgroundColor, frame.Opacity);
        }
    }

    // A button: its background (disabled, else pressed, else hovered, else as authored) and its centred label.
    private static void DrawButton(ref Batch batch, GuiButton button, GuiRect rect, GuiButtonVisual visual)
    {
        var background = !button.Enabled ? Disabled(button.BackgroundColor)
            : visual == GuiButtonVisual.Pressed ? Pressed(button.BackgroundColor)
            : visual == GuiButtonVisual.Hovered ? Hovered(button.BackgroundColor)
            : button.BackgroundColor;
        var textColor = button.Enabled ? button.TextColor : Disabled(button.TextColor);
        batch.Rect(rect, background, 1f);
        DrawLabel(ref batch, rect, button.Text, textColor, button.FontSize, GuiHorizontalAlignment.Center, GuiVerticalAlignment.Middle);
    }

    /// <summary>The focus ring of a TextBox that has the keyboard.</summary>
    public static readonly GuiColor TextBoxFocusColor = new(52, 120, 246);

    /// <summary>Space between a TextBox's edge and its text, in pixels.</summary>
    public const float TextBoxPadding = 8f;

    // A TextBox: its background, a focus ring while it has the keyboard, its text (or the dimmed placeholder while it is
    // empty) on one line, scrolled so the caret stays inside, and the blinking caret.
    private static void DrawTextBox(ref Batch batch, GuiTextBox box, GuiRect rect, GuiRuntime? runtime)
    {
        var focused = runtime?.FocusedTextBoxId == box.Id;
        batch.Rect(rect, box.BackgroundColor, 1f);
        if (focused)
        {
            var ring = 2f * batch.Zoom;
            batch.Rect(new GuiRect(rect.X, rect.Y, rect.Width, ring), TextBoxFocusColor, 1f);
            batch.Rect(new GuiRect(rect.X, rect.Bottom - ring, rect.Width, ring), TextBoxFocusColor, 1f);
            batch.Rect(new GuiRect(rect.X, rect.Y, ring, rect.Height), TextBoxFocusColor, 1f);
            batch.Rect(new GuiRect(rect.Right - ring, rect.Y, ring, rect.Height), TextBoxFocusColor, 1f);
        }

        var padding = TextBoxPadding * batch.Zoom;
        var inner = new GuiRect(rect.X + padding, rect.Y, MathF.Max(0f, rect.Width - padding * 2f), rect.Height);
        var scale = TextScale(box.FontSize) * batch.Zoom;
        var height = batch.Ui.MeasureLineHeight(scale);
        var y = Snap(rect.Y + (rect.Height - height) * 0.5f, batch.DpiScale);
        if (box.Text.Length == 0)
        {
            if (box.PlaceholderText.Length > 0)
            {
                batch.Text(inner, Snap(inner.X, batch.DpiScale), y, box.PlaceholderText, Mix(box.TextColor, box.BackgroundColor, 0.55f), scale);
            }
        }
        else
        {
            // Scrolled left just enough that the caret (the end, without focus) is inside the box.
            var caret = focused ? global::System.Math.Clamp(runtime!.CaretIndex, 0, box.Text.Length) : box.Text.Length;
            var caretOffset = batch.Ui.MeasureWidth(box.Text[..caret], scale);
            var scroll = MathF.Max(0f, caretOffset - inner.Width + 2f * batch.Zoom);
            batch.Text(inner, Snap(inner.X - scroll, batch.DpiScale), y, box.Text, box.TextColor, scale);
        }

        if (focused && runtime!.CaretVisible)
        {
            var caret = global::System.Math.Clamp(runtime.CaretIndex, 0, box.Text.Length);
            var caretOffset = batch.Ui.MeasureWidth(box.Text[..caret], scale);
            var scroll = MathF.Max(0f, caretOffset - inner.Width + 2f * batch.Zoom);
            var x = Snap(inner.X + caretOffset - scroll, batch.DpiScale);
            batch.Rect(new GuiRect(x, y, MathF.Max(1f, 1.5f * batch.Zoom), height), box.TextColor, 1f);
        }
    }

    // a + (b - a) * t, per channel.
    private static GuiColor Mix(GuiColor a, GuiColor b, float t) => new(
        (byte)MathF.Round(a.R + (b.R - a.R) * t),
        (byte)MathF.Round(a.G + (b.G - a.G) * t),
        (byte)MathF.Round(a.B + (b.B - a.B) * t));

    // One line of text placed in its box by the alignments (Text and Button labels alike), clipped to the box.
    private static void DrawLabel(ref Batch batch, GuiRect box, string text, GuiColor color, int fontSize, GuiHorizontalAlignment horizontal, GuiVerticalAlignment vertical)
    {
        if (text.Length == 0)
        {
            return;
        }

        var scale = TextScale(fontSize) * batch.Zoom;
        var width = batch.Ui.MeasureWidth(text, scale);
        var height = batch.Ui.MeasureLineHeight(scale);
        var x = horizontal switch
        {
            GuiHorizontalAlignment.Left => box.X,
            GuiHorizontalAlignment.Right => box.Right - width,
            _ => box.X + (box.Width - width) * 0.5f
        };
        var y = vertical switch
        {
            GuiVerticalAlignment.Top => box.Y,
            GuiVerticalAlignment.Bottom => box.Bottom - height,
            _ => box.Y + (box.Height - height) * 0.5f
        };
        batch.Text(box, Snap(x, batch.DpiScale), Snap(y, batch.DpiScale), text, color, scale);
    }

    // Text starts on a whole device pixel so glyphs stay crisp (the same rounding on every backend).
    private static float Snap(float value, float dpiScale) => MathF.Round(value * dpiScale) / dpiScale;

    // The current IUiRenderer batch: knows where its text is, so a rectangle over that text starts a new batch.
    private struct Batch(IUiRenderer ui, UiClip canvas, List<GuiRect> text, float dpiScale, int screenWidth, int screenHeight)
    {
        public readonly IUiRenderer Ui => ui;

        public readonly float DpiScale => dpiScale;

        public float Zoom { get; init; } = 1f;

        public int Batches { get; private set; } = 1;

        public int Rects { get; private set; }

        public int Texts { get; private set; }

        // Flushes what is batched so something can draw on top of it now, and starts a fresh batch for what follows.
        public void Interrupt()
        {
            ui.Flush(screenWidth, screenHeight);
            ui.BeginBatch(dpiScale);
            text.Clear();
            Batches++;
        }

        public void Rect(GuiRect rect, GuiColor color, float alpha)
        {
            if (OverlapsText(rect))
            {
                ui.Flush(screenWidth, screenHeight);
                ui.BeginBatch(dpiScale);
                text.Clear();
                Batches++;
            }

            ui.SetClip(canvas);
            ui.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color.ToVector3(), alpha);
            Rects++;
        }

        public void Text(GuiRect box, float x, float y, string label, GuiColor color, float scale)
        {
            var clip = canvas.Intersect(new UiClip(box.X, box.Y, box.Width, box.Height));
            ui.SetClip(clip);
            ui.DrawString(x, y, label, color.ToVector3(), scale);
            text.Add(new GuiRect(clip.X, clip.Y, clip.Width, clip.Height));
            Texts++;
        }

        // Glyphs never leave their clip, so a rectangle clear of every clip of the batch's text cannot cover any of it.
        private readonly bool OverlapsText(GuiRect rect)
        {
            foreach (var area in text)
            {
                if (rect.X < area.Right && area.X < rect.Right && rect.Y < area.Bottom && area.Y < rect.Bottom)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
