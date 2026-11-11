using Wisko.Engine.Editor;
using Wisko.Engine.Math;
using Wisko.Engine.Text;

namespace Wisko.Engine.UI;

public sealed class ColorPanelRenderer : IDisposable
{
    public const float SidebarWidth = 228f;
    private const float TitleHeight = 34f;
    private const float ItemHeight = 30f;
    private const float SidePadding = 10f;
    private const float TextScale = 1.85f;
    private const float SmallTextScale = 1.65f;
    private const float SwatchSize = 18f;

    private IUiRenderer _screenUi = null!;
    private bool _isVisible;

    public bool IsVisible => _isVisible;

    internal void Initialize(IUiBackend ui) => _screenUi = ui.CreateRenderer();

    public void Toggle() => _isVisible = !_isVisible;

    public void Close() => _isVisible = false;

    public bool TryConsumeEscape()
    {
        if (!_isVisible)
        {
            return false;
        }

        Close();
        return true;
    }

    public bool IsOverPanel(float mouseX, float mouseY, int screenWidth, int screenHeight)
    {
        if (!_isVisible)
        {
            return false;
        }

        var rect = GetPanelRect(screenWidth, screenHeight);
        return mouseX >= rect.X
            && mouseX <= rect.X + rect.Width
            && mouseY >= rect.Y
            && mouseY <= rect.Y + rect.Height;
    }

    public bool TryHitColor(float mouseX, float mouseY, int screenWidth, int screenHeight, out PaletteColor paletteColor)
    {
        paletteColor = default;

        if (!_isVisible || !IsOverPanel(mouseX, mouseY, screenWidth, screenHeight))
        {
            return false;
        }

        var index = GetItemIndexAt(mouseY, screenWidth, screenHeight);
        if (index < 0 || index >= PartColorPalette.Colors.Length)
        {
            return false;
        }

        paletteColor = PartColorPalette.Colors[index];
        return true;
    }

    public void Render(int screenWidth, int screenHeight, float dpiScale)
    {
        if (!_isVisible)
        {
            return;
        }

        var panel = GetPanelRect(screenWidth, screenHeight);
        var barColor = new Vector3(0.12f, 0.13f, 0.15f);
        var separator = new Vector3(0.22f, 0.24f, 0.28f);
        var titleText = new Vector3(0.92f, 0.94f, 0.98f);
        var itemText = new Vector3(0.82f, 0.84f, 0.88f);
        var itemInactive = new Vector3(0.18f, 0.19f, 0.22f);
        var swatchBorder = new Vector3(0.35f, 0.38f, 0.42f);

        _screenUi.BeginBatch(dpiScale);

        _screenUi.DrawRect(panel.X, panel.Y, panel.Width, panel.Height, barColor);
        _screenUi.DrawRect(panel.X, panel.Y, 1f, panel.Height, separator);

        DrawString(panel.X + SidePadding, panel.Y + 10f, "Color", titleText, TextScale, FontWeight.SemiBold);
        _screenUi.DrawRect(
            panel.X + SidePadding,
            panel.Y + TitleHeight,
            panel.Width - SidePadding * 2f,
            1f,
            separator);

        var listTop = panel.Y + TitleHeight + 8f;
        for (var i = 0; i < PartColorPalette.Colors.Length; i++)
        {
            var color = PartColorPalette.Colors[i];
            var itemY = listTop + i * ItemHeight;
            var itemRectX = panel.X + SidePadding;
            var itemRectW = panel.Width - SidePadding * 2f;
            var itemRectH = ItemHeight - 4f;

            _screenUi.DrawRect(itemRectX, itemY, itemRectW, itemRectH, itemInactive);

            var swatchX = itemRectX + 6f;
            var swatchY = itemY + (itemRectH - SwatchSize) * 0.5f;
            _screenUi.DrawRect(swatchX - 1f, swatchY - 1f, SwatchSize + 2f, SwatchSize + 2f, swatchBorder);
            _screenUi.DrawRect(swatchX, swatchY, SwatchSize, SwatchSize, color.Rgb);

            DrawString(swatchX + SwatchSize + 8f, itemY + 6f, color.Name, itemText, SmallTextScale);
        }

        _screenUi.Flush(screenWidth, screenHeight);
    }

    public void Dispose() => _screenUi.Dispose();

    private static UiRect GetPanelRect(int screenWidth, int screenHeight)
    {
        var x = screenWidth - SidebarWidth;
        var y = ToolbarRenderer.ToolbarHeight;
        var height = screenHeight - y;
        return new UiRect(x, y, SidebarWidth, height);
    }

    private static int GetItemIndexAt(float mouseY, int screenWidth, int screenHeight)
    {
        var panel = GetPanelRect(screenWidth, screenHeight);
        var listTop = panel.Y + TitleHeight + 8f;
        if (mouseY < listTop)
        {
            return -1;
        }

        return (int)((mouseY - listTop) / ItemHeight);
    }

    private void DrawString(float x, float y, string text, Vector3 color, float scale, FontWeight weight = FontWeight.Regular) =>
        _screenUi.DrawString(x, y, text, color, scale, weight);

    private readonly struct UiRect(float x, float y, float width, float height)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public float Width { get; } = width;
        public float Height { get; } = height;
    }
}
