using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

/// <summary>Shows the engine console through recycled native rows.</summary>
public partial class ConsolePanel : NativeVirtualTextPanel
{
    private ELogCategory? _category;
    private DateTime _lastRefresh;
    private int _pendingRefresh;
    private readonly List<(ulong Id, string Text)> _visible = [];
    private readonly Dictionary<LogEntry, ulong> _entryIds = [];
    private readonly HashSet<LogEntry> _seenEntries = [];
    private readonly List<LogEntry> _retiredEntries = [];
    private ulong _nextEntryId;
    private string _filter = string.Empty;
    private UITextInputComponent? _filterInput;
    private bool _terminal;
    private bool _ownerActive;
    private bool _listening;
    private bool _tickRegistered;
    public event Action<string>? FilterChanged;

    public ELogCategory? Category
    {
        get => _category;
        set { _category = value; if (!_terminal) RefreshRows(); }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? string.Empty;
            if (_terminal) return;
            if (_filterInput is not null)
                _filterInput.Text = _filter;
            RefreshRows();
            FilterChanged?.Invoke(_filter);
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_terminal || !IsActiveInHierarchy) return;
        _ownerActive = true;
        if (!_listening)
        {
            Debug.ConsoleEntryAdded += EntryAdded;
            _listening = true;
        }
        if (!_tickRegistered)
        {
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick);
            _tickRegistered = true;
        }
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        try { DetachExternalCallbacks(); }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        FilterChanged = null;
        if (_filterInput is { } input)
        {
            input.Submitted -= FilterSubmitted;
            input.Cancelled -= FilterCancelled;
            _filterInput = null;
        }
        try { DetachExternalCallbacks(); }
        finally { base.OnDestroying(); }
    }

    private void DetachExternalCallbacks()
    {
        if (_tickRegistered)
        {
            _tickRegistered = false;
            try { UnregisterTick(ETickGroup.Late, ETickOrder.Scene, RefreshTick); }
            finally { DetachConsoleEvent(); }
        }
        else DetachConsoleEvent();
    }

    private void DetachConsoleEvent()
    {
        if (_listening)
        {
            _listening = false;
            Debug.ConsoleEntryAdded -= EntryAdded;
        }
    }

    protected override void CreateCommands(SceneNode commands)
    {
        AddCommand("All", () => Category = null);
        AddCommand("General", () => Category = ELogCategory.General);
        AddCommand("UI", () => Category = ELogCategory.UI);
        AddCommand("Clear", () => { Debug.ClearConsoleEntries(); RefreshRows(); });
        SceneNode filterNode = commands.NewChild();
        filterNode.SetTransform<UIBoundableTransform>().Width = 144;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(filterNode);
        _filterInput = input;
        input.SingleLineMode = true;
        input.Text = _filter;
        input.Submitted += FilterSubmitted;
        input.Cancelled += FilterCancelled;
    }

    private void FilterSubmitted(UITextInputComponent field)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        Filter = field.Text.Trim();
    }

    private void FilterCancelled(UITextInputComponent field)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        field.Text = _filter;
    }

    private void RefreshTick()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        DateTime now = DateTime.UtcNow;
        if (now - _lastRefresh < TimeSpan.FromMilliseconds(250))
            return;
        if (Interlocked.Exchange(ref _pendingRefresh, 0) == 0 && now - _lastRefresh < TimeSpan.FromSeconds(2))
            return;
        _lastRefresh = now;
        RefreshRows();
    }

    private void EntryAdded(LogEntry _)
    {
        if (!_terminal && _ownerActive && IsActiveInHierarchy)
            Interlocked.Exchange(ref _pendingRefresh, 1);
    }

    protected override void RefreshRows()
    {
        if (_terminal) return;
        _visible.Clear();
        List<LogEntry> entries = Debug.GetConsoleEntries();
        _seenEntries.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            LogEntry entry = entries[i];
            _seenEntries.Add(entry);
            if (_category is { } category && entry.Category != category)
                continue;
            if (_filter.Length != 0 && !entry.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;
            string line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Category}] {entry.Message}";
            if (entry.RepeatCount > 1)
                line += $" (x{entry.RepeatCount})";
            if (!_entryIds.TryGetValue(entry, out ulong id))
            {
                id = ++_nextEntryId;
                _entryIds.Add(entry, id);
            }
            _visible.Add((id, line));
        }
        _retiredEntries.Clear();
        foreach (LogEntry entry in _entryIds.Keys)
            if (!_seenEntries.Contains(entry))
                _retiredEntries.Add(entry);
        foreach (LogEntry entry in _retiredEntries)
            _entryIds.Remove(entry);
        SetRows(_visible);
    }
}
