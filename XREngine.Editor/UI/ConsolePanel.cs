using System.Numerics;
using XREngine.Components;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI.Components;

/// <summary>Shows the engine console through recycled native rows.</summary>
public partial class ConsolePanel : NativeVirtualTextPanel
{
    private static readonly ELogCategory[] CategoryOptions = Enum.GetValues<ELogCategory>();
    private ELogCategory? _category;
    private DateTime _lastRefresh;
    private int _pendingRefresh;
    private readonly ConsoleRowCache _cache = new();
    private readonly List<ConsoleEntrySnapshot> _entries = [];
    private ulong _entriesRevision;
    private ELogCategory? _projectedCategory;
    private string _projectedFilter = string.Empty;
    private string _filter = string.Empty;
    private UITextInputComponent? _filterInput;
    private UIButtonComponent? _actionsButton;
    private NativePropertyPopupHost? _actionsPopupHost;
    private UIContextMenuComponent? _actionsPopup;
    private UIComboBoxComponent? _categoryCombo;
    private NativePropertyPopupHost? _categoryPopupHost;
    private UITextComponent? _categoryLabel;
    private UITextComponent? _statusLabel;
    private UIContextMenuComponent? _entryMenu;
    private SceneNode? _entryMenuNode;
    private IUICanvasInputSource? _rightClickInput;
    private ulong? _selectedEntryId;
    private ulong _actionsMenuRevision;
    private bool _autoScroll = true;
    private bool _statusInitialized;
    private int _statusTotalCount;
    private int _statusFilteredCount;
    private ELogCategory? _statusCategory;
    private string _statusFilter = string.Empty;
    private bool _statusAutoScroll;
    private bool _terminal;
    private bool _ownerActive;
    private bool _listening;
    private bool _tickRegistered;
    public event Action<string>? FilterChanged;
    /// <summary>Raised after the category filter changes.</summary>
    public event Action<ELogCategory?>? CategoryChanged;
    /// <summary>Raised after automatic scrolling is turned on or off.</summary>
    public event Action<bool>? AutoScrollChanged;

    public ELogCategory? Category
    {
        get => _category;
        set
        {
            if (_terminal) return;
            SetCategoryCore(value, BeginStateIntent(), null);
        }
    }

    internal void SetCategoryOwned(ELogCategory? value, ulong intent,
        NativeToolPanelStateBinding binding) => SetCategoryCore(value, intent, binding);

    private void SetCategoryCore(ELogCategory? value, ulong intent,
        NativeToolPanelStateBinding? binding)
    {
            ELogCategory? before = _category;
            bool changed;
            try
            {
                changed = SetFieldValidated(ref _category, value, (Owner: this, Intent: intent, Binding: binding),
                    static (state, original, current, _) =>
                    {
                        state.Owner.RequireStateIntent(state.Intent);
                        state.Binding?.RequireOwnedState(state.Intent);
                        if (original != current)
                            throw new InvalidOperationException("The category changed during this edit.");
                    }, propertyName: nameof(Category));
            }
            finally { if (before != _category) PublishStateWrite(); }
            if (!changed || !StateEditCurrent(intent, binding) || _category != value) return;
            _actionsMenuRevision++;
            _categoryCombo?.Select(CategoryId(value));
            if (!StateEditCurrent(intent, binding)) return;
            if (_categoryLabel is not null)
                _categoryLabel.Text = value?.ToString() ?? "All";
            if (!StateEditCurrent(intent, binding)) return;
            RefreshRows();
            if (StateEditCurrent(intent, binding)) CategoryChanged?.Invoke(_category);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (_terminal) return;
            SetAutoScrollCore(value, BeginStateIntent(), null);
        }
    }

    internal void SetAutoScrollOwned(bool value, ulong intent,
        NativeToolPanelStateBinding binding) => SetAutoScrollCore(value, intent, binding);

    private void SetAutoScrollCore(bool value, ulong intent,
        NativeToolPanelStateBinding? binding)
    {
            bool before = _autoScroll;
            bool changed;
            try
            {
                changed = SetFieldValidated(ref _autoScroll, value,
                    (Owner: this, Intent: intent, Binding: binding),
                    static (state, original, current, _) =>
                    {
                        state.Owner.RequireStateIntent(state.Intent);
                        state.Binding?.RequireOwnedState(state.Intent);
                        if (original != current)
                            throw new InvalidOperationException("Automatic scroll changed during this edit.");
                    }, propertyName: nameof(AutoScroll));
            }
            finally { if (before != _autoScroll) PublishStateWrite(); }
            if (!changed || !StateEditCurrent(intent, binding) || _autoScroll != value) return;
            _actionsMenuRevision++;
            UpdateStatus();
            if (StateEditCurrent(intent, binding)) AutoScrollChanged?.Invoke(_autoScroll);
    }

    public int TotalEntryCount => _cache.TotalCount;
    public int FilteredEntryCount => _cache.FilteredCount;

    public string Filter
    {
        get => _filter;
        set
        {
            if (_terminal) return;
            value ??= string.Empty;
            SetFilterCore(value, BeginStateIntent(), null);
        }
    }

    internal void SetFilterOwned(string value, ulong intent,
        NativeToolPanelStateBinding binding) => SetFilterCore(value, intent, binding);

    private void SetFilterCore(string value, ulong intent,
        NativeToolPanelStateBinding? binding)
    {
            string before = _filter;
            bool changed;
            try
            {
                changed = SetFieldValidated(ref _filter, value,
                    (Owner: this, Intent: intent, Binding: binding),
                    static (state, original, current, _) =>
                    {
                        state.Owner.RequireStateIntent(state.Intent);
                        state.Binding?.RequireOwnedState(state.Intent);
                        if (original != current)
                            throw new InvalidOperationException("The console filter changed during this edit.");
                    }, propertyName: nameof(Filter));
            }
            finally { if (before != _filter) PublishStateWrite(); }
            if (!changed || !StateEditCurrent(intent, binding) || _filter != value) return;
            _actionsMenuRevision++;
            if (_filterInput is not null)
                _filterInput.Text = _filter;
            if (!StateEditCurrent(intent, binding)) return;
            RefreshRows();
            if (StateEditCurrent(intent, binding)) FilterChanged?.Invoke(_filter);
    }

    private bool StateEditCurrent(ulong intent, NativeToolPanelStateBinding? binding)
    {
        if (!IsCurrentStateIntent(intent) || _terminal)
        {
            if (binding is not null)
                throw new InvalidOperationException("The console changed during restore.");
            return false;
        }
        binding?.RequireOwnedState(intent);
        return true;
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
        if (_terminal || !_ownerActive || !IsActiveInHierarchy)
            return;
        IUICanvasInputSource? input = BoundableTransform.GetCanvasComponent()?.GetInputComponent();
        if (input is not null && !ReferenceEquals(_rightClickInput, input))
        {
            DetachRightClick();
            _rightClickInput = input;
            input.RightClick += OnRightClick;
        }
    }

    protected override void OnComponentDeactivated()
    {
        _ownerActive = false;
        _actionsMenuRevision++;
        try { DetachRightClick(); }
        finally
        {
            try { _entryMenu?.Hide(); }
            finally
            {
                try { _actionsPopup?.Hide(); }
                finally
                {
                    try { _categoryCombo?.Close(); }
                    finally
                    {
                        try { DetachExternalCallbacks(); }
                        finally { base.OnComponentDeactivated(); }
                    }
                }
            }
        }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        _actionsMenuRevision++;
        FilterChanged = null;
        CategoryChanged = null;
        AutoScrollChanged = null;
        List<Exception>? failures = null;
        AttemptRelease(DetachRightClick, ref failures);
        SceneNode? menuNode = _entryMenuNode;
        UIButtonComponent? actionsButton = _actionsButton;
        NativePropertyPopupHost? actionsHost = _actionsPopupHost;
        UIComboBoxComponent? categoryCombo = _categoryCombo;
        NativePropertyPopupHost? categoryHost = _categoryPopupHost;
        _entryMenuNode = null;
        _entryMenu = null;
        _selectedEntryId = null;
        _actionsButton = null;
        _actionsPopupHost = null;
        _actionsPopup = null;
        _categoryCombo = null;
        _categoryPopupHost = null;
        _categoryLabel = null;
        _statusLabel = null;
        if (_filterInput is { } input)
        {
            _filterInput = null;
            AttemptRelease(() => input.Submitted -= FilterSubmitted, ref failures);
            AttemptRelease(() => input.Cancelled -= FilterCancelled, ref failures);
        }
        if (actionsButton is not null)
            AttemptRelease(() => actionsButton.UnregisterClickActions(OnActionsClick), ref failures);
        if (categoryCombo is not null)
        {
            AttemptRelease(() => categoryCombo.Opening -= OnCategoryOpening, ref failures);
            AttemptRelease(() => categoryCombo.SelectionChanged -= OnCategorySelectionChanged, ref failures);
        }
        if (actionsHost is not null)
            AttemptRelease(() => actionsHost.Destroy(true), ref failures);
        if (categoryHost is not null)
            AttemptRelease(() => categoryHost.Destroy(true), ref failures);
        AttemptRelease(DetachExternalCallbacks, ref failures);
        if (menuNode is not null)
            AttemptRelease(() => menuNode.Destroy(false), ref failures);
        AttemptRelease(() => base.OnDestroying(), ref failures);
        if (failures is { Count: > 0 })
            throw new AggregateException("Console cleanup failed.", failures);
    }

    private static void AttemptRelease(Action release, ref List<Exception>? failures)
    {
        try { release(); }
        catch (Exception error) { (failures ??= []).Add(error); }
    }

    private void DetachRightClick()
    {
        IUICanvasInputSource? input = _rightClickInput;
        _rightClickInput = null;
        if (input is not null)
            input.RightClick -= OnRightClick;
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
        if (BoundableTransform.GetCanvasTransform()?.SceneNode is { } canvasNode)
        {
            SceneNode menuNode = new(canvasNode) { Name = "Console entry menu", IsActiveSelf = false };
            menuNode.SetTransform<UIBoundableTransform>().Visibility = EVisibility.Collapsed;
            _entryMenuNode = menuNode;
            _entryMenu = menuNode.AddComponent<UIContextMenuComponent>()!;
            menuNode.IsActiveSelf = true;
        }
        var actions = NativeUIElements.CreateButton(commands, "Actions", null, width: 88, height: 28);
        NativePropertyPopupHost actionsPopupHost = actions.Node.AddComponent<NativePropertyPopupHost>()!;
        UIContextMenuComponent actionsPopup = actionsPopupHost.CreatePopup();
        _actionsButton = actions.Button;
        _actionsPopupHost = actionsPopupHost;
        _actionsPopup = actionsPopup;
        actions.Button.RegisterClickActions(OnActionsClick);

        SceneNode categoryNode = commands.NewChild();
        categoryNode.SetTransform<UIBoundableTransform>().Width = 104;
        UIComboBoxComponent combo = categoryNode.AddComponent<UIComboBoxComponent>()!;
        NativePropertyPopupHost popupHost = categoryNode.AddComponent<NativePropertyPopupHost>()!;
        combo.Popup = popupHost.CreatePopup();
        ELogCategory[] categories = CategoryOptions;
        UIComboOption[] options = new UIComboOption[categories.Length + 1];
        options[0] = new UIComboOption(1, "All");
        for (int index = 0; index < categories.Length; index++)
            options[index + 1] = new UIComboOption(CategoryId(categories[index]), categories[index].ToString());
        combo.SetOptions(options);
        combo.Select(CategoryId(_category));
        combo.Opening += OnCategoryOpening;
        combo.SelectionChanged += OnCategorySelectionChanged;
        _categoryCombo = combo;
        _categoryPopupHost = popupHost;
        categoryNode.NewChild<UITextComponent>(out var categoryLabel);
        _categoryLabel = categoryLabel;
        categoryLabel.Text = _category?.ToString() ?? "All";
        categoryLabel.VerticalAlignment = EVerticalAlignment.Center;

        SceneNode filterNode = commands.NewChild();
        filterNode.SetTransform<UIBoundableTransform>().Width = 144;
        var (input, _, _, _) = NativeUIElements.CreateTextInputField<UITextInputComponent>(filterNode);
        _filterInput = input;
        input.SingleLineMode = true;
        input.Text = _filter;
        input.Submitted += FilterSubmitted;
        input.Cancelled += FilterCancelled;
    }

    private static ulong CategoryId(ELogCategory? category)
        => category is null ? 1UL : (ulong)(int)category.Value + 2UL;

    private void OnActionsClick(UIInteractableComponent _)
    {
        UIContextMenuComponent? popup = _actionsPopup;
        NativePropertyPopupHost? host = _actionsPopupHost;
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || popup is null || host is null)
            return;
        ulong revision = ++_actionsMenuRevision;
        ELogCategory? category = Category;
        bool autoScroll = AutoScroll;
        ulong? selectedId = _selectedEntryId;
        LogEntry? selectedEntry = selectedId is { } id && _cache.TryGetVisibleEntry(id, out LogEntry? entry)
            ? entry : null;
        popup.Show(host.GetPosition(),
            new ContextMenuItem("Clear All", () =>
            {
                if (!CanApplyActionsMenu(revision, popup)) return;
                Debug.ClearConsoleEntries();
                RefreshRows();
            }),
            new ContextMenuItem("Clear Category", () =>
            {
                if (!CanApplyActionsMenu(revision, popup) || category is not { } admitted ||
                    Category != admitted) return;
                Debug.ClearConsoleEntries(admitted);
                RefreshRows();
            }, category is not null),
            new ContextMenuItem("Copy Selected Message", () =>
            {
                if (CanApplyActionsMenu(revision, popup) && selectedId is { } admitted &&
                    selectedEntry is not null && _selectedEntryId == admitted)
                    CopyEntry(admitted, selectedEntry, false);
            }, selectedEntry is not null),
            new ContextMenuItem("Copy Selected Entry", () =>
            {
                if (CanApplyActionsMenu(revision, popup) && selectedId is { } admitted &&
                    selectedEntry is not null && _selectedEntryId == admitted)
                    CopyEntry(admitted, selectedEntry, true);
            }, selectedEntry is not null),
            new ContextMenuItem(autoScroll ? "Disable Auto-scroll" : "Enable Auto-scroll", () =>
            {
                if (CanApplyActionsMenu(revision, popup) && AutoScroll == autoScroll)
                    AutoScroll = !autoScroll;
            }));
    }

    private bool CanApplyActionsMenu(ulong revision, UIContextMenuComponent popup)
        => !_terminal && _ownerActive && IsActiveInHierarchy &&
            revision == _actionsMenuRevision && ReferenceEquals(_actionsPopup, popup) && popup.IsOpen;

    private void OnCategoryOpening(UIComboBoxComponent combo)
    {
        if (ReferenceEquals(_categoryCombo, combo) && _categoryPopupHost is { } host)
            combo.PopupPosition = host.GetPosition();
    }

    private void OnCategorySelectionChanged(UIValueChange<ulong?> change)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        if (change.NewValue is not { } id || id == 1)
        {
            if (_category is not null) Category = null;
        }
        else if (id - 2 < (ulong)CategoryOptions.Length)
        {
            ELogCategory selected = (ELogCategory)(id - 2);
            if (_category != selected) Category = selected;
        }
    }

    protected override float FooterHeight => 24;

    protected override void CreateFooter(SceneNode parent)
    {
        parent.NewChild<UITextComponent>(out UITextComponent status);
        _statusLabel = status;
        status.Text = "0 entries";
        status.VerticalAlignment = EVerticalAlignment.Center;
    }

    private void FilterSubmitted(UITextInputComponent field)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
        string submitted = field.Text.Trim();
        if (_filter != submitted) Filter = submitted;
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
        int previousCount = _cache.FilteredCount;
        bool stayAtEnd = _autoScroll && ScrollOffset >= MaximumScrollOffset(previousCount) - 24;
        ulong revision = Debug.CopyConsoleEntriesTo(_entries, _entriesRevision);
        bool needsProjection = revision != _entriesRevision ||
            _projectedCategory != _category || _projectedFilter != _filter;
        SetField(ref _entriesRevision, revision, publishNotifications: false);
        bool rowsChanged = needsProjection && _cache.Update(_entries, _category, _filter);
        SetField(ref _projectedCategory, _category, publishNotifications: false);
        SetField(ref _projectedFilter, _filter, publishNotifications: false);
        if (_selectedEntryId is { } selected && !_cache.TryGetVisibleEntry(selected, out _))
        {
            _selectedEntryId = null;
            _actionsMenuRevision++;
        }
        if (rowsChanged)
            SetRows(_cache.VisibleRows);
        if (stayAtEnd)
            SetDerivedScrollOffset(MaximumScrollOffset(_cache.FilteredCount));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_statusLabel is null || _statusInitialized && _statusTotalCount == _cache.TotalCount &&
            _statusFilteredCount == _cache.FilteredCount && _statusCategory == _category &&
            _statusFilter == _filter && _statusAutoScroll == _autoScroll)
            return;
        _statusInitialized = true;
        _statusTotalCount = _cache.TotalCount;
        _statusFilteredCount = _cache.FilteredCount;
        _statusCategory = _category;
        _statusFilter = _filter;
        _statusAutoScroll = _autoScroll;
        string status = _category is not null || _filter.Length != 0
            ? $"{_cache.FilteredCount} / {_cache.TotalCount} entries"
            : $"{_cache.TotalCount} entries";
        _statusLabel.Text = status + (_autoScroll ? " | Auto-scroll on" : " | Auto-scroll off");
    }

    protected override void OnRowActivated(ulong id)
    {
        if (_ownerActive && !_terminal && _cache.TryGetVisibleEntry(id, out _))
        {
            if (_selectedEntryId != id) _actionsMenuRevision++;
            _selectedEntryId = id;
        }
    }

    private void CopyEntry(ulong id, LogEntry admitted, bool full)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            !_cache.TryGetVisibleEntry(id, out LogEntry? current) || !ReferenceEquals(current, admitted))
            return;
        string text = admitted.Message;
        if (full)
        {
            if (!_cache.TryGetVisibleText(id, out string? displayed) || displayed is null)
                return;
            text = displayed;
        }
        IRuntimeClipboardServices? clipboard = RuntimeClipboardServices.Current;
        if (clipboard is null)
        {
            if (_statusLabel is not null) _statusLabel.Text = "Clipboard is unavailable.";
            return;
        }
        try
        {
            clipboard.SetText(text);
        }
        catch (Exception error)
        {
            if (_statusLabel is not null) _statusLabel.Text = "Clipboard failed: " + error.Message;
        }
    }

    private void OnRightClick(UIInteractableComponent target)
    {
        IUICanvasInputSource? input = _rightClickInput;
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || input is null ||
            !ReferenceEquals(input, BoundableTransform.GetCanvasComponent()?.GetInputComponent()) ||
            !TryGetBoundTextRowId(target, out ulong id) ||
            !_cache.TryGetVisibleEntry(id, out LogEntry? entry) || entry is null ||
            !input.TryGetPresentedPointerPosition(target, out Vector2 position))
            return;
        if (_selectedEntryId != id) _actionsMenuRevision++;
        _selectedEntryId = id;
        UIContextMenuComponent? menu = _entryMenu;
        if (menu is null || _terminal || !_ownerActive || !ReferenceEquals(_rightClickInput, input) ||
            !TryGetBoundTextRowId(target, out ulong currentId) || currentId != id ||
            !_cache.TryGetVisibleEntry(id, out LogEntry? latest) ||
            !ReferenceEquals(latest, entry))
            return;
        menu.Show(position,
            new ContextMenuItem("Copy Message", () => CopyEntry(id, entry, false)),
            new ContextMenuItem("Copy Full Entry", () => CopyEntry(id, entry, true)));
    }

}
