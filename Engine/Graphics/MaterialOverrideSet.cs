namespace Wisko.Engine.Graphics;

/// <summary>
/// A model instance's own versions of some of its imported materials, by material index (the model file's order):
/// immutable, with value equality, so a Part shares it safely with undo snapshots and Play copies. A material without
/// an entry draws as imported.
/// </summary>
public sealed class MaterialOverrideSet : IEquatable<MaterialOverrideSet>
{
    /// <summary>Most materials a model's overrides can address (indices 0 to this - 1).</summary>
    public const int MaxMaterialIndex = 1024;

    private readonly (int Index, SurfaceMaterial Material)[] _entries;

    private MaterialOverrideSet((int Index, SurfaceMaterial Material)[] entries) => _entries = entries;

    public static MaterialOverrideSet Empty { get; } = new([]);

    /// <summary>The overrides in material-index order.</summary>
    public IReadOnlyList<(int Index, SurfaceMaterial Material)> Entries => _entries;

    public int Count => _entries.Length;

    public SurfaceMaterial? Get(int index)
    {
        foreach (var (i, material) in _entries)
        {
            if (i == index)
            {
                return material;
            }
        }

        return null;
    }

    /// <summary>A copy with one material overridden (sanitized), or its override removed (null).</summary>
    public MaterialOverrideSet With(int index, SurfaceMaterial? material)
    {
        if (index < 0 || index >= MaxMaterialIndex)
        {
            return this;
        }

        var list = _entries.Where(e => e.Index != index).ToList();
        if (material is not null)
        {
            list.Add((index, material.Sanitized()));
        }

        return list.Count == 0 ? Empty : new MaterialOverrideSet(list.OrderBy(e => e.Index).ToArray());
    }

    public static MaterialOverrideSet From(IEnumerable<(int Index, SurfaceMaterial Material)> entries)
    {
        var set = Empty;
        foreach (var (index, material) in entries)
        {
            set = set.With(index, material);
        }

        return set;
    }

    public bool Equals(MaterialOverrideSet? other) =>
        other is not null && (ReferenceEquals(this, other) || _entries.AsSpan().SequenceEqual(other._entries));

    public override bool Equals(object? obj) => Equals(obj as MaterialOverrideSet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries)
        {
            hash.Add(entry.Index);
            hash.Add(entry.Material);
        }

        return hash.ToHashCode();
    }

    /// <summary>Equality where null and empty are the same (no overrides).</summary>
    public static bool Same(MaterialOverrideSet? a, MaterialOverrideSet? b) =>
        (a is null || a.Count == 0) ? (b is null || b.Count == 0) : a.Equals(b);
}
