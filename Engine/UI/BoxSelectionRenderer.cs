using Wisko.Engine.Editor;
using Wisko.Engine.Math;

namespace Wisko.Engine.UI;

public sealed class BoxSelectionRenderer : IDisposable
{
    private const float FillAlpha = 0.22f;
    private const float BorderThickness = 2f;

    private IUiRenderer _screenUi = null!;

    internal void Initialize(IUiBackend ui) => _screenUi = ui.CreateRenderer();

    public void Render(int screenWidth, int screenHeight, float dpiScale, BoxSelectionController controller)
    {
        if (!controller.IsActive)
        {
            return;
        }

        controller.GetRect(out var x, out var y, out var width, out var height);
        if (width < 1f && height < 1f)
        {
            return;
        }

        var fillColor = new Vector3(0.20f, 0.45f, 0.95f);
        var borderColor = new Vector3(0.55f, 0.78f, 1.0f);

        _screenUi.BeginBatch(dpiScale);
        _screenUi.DrawRect(x, y, width, height, fillColor, FillAlpha);
        _screenUi.DrawBorderRect(x, y, width, height, borderColor, BorderThickness);
        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();
}
