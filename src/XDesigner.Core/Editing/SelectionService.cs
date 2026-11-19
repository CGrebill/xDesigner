namespace XDesigner.Core.Editing;

/// <summary>
/// The selected elements of one document, by <see cref="Xaml.ElementNode.Id"/> (§15.1). The design surface,
/// the outline and the properties window all read and write this one object, so they always agree.
/// </summary>
public sealed class SelectionService
{
    private readonly List<int> _selected = [];

    /// <summary>Selected ids in the order they were selected.</summary>
    public IReadOnlyList<int> Selected => _selected;

    /// <summary>The element whose properties are shown and that gets resize handles: the most recently selected one.</summary>
    public int? Primary => _selected.Count > 0 ? _selected[^1] : null;

    public bool IsSelected(int id) => _selected.Contains(id);

    /// <summary>Raised after any change. Not raised when a call leaves the selection as it was.</summary>
    public event EventHandler? Changed;

    /// <summary>Selects only <paramref name="id"/> (plain click).</summary>
    public void Select(int id) => Set([id]);

    /// <summary>Replaces the selection.</summary>
    public void Set(IEnumerable<int> ids)
    {
        var next = ids.Distinct().ToList();
        if (next.SequenceEqual(_selected))
        {
            return;
        }

        _selected.Clear();
        _selected.AddRange(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds to the selection and makes it primary (Shift+click).</summary>
    public void Add(int id)
    {
        if (_selected.Count > 0 && _selected[^1] == id)
        {
            return;
        }

        _selected.Remove(id);
        _selected.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds or removes (Ctrl+click).</summary>
    public void Toggle(int id)
    {
        if (!_selected.Remove(id))
        {
            _selected.Add(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear() => Set([]);

    /// <summary>Drops ids that no longer exist (after elements are deleted or the document reloads).</summary>
    public void RemoveMissing(Func<int, bool> exists) => Set(_selected.Where(exists));
}
