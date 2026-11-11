using Wisko.Engine.Input;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

internal enum TextPromptResult
{
    None,
    Confirmed,
    Cancelled
}

/// <summary>What a <see cref="TextPrompt"/> asks for: its wording and how long the answer may be.</summary>
internal sealed record TextPromptOptions(string Title, string Subtitle, string ConfirmLabel, int MaxLength, string Placeholder = "")
{
    /// <summary>The project name prompt (naming an unnamed world before it is kept, or before assets are added).</summary>
    public static TextPromptOptions ProjectName(string subtitle = "A name is required to keep this project.") =>
        new("Name your project", subtitle, "Create", ProjectStore.MaxNameLength);
}

/// <summary>
/// Modal one-line text prompt drawn over the editor (project name, new script name). It has no discard action:
/// Cancel/Escape only closes the prompt. Validation lives with the caller; this only collects text.
/// </summary>
internal sealed class TextPrompt : IDisposable
{
    private const float BoxWidth = 440f;
    private const float BoxHeight = 214f;
    private const float Padding = 24f;
    private const float FieldHeight = 40f;
    private const float ButtonWidth = 104f;
    private const float ButtonHeight = 36f;
    private const float ButtonGap = 10f;
    private const float TitleScale = 2.3f;
    private const float TextScale = 2.0f;
    private const float SmallTextScale = 1.7f;

    private static readonly Vector3 Overlay = new(0f, 0f, 0f);
    private static readonly Vector3 Panel = new(0.12f, 0.13f, 0.15f);
    private static readonly Vector3 PanelBorder = new(0.22f, 0.24f, 0.28f);
    private static readonly Vector3 Field = new(0.08f, 0.085f, 0.10f);
    private static readonly Vector3 FieldBorder = new(0.55f, 0.75f, 1.0f);
    private static readonly Vector3 Title = new(0.95f, 0.96f, 0.98f);
    private static readonly Vector3 Muted = new(0.58f, 0.61f, 0.66f);
    private static readonly Vector3 Placeholder = new(0.40f, 0.43f, 0.48f);
    private static readonly Vector3 Error = new(0.95f, 0.45f, 0.42f);
    private static readonly Vector3 ButtonFill = new(0.24f, 0.26f, 0.30f);
    private static readonly Vector3 ButtonHover = new(0.30f, 0.32f, 0.37f);
    private static readonly Vector3 AccentFill = new(0.20f, 0.42f, 0.78f);
    private static readonly Vector3 AccentHover = new(0.25f, 0.49f, 0.88f);
    private static readonly Vector3 ButtonText = new(0.92f, 0.94f, 0.97f);

    private IUiRenderer _screenUi = null!;
    private TextPromptOptions _options = TextPromptOptions.ProjectName();
    private string _text = string.Empty;
    private string? _error;
    private float _mouseX;
    private float _mouseY;
    private float _caretTime;

    public bool IsOpen { get; private set; }

    public string Text => _text;

    public void Initialize(IUiBackend ui) => _screenUi = ui.CreateRenderer();

    public void Open(TextPromptOptions options)
    {
        _options = options;
        IsOpen = true;
        _text = string.Empty;
        _error = null;
        _caretTime = 0f;
    }

    public void Close()
    {
        IsOpen = false;
        _error = null;
    }

    public void SetError(string? error) => _error = error;

    public TextPromptResult Update(InputManager input, int screenWidth, int screenHeight, float deltaTime)
    {
        if (!IsOpen)
        {
            return TextPromptResult.None;
        }

        _mouseX = input.MouseX;
        _mouseY = input.MouseY;
        _caretTime += deltaTime;

        if (input.TextBackspacePressed && _text.Length > 0)
        {
            _text = _text[..^1];
            _error = null;
            _caretTime = 0f;
        }

        foreach (var ch in input.TextInputCharacters)
        {
            if (char.IsControl(ch))
            {
                continue;
            }

            if (_text.Length >= _options.MaxLength)
            {
                _error = $"Name must be {_options.MaxLength} characters or fewer";
                break;
            }

            _text += ch;
            _error = null;
            _caretTime = 0f;
        }

        if (input.EnterPressed)
        {
            return TextPromptResult.Confirmed;
        }

        if (input.LeftMousePressed)
        {
            GetLayout(screenWidth, screenHeight, out _, out _, out var cancel, out var confirm);
            if (confirm.Contains(_mouseX, _mouseY))
            {
                return TextPromptResult.Confirmed;
            }

            if (cancel.Contains(_mouseX, _mouseY))
            {
                return TextPromptResult.Cancelled;
            }
        }

        return TextPromptResult.None;
    }

    public void Render(int screenWidth, int screenHeight, float dpiScale)
    {
        if (!IsOpen)
        {
            return;
        }

        _screenUi.BeginBatch(dpiScale);
        GetLayout(screenWidth, screenHeight, out var box, out var field, out var cancel, out var confirm);

        _screenUi.DrawRect(0f, 0f, screenWidth, screenHeight, Overlay, 0.5f);
        _screenUi.DrawRect(box.X - 1f, box.Y - 1f, box.Width + 2f, box.Height + 2f, PanelBorder);
        _screenUi.DrawRect(box.X, box.Y, box.Width, box.Height, Panel);

        _screenUi.DrawString(box.X + Padding, box.Y + Padding, _options.Title, Title, TitleScale, FontWeight.SemiBold);
        _screenUi.DrawString(
            box.X + Padding,
            box.Y + Padding + 30f,
            FitText(_options.Subtitle, BoxWidth - Padding * 2f, SmallTextScale),
            Muted,
            SmallTextScale);

        _screenUi.DrawRect(field.X - 1f, field.Y - 1f, field.Width + 2f, field.Height + 2f, FieldBorder);
        _screenUi.DrawRect(field.X, field.Y, field.Width, field.Height, Field);

        // Show the tail of the text when it is wider than the field.
        var textAreaWidth = field.Width - 24f;
        var visible = _text;
        while (visible.Length > 0 && _screenUi.MeasureWidth(visible, TextScale) > textAreaWidth)
        {
            visible = visible[1..];
        }

        var lineHeight = _screenUi.MeasureLineHeight(TextScale);
        var textX = field.X + 10f;
        var textY = field.Y + (field.Height - lineHeight) * 0.5f;
        _screenUi.DrawString(textX, textY, visible, Title, TextScale);
        if (_text.Length == 0 && _options.Placeholder.Length > 0)
        {
            _screenUi.DrawString(textX + 4f, textY, _options.Placeholder, Placeholder, TextScale);
        }

        if (_caretTime % 1f < 0.6f)
        {
            var caretX = textX + _screenUi.MeasureWidth(visible, TextScale) + 1f;
            _screenUi.DrawRect(caretX, field.Y + 9f, 1.5f, field.Height - 18f, Title);
        }

        if (_error is not null)
        {
            _screenUi.DrawString(field.X, field.Y + field.Height + 8f, _error, Error, SmallTextScale);
        }

        DrawButton(cancel, "Cancel", ButtonFill, ButtonHover);
        DrawButton(confirm, _options.ConfirmLabel, AccentFill, AccentHover);

        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();

    // The text, shortened with "..." when it is wider than the space it has.
    private string FitText(string text, float maxWidth, float scale)
    {
        if (_screenUi.MeasureWidth(text, scale) <= maxWidth)
        {
            return text;
        }

        var shortened = text;
        while (shortened.Length > 1 && _screenUi.MeasureWidth(shortened + "...", scale) > maxWidth)
        {
            shortened = shortened[..^1];
        }

        return shortened.TrimEnd() + "...";
    }

    private void DrawButton(UiBox rect, string label, Vector3 fill, Vector3 hover)
    {
        var color = rect.Contains(_mouseX, _mouseY) ? hover : fill;
        _screenUi.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color);
        var textWidth = _screenUi.MeasureWidth(label, TextScale);
        var textHeight = _screenUi.MeasureLineHeight(TextScale);
        _screenUi.DrawString(
            rect.X + (rect.Width - textWidth) * 0.5f,
            rect.Y + (rect.Height - textHeight) * 0.5f,
            label,
            ButtonText,
            TextScale);
    }

    private static void GetLayout(
        int screenWidth,
        int screenHeight,
        out UiBox box,
        out UiBox field,
        out UiBox cancel,
        out UiBox confirm)
    {
        box = new UiBox(
            MathF.Round((screenWidth - BoxWidth) * 0.5f),
            MathF.Round((screenHeight - BoxHeight) * 0.5f),
            BoxWidth,
            BoxHeight);
        field = new UiBox(box.X + Padding, box.Y + 78f, BoxWidth - Padding * 2f, FieldHeight);

        var buttonY = box.Y + BoxHeight - Padding - ButtonHeight;
        confirm = new UiBox(box.X + BoxWidth - Padding - ButtonWidth, buttonY, ButtonWidth, ButtonHeight);
        cancel = new UiBox(confirm.X - ButtonGap - ButtonWidth, buttonY, ButtonWidth, ButtonHeight);
    }
}

internal readonly record struct UiBox(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}
