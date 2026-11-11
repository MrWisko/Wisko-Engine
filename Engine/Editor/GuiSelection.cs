namespace Wisko.Engine.Editor;

/// <summary>What the UI section's canvas is previewed as: the game view itself, or a fixed screen size scaled to fit.</summary>
public enum GuiPreviewSize
{
    Viewport,
    Hd720,
    Hd1080
}

/// <summary>
/// The GUI element selected in the UI section (one at most), by Id, kept apart from the Part selection:
/// switching ToolBench sections keeps both. Editor state only, never saved.
/// </summary>
public sealed class GuiSelection
{
    public int? SelectedId { get; set; }
}
