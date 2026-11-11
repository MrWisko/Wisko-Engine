using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

internal sealed class EditorShapeState
{
    public ShapeType SelectedShape { get; private set; } = ShapeType.Block;

    public void SetShape(ShapeType shape)
    {
        if (SelectedShape == shape)
        {
            return;
        }

        SelectedShape = shape;
        Console.WriteLine($"[EditorShape] Selected shape={shape}");
    }
}
