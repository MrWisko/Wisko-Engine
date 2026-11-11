namespace Wisko.Engine.World;

/// <summary>
/// Tracks selected Parts in the editor viewport.
/// </summary>
public sealed class EditorSelection
{
    private readonly List<Part> _selectedParts = [];

    public IReadOnlyList<Part> SelectedParts => _selectedParts;

    public Part? SelectedPart => _selectedParts.Count > 0 ? _selectedParts[0] : null;

    public bool IsSelected(Part part) => _selectedParts.Contains(part);

    public void Select(Part? part)
    {
        SelectMany(part is null ? [] : [part]);
    }

    public void SelectMany(IReadOnlyList<Part> parts)
    {
        _selectedParts.Clear();
        if (parts.Count > 0)
        {
            _selectedParts.AddRange(parts);
        }

        LogSelection();
    }

    private void LogSelection()
    {
        if (_selectedParts.Count == 0)
        {
            Console.WriteLine("[Selection] Cleared");
        }
        else if (_selectedParts.Count == 1)
        {
            Console.WriteLine($"[Selection] {_selectedParts[0].Name}");
        }
        else
        {
            Console.WriteLine($"[Selection] {_selectedParts.Count} parts");
        }
    }
}
