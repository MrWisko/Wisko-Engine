namespace Wisko.Engine.Editor;

public sealed class BoxSelectionController
{
    private const float MinDragPixels = 4f;

    private bool _active;
    private float _startX;
    private float _startY;
    private float _currentX;
    private float _currentY;

    public bool IsActive => _active;

    public void Begin(float mouseX, float mouseY)
    {
        _active = true;
        _startX = mouseX;
        _startY = mouseY;
        _currentX = mouseX;
        _currentY = mouseY;
    }

    public void Update(float mouseX, float mouseY)
    {
        if (!_active)
        {
            return;
        }

        _currentX = mouseX;
        _currentY = mouseY;
    }

    public void Cancel() => _active = false;

    public bool TryGetSelectionRect(out float x, out float y, out float width, out float height)
    {
        GetRect(out x, out y, out width, out height);
        return width >= MinDragPixels || height >= MinDragPixels;
    }

    public void GetRect(out float x, out float y, out float width, out float height)
    {
        x = global::System.Math.Min(_startX, _currentX);
        y = global::System.Math.Min(_startY, _currentY);
        width = global::System.Math.Abs(_currentX - _startX);
        height = global::System.Math.Abs(_currentY - _startY);
    }
}
