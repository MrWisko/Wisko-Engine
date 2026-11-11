using System.Globalization;
using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>What a click on the Output panel hit.</summary>
internal enum OutputPanelControl
{
    None,
    Header,
    ToggleCollapse,
    Clear,
    ResizeEdge,
    Row
}

/// <summary>
/// The Output panel under the central content (3D View and script tabs alike): a header (title, error count, Clear,
/// collapse) and, when expanded, the session's <see cref="OutputLog"/> rows, newest at the bottom. Rows from scripts
/// show "[Spin.lua]" or "Spin.lua:8" and can be clicked to open the script at that line. Only visible rows are drawn;
/// the view follows new output while it is scrolled to the bottom.
/// </summary>
internal sealed class OutputPanel : IDisposable
{
    public const float HeaderHeight = 26f;
    public const float MinBodyHeight = 60f;
    private const float RowHeight = 20f;
    private const float TextScale = 1.6f;
    private const float ResizeGrip = 5f;

    private static readonly Vector3 Fill = new(0.075f, 0.08f, 0.095f);
    private static readonly Vector3 HeaderFill = new(0.11f, 0.12f, 0.14f);
    private static readonly Vector3 Border = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 Title = new(0.88f, 0.90f, 0.94f);
    private static readonly Vector3 Text = new(0.80f, 0.82f, 0.86f);
    private static readonly Vector3 Muted = new(0.52f, 0.55f, 0.60f);
    private static readonly Vector3 Warning = new(0.95f, 0.78f, 0.40f);
    private static readonly Vector3 Error = new(0.96f, 0.45f, 0.45f);
    private static readonly Vector3 Link = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 Hover = new(0.14f, 0.15f, 0.18f);
    private static readonly Vector3 ButtonFill = new(0.20f, 0.22f, 0.26f);

    private IUiRenderer _ui = null!;

    // Rows scrolled up from the bottom (0 = following the newest output).
    private float _scrollFromBottom;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public bool Collapsed { get; set; } = true;

    /// <summary>Body height when expanded (the header comes on top of it).</summary>
    public float BodyHeight { get; set; } = 150f;

    public float TotalHeight => HeaderHeight + (Collapsed ? 0f : BodyHeight);

    public void ScrollRows(float rows, OutputLog log, UiBox panel)
    {
        var visible = global::System.Math.Max(1f, (panel.Height - HeaderHeight) / RowHeight);
        _scrollFromBottom = global::System.Math.Clamp(_scrollFromBottom + rows, 0f, MathF.Max(0f, log.Count - visible));
    }

    public void ScrollToBottom() => _scrollFromBottom = 0f;

    public OutputPanelControl HitTest(UiBox panel, OutputLog log, float x, float y, out int row)
    {
        row = -1;
        if (!panel.Contains(x, y))
        {
            return OutputPanelControl.None;
        }

        if (!Collapsed && y <= panel.Y + ResizeGrip)
        {
            return OutputPanelControl.ResizeEdge;
        }

        if (y <= panel.Y + HeaderHeight)
        {
            var (clear, toggle) = HeaderButtons(panel);
            return clear.Contains(x, y) ? OutputPanelControl.Clear
                : toggle.Contains(x, y) ? OutputPanelControl.ToggleCollapse
                : OutputPanelControl.Header;
        }

        var index = RowAt(panel, log, y);
        if (index >= 0)
        {
            row = index;
            return OutputPanelControl.Row;
        }

        return OutputPanelControl.None;
    }

    public void Render(UiBox panel, OutputLog log, float mouseX, float mouseY, int screenWidth, int screenHeight, float dpiScale)
    {
        _ui.BeginBatch(dpiScale);
        _ui.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, Fill);
        _ui.DrawRect(panel.X, panel.Y, panel.Width, 1f, Border);
        _ui.DrawRect(panel.X, panel.Y + 1f, panel.Width, HeaderHeight - 1f, HeaderFill);
        var textY = panel.Y + (HeaderHeight - _ui.MeasureLineHeight(TextScale)) * 0.5f;
        _ui.DrawString(panel.X + 12f, textY, "Output", Title, TextScale, FontWeight.SemiBold);
        var titleRight = panel.X + 12f + _ui.MeasureWidth("Output", TextScale, FontWeight.SemiBold) + 10f;
        var (clear, toggle) = HeaderButtons(panel);
        if (log.ErrorCount > 0)
        {
            var badge = log.ErrorCount == 1 ? "1 error" : $"{log.ErrorCount} errors";
            _ui.DrawString(titleRight, textY, badge, Error, TextScale);
        }
        else if (log.Count > 0 && Collapsed)
        {
            var last = log[log.Count - 1];
            // Never under the header buttons (a narrow workspace leaves little room between the title and them).
            var room = MathF.Min(panel.Width * 0.5f, clear.X - 8f - titleRight);
            if (room > 0f)
            {
                _ui.DrawString(titleRight, textY, UiText.Fit(_ui, Describe(last), room, TextScale), Muted, TextScale);
            }
        }

        DrawButton(clear, "Clear", mouseX, mouseY);
        DrawButton(toggle, Collapsed ? "Expand" : "Collapse", mouseX, mouseY);

        if (!Collapsed)
        {
            var body = new UiBox(panel.X, panel.Y + HeaderHeight, panel.Width, panel.Height - HeaderHeight);
            _ui.SetClip(new UiClip(body.X, body.Y, body.Width, body.Height));
            if (log.Count == 0)
            {
                _ui.DrawString(body.X + 12f, body.Y + 8f, "Script output, warnings and errors appear here.", Muted, TextScale);
            }
            else
            {
                var visible = (int)MathF.Ceiling(body.Height / RowHeight) + 1;
                var lastIndex = log.Count - 1 - (int)_scrollFromBottom;
                var firstIndex = global::System.Math.Max(0, lastIndex - visible + 1);
                var rowTextY = (RowHeight - _ui.MeasureLineHeight(TextScale)) * 0.5f;
                for (var index = firstIndex; index <= lastIndex; index++)
                {
                    var y = body.Y + body.Height - (lastIndex - index + 1) * RowHeight;
                    var entry = log[index];
                    var hovered = mouseY >= y && mouseY < y + RowHeight && mouseX >= body.X && mouseX < body.X + body.Width && mouseY >= body.Y;
                    if (hovered && entry.HasSource)
                    {
                        _ui.DrawRect(body.X, y, body.Width, RowHeight, Hover);
                    }

                    DrawRow(body, y + rowTextY, entry, hovered);
                }
            }

            _ui.SetClip(null);
        }

        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private void DrawRow(UiBox body, float y, OutputEntry entry, bool hovered)
    {
        var x = body.X + 12f;
        if (entry.Kind == OutputKind.Separator)
        {
            var label = entry.Message;
            var width = _ui.MeasureWidth(label, TextScale);
            _ui.DrawRect(x, y + 8f, 24f, 1f, Border);
            _ui.DrawString(x + 30f, y, label, Muted, TextScale);
            _ui.DrawRect(x + 36f + width, y + 8f, MathF.Max(0f, body.Width - width - 60f), 1f, Border);
            return;
        }

        var time = entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        _ui.DrawString(x, y, time, Muted, TextScale);
        x += _ui.MeasureWidth("00:00:00", TextScale) + 10f;
        var color = entry.Kind switch
        {
            OutputKind.Error => Error,
            OutputKind.Warning => Warning,
            _ => Text
        };

        if (entry.ScriptPath is { } path)
        {
            var file = Path.GetFileName(path);
            var source = entry.Line is int line ? $"{file}:{line}" : $"[{file}]";
            _ui.DrawString(x, y, source, hovered ? Link : entry.Kind == OutputKind.Info ? Muted : color, TextScale, hovered ? FontWeight.SemiBold : FontWeight.Regular);
            if (hovered)
            {
                _ui.DrawRect(x, y + _ui.MeasureLineHeight(TextScale) - 2f, _ui.MeasureWidth(source, TextScale, FontWeight.SemiBold), 1f, Link);
            }

            x += _ui.MeasureWidth(source, TextScale, FontWeight.SemiBold) + 10f;
        }

        _ui.DrawString(x, y, UiText.Fit(_ui, entry.Message.Replace('\n', ' '), body.X + body.Width - x - 12f, TextScale), color, TextScale);
    }

    private static string Describe(OutputEntry entry) =>
        entry.ScriptPath is { } path ? $"{Path.GetFileName(path)}: {entry.Message}" : entry.Message;

    private int RowAt(UiBox panel, OutputLog log, float y)
    {
        if (Collapsed || log.Count == 0)
        {
            return -1;
        }

        var bodyBottom = panel.Y + panel.Height;
        var fromBottom = (int)((bodyBottom - y) / RowHeight);
        var index = log.Count - 1 - (int)_scrollFromBottom - fromBottom;
        return index >= 0 && index < log.Count ? index : -1;
    }

    private (UiBox Clear, UiBox Toggle) HeaderButtons(UiBox panel)
    {
        var y = panel.Y + 4f;
        var h = HeaderHeight - 8f;
        var toggle = new UiBox(panel.X + panel.Width - 8f - 64f, y, 64f, h);
        var clear = new UiBox(toggle.X - 6f - 52f, y, 52f, h);
        return (clear, toggle);
    }

    private void DrawButton(UiBox rect, string label, float mouseX, float mouseY)
    {
        _ui.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Contains(mouseX, mouseY) ? new Vector3(0.28f, 0.30f, 0.35f) : ButtonFill);
        var width = _ui.MeasureWidth(label, 1.5f);
        _ui.DrawString(rect.X + (rect.Width - width) * 0.5f, rect.Y + (rect.Height - _ui.MeasureLineHeight(1.5f)) * 0.5f, label, Text, 1.5f);
    }

}
