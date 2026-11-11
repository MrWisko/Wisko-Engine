using Wisko.Engine.Rendering;
using Wisko.Engine.World;
using static Wisko.Engine.Rendering.ScreenProjection;

namespace Wisko.Engine.Editor;

internal static class PartBoxSelect
{
    public static List<Part> CollectPartsInRect(
        Workspace workspace,
        EditorCamera camera,
        int screenWidth,
        int screenHeight,
        float rectX,
        float rectY,
        float rectWidth,
        float rectHeight)
    {
        var selectionRect = new ScreenRect(rectX, rectY, rectWidth, rectHeight);
        var selected = new List<Part>();

        foreach (var part in workspace.Parts)
        {
            if (IsPartInSelectionRect(part, camera, screenWidth, screenHeight, selectionRect))
            {
                selected.Add(part);
            }
        }

        return selected;
    }

    private static bool IsPartInSelectionRect(
        Part part,
        EditorCamera camera,
        int screenWidth,
        int screenHeight,
        ScreenRect selectionRect)
    {
        if (TryProjectWorldPoint(part.Position, camera, screenWidth, screenHeight, out var centerX, out var centerY)
            && selectionRect.ContainsPoint(centerX, centerY))
        {
            return true;
        }

        var worldBounds = PartBounds.GetWorldAabb(part);
        if (!TryProjectWorldBounds(
                worldBounds.Min,
                worldBounds.Max,
                camera,
                screenWidth,
                screenHeight,
                out var screenBounds))
        {
            return false;
        }

        return selectionRect.Overlaps(screenBounds);
    }
}
