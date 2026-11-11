namespace Wisko.Engine.Editor;

public sealed class EditorToolState
{
    public EditorToolMode ActiveTool { get; private set; } = EditorToolMode.Select;

    public void SetTool(EditorToolMode tool)
    {
        if (ActiveTool == tool)
        {
            return;
        }

        ActiveTool = tool;
        Console.WriteLine($"[Tool] {tool}");
    }
}
