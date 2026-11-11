using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

/// <summary>
/// What a <see cref="ConfirmDialog"/> asks: a title, a message and two or three buttons. Enter picks
/// <see cref="DefaultButton"/> (highlighted); the last button is the safe one Escape picks. A
/// <see cref="DestructiveButton"/> (e.g. Delete) is drawn red.
/// </summary>
internal sealed record ConfirmRequest(string Title, string Message, IReadOnlyList<string> Buttons, Action<int> OnChoice, int DefaultButton = 0,
    int DestructiveButton = -1);

/// <summary>
/// Wisko's own modal question (e.g. Save / Discard / Cancel): drawn over the editor with the shared UI
/// renderer. Enter picks the default button, Escape the last one (the safe "Cancel"); a click picks any.
/// </summary>
internal sealed class ConfirmDialog : IDisposable
{
    private const float BoxWidth = 460f;
    private const float Padding = 24f;
    private const float ButtonHeight = 34f;
    private const float ButtonGap = 10f;
    private const float TitleScale = 2.3f;
    private const float TextScale = 1.8f;
    private const float ButtonScale = 1.9f;

    private static readonly Vector3 Panel = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 PanelBorder = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 Title = new(0.95f, 0.96f, 0.98f);
    private static readonly Vector3 Muted = new(0.70f, 0.73f, 0.78f);
    private static readonly Vector3 ButtonFill = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 ButtonHover = new(0.30f, 0.32f, 0.37f);
    private static readonly Vector3 AccentFill = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 AccentHover = new(0.25f, 0.49f, 0.88f);
    private static readonly Vector3 DestructiveFill = new(0.66f, 0.22f, 0.22f);
    private static readonly Vector3 DestructiveHover = new(0.77f, 0.27f, 0.27f);
    private static readonly Vector3 ButtonText = new(0.92f, 0.94f, 0.97f);

    private IUiRenderer _ui = null!;
    private ConfirmRequest? _request;
    private List<string> _messageLines = [];
    private float _mouseX;
    private float _mouseY;

    public bool IsOpen => _request is not null;

    public void Initialize(IUiBackend ui) => _ui = ui.CreateRenderer();

    public void Open(ConfirmRequest request)
    {
        _request = request;
        _messageLines = [];
    }

    /// <summary>Escape: the last button (Cancel) is chosen.</summary>
    public void Cancel()
    {
        if (_request is { } request)
        {
            Choose(request.Buttons.Count - 1);
        }
    }

    public void Update(InputManager input, int screenWidth, int screenHeight)
    {
        if (_request is not { } request)
        {
            return;
        }

        _mouseX = input.MouseX;
        _mouseY = input.MouseY;
        foreach (var key in input.KeyEvents)
        {
            if (key.Key == EditorKey.Enter && !key.IsRepeat)
            {
                Choose(request.DefaultButton);
                return;
            }
        }

        if (!input.LeftMousePressed)
        {
            return;
        }

        var buttons = ButtonRects(screenWidth, screenHeight, request);
        for (var i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].Contains(_mouseX, _mouseY))
            {
                Choose(i);
                return;
            }
        }
    }

    public void Render(int screenWidth, int screenHeight, float dpiScale)
    {
        if (_request is not { } request)
        {
            return;
        }

        _ui.BeginBatch(dpiScale);
        if (_messageLines.Count == 0)
        {
            _messageLines = Wrap(request.Message, BoxWidth - Padding * 2f);
        }

        var box = BoxRect(screenWidth, screenHeight);
        _ui.DrawRect(0f, 0f, screenWidth, screenHeight, new Vector3(0f, 0f, 0f), 0.5f);
        _ui.DrawRect(box.X - 1f, box.Y - 1f, box.Width + 2f, box.Height + 2f, PanelBorder);
        _ui.DrawRect(box.X, box.Y, box.Width, box.Height, Panel);
        _ui.DrawString(box.X + Padding, box.Y + Padding, Fit(request.Title, box.Width - Padding * 2f), Title, TitleScale, FontWeight.SemiBold);
        var lineHeight = _ui.MeasureLineHeight(TextScale) + 4f;
        for (var i = 0; i < _messageLines.Count; i++)
        {
            _ui.DrawString(box.X + Padding, box.Y + Padding + 34f + i * lineHeight, _messageLines[i], Muted, TextScale);
        }

        var buttons = ButtonRects(screenWidth, screenHeight, request);
        for (var i = 0; i < buttons.Length; i++)
        {
            var rect = buttons[i];
            var hovered = rect.Contains(_mouseX, _mouseY);
            var fill = i == request.DestructiveButton ? (hovered ? DestructiveHover : DestructiveFill)
                : i == request.DefaultButton ? (hovered ? AccentHover : AccentFill)
                : (hovered ? ButtonHover : ButtonFill);
            _ui.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, fill);
            var label = request.Buttons[i];
            var width = _ui.MeasureWidth(label, ButtonScale);
            var height = _ui.MeasureLineHeight(ButtonScale);
            _ui.DrawString(rect.X + (rect.Width - width) * 0.5f, rect.Y + (rect.Height - height) * 0.5f, label, ButtonText, ButtonScale);
        }

        _ui.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _ui.Dispose();

    private void Choose(int index)
    {
        var request = _request;
        _request = null;
        request?.OnChoice(index);
    }

    private UiBox BoxRect(int screenWidth, int screenHeight)
    {
        var lines = global::System.Math.Max(1, _messageLines.Count == 0 ? 2 : _messageLines.Count);
        var height = Padding * 2f + 34f + lines * 22f + 18f + ButtonHeight;
        return new UiBox(MathF.Round((screenWidth - BoxWidth) * 0.5f), MathF.Round((screenHeight - height) * 0.5f), BoxWidth, height);
    }

    private UiBox[] ButtonRects(int screenWidth, int screenHeight, ConfirmRequest request)
    {
        var box = BoxRect(screenWidth, screenHeight);
        var rects = new UiBox[request.Buttons.Count];
        var right = box.X + box.Width - Padding;
        var y = box.Y + box.Height - Padding - ButtonHeight;

        // Primary button on the right, the others to its left in order (the last one, Cancel, leftmost).
        for (var i = 0; i < request.Buttons.Count; i++)
        {
            var width = MathF.Max(96f, _ui.MeasureWidth(request.Buttons[i], ButtonScale) + 28f);
            rects[i] = new UiBox(right - width, y, width, ButtonHeight);
            right -= width + ButtonGap;
        }

        return rects;
    }

    // A title too long for the box (e.g. one with a long name in it) ends with an ellipsis.
    private string Fit(string title, float maxWidth)
    {
        if (_ui.MeasureWidth(title, TitleScale, FontWeight.SemiBold) <= maxWidth)
        {
            return title;
        }

        var length = title.Length;
        while (length > 0 && _ui.MeasureWidth(title[..length] + "…", TitleScale, FontWeight.SemiBold) > maxWidth)
        {
            length--;
        }

        return title[..length] + "…";
    }

    private List<string> Wrap(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && _ui.MeasureWidth(candidate, TextScale) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }
}
