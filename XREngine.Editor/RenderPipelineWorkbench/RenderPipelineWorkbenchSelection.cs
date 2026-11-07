namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Holds the one selection that every Workbench view shares, with back and forward history.
/// The history is a fixed ring buffer, so navigation does not allocate.
/// </summary>
public sealed class RenderPipelineWorkbenchSelection
{
    /// <summary>The number of history entries that the selection keeps.</summary>
    public const int HistoryCapacity = 64;

    private readonly RenderPipelineWorkbenchSelectionEntry[] _history = new RenderPipelineWorkbenchSelectionEntry[HistoryCapacity];
    private int _start;
    private int _count;
    private int _cursor = -1;

    /// <summary>Gets the current selection.</summary>
    public RenderPipelineWorkbenchSelectionEntry Current
        => _cursor < 0 ? RenderPipelineWorkbenchSelectionEntry.None : _history[ToSlot(_cursor)];

    /// <summary>Gets the number of entries in the history.</summary>
    public int HistoryCount => _count;

    /// <summary>Gets whether <see cref="Back"/> can move.</summary>
    public bool CanGoBack => _cursor > 0;

    /// <summary>Gets whether <see cref="Forward"/> can move.</summary>
    public bool CanGoForward => _cursor >= 0 && _cursor < _count - 1;

    /// <summary>Gets the selected command or container node id, or 0.</summary>
    public int NodeId
        => Current.Kind is ERenderPipelineWorkbenchSelectionKind.Command or ERenderPipelineWorkbenchSelectionKind.Container ? Current.NodeId : 0;

    /// <summary>Gets the selected pass index, or -1.</summary>
    public int PassIndex
        => Current.Kind == ERenderPipelineWorkbenchSelectionKind.Pass ? Current.PassIndex : -1;

    /// <summary>Gets the selected resource name, or null.</summary>
    public string? ResourceName
        => Current.Kind == ERenderPipelineWorkbenchSelectionKind.Resource ? Current.Name : null;

    /// <summary>Gets the selected stage key, or null.</summary>
    public string? StageKey
        => Current.Kind == ERenderPipelineWorkbenchSelectionKind.Stage ? Current.Name : null;

    /// <summary>
    /// Selects <paramref name="entry"/>. A new selection removes the forward history.
    /// Selecting the current object again changes nothing.
    /// </summary>
    public void Select(RenderPipelineWorkbenchSelectionEntry entry)
    {
        if (_cursor >= 0 && Current == entry)
            return;

        // Drop forward entries.
        _count = _cursor + 1;
        if (_count == HistoryCapacity)
        {
            // The ring is full: forget the oldest entry.
            _start = (_start + 1) % HistoryCapacity;
            _count--;
        }

        _history[ToSlot(_count)] = entry;
        _count++;
        _cursor = _count - 1;
    }

    /// <summary>Moves to the previous selection. Returns false when there is none.</summary>
    public bool Back()
    {
        if (!CanGoBack)
            return false;

        _cursor--;
        return true;
    }

    /// <summary>Moves to the next selection. Returns false when there is none.</summary>
    public bool Forward()
    {
        if (!CanGoForward)
            return false;

        _cursor++;
        return true;
    }

    /// <summary>Clears the selection and the history.</summary>
    public void Clear()
    {
        _start = 0;
        _count = 0;
        _cursor = -1;
    }

    private int ToSlot(int logicalIndex)
        => (_start + logicalIndex) % HistoryCapacity;
}
