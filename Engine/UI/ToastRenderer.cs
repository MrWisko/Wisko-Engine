using Wisko.Engine.Math;

namespace Wisko.Engine.UI;

public sealed class ToastRenderer : IDisposable
{
    private const float DurationSeconds = 2f;
    private const float FadeOutSeconds = 0.6f;
    private const float HorizontalPadding = 14f;
    private const float VerticalPadding = 8f;
    private const float TopMargin = 10f;
    private const float TextScale = 1.65f;

    // A blocking native dialog (file picker) makes the next frame's delta huge; a toast shown right after it must
    // still be readable, so one frame never consumes more than this.
    private const float MaxFrameStepSeconds = 0.1f;

    private IUiRenderer _screenUi = null!;
    private string? _message;
    private float _timeRemaining;

    internal void Initialize(IUiBackend ui) => _screenUi = ui.CreateRenderer();

    public void Show(string message, float durationSeconds = DurationSeconds)
    {
        _message = message;
        _timeRemaining = global::System.Math.Max(durationSeconds, FadeOutSeconds);
    }

    public void Hide() => _message = null;

    public void Update(float deltaTime)
    {
        if (_message is null)
        {
            return;
        }

        _timeRemaining -= global::System.Math.Min(deltaTime, MaxFrameStepSeconds);
        if (_timeRemaining <= 0f)
        {
            _message = null;
        }
    }

    public void Render(int screenWidth, int screenHeight, float dpiScale)
    {
        if (_message is null)
        {
            return;
        }

        var fade = _timeRemaining <= FadeOutSeconds
            ? _timeRemaining / FadeOutSeconds
            : 1f;
        fade = global::System.Math.Clamp(fade, 0f, 1f);

        _screenUi.BeginBatch(dpiScale);

        var textWidth = _screenUi.MeasureWidth(_message, TextScale);
        var textHeight = _screenUi.MeasureLineHeight(TextScale);
        var boxWidth = textWidth + HorizontalPadding * 2f;
        var boxHeight = textHeight + VerticalPadding * 2f;
        var boxX = (screenWidth - boxWidth) * 0.5f;
        var boxY = ToolbarRenderer.ToolbarHeight + TopMargin;

        var background = new Vector3(0.12f, 0.12f, 0.14f) * fade;
        var textColor = new Vector3(1f, 1f, 1f) * fade;

        _screenUi.DrawRect(boxX, boxY, boxWidth, boxHeight, background);
        _screenUi.DrawString(
            boxX + HorizontalPadding,
            boxY + VerticalPadding,
            _message,
            textColor,
            TextScale);
        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();
}
