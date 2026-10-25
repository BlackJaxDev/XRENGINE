using System.Numerics;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Provides a virtual text view with a small command bar.</summary>
public abstract class NativeVirtualTextPanel : EditorPanel, IUIScrollReceiver
{
    private readonly NativeTextList _rows = new();
    private UIListTransform? _list;
    private float _storedScrollOffset;
    private bool _terminal;
    private bool _ownerActive;
    private ulong _ownerRevision;
    private ulong _stateIntentRevision;
    private ulong _stateRevision;
    private NativeToolPanelStateBinding? _activeRestore;
    internal ulong StateIntentRevision => _stateIntentRevision;
    internal ulong StateRevision => _stateRevision;
    internal ulong ScrollIntentRevision => _list?.ContentScrollIntentRevision ?? 0;
    internal bool TryCaptureScrollOwner(out NativePanelScrollOwnerReceipt? owner)
    {
        owner = _list is null ? null :
            NativePanelScrollOwnerReceipt.Capture(_list, Transform);
        return _list is null || owner is not null;
    }
    internal bool HasCurrentScrollOwner(NativePanelScrollOwnerReceipt? owner)
        => owner is { } captured ? _list is { } list &&
            captured.IsCurrent(list, Transform) : _list is null;

    /// <summary>Starts one panel-state change before any user callback runs.</summary>
    protected internal ulong BeginStateIntent() => ++_stateIntentRevision;

    protected internal bool IsCurrentStateIntent(ulong intent)
        => intent != 0 && _stateIntentRevision == intent &&
            !_terminal && !IsDestroyed && !IsDestroyQueued;

    protected internal void RequireStateIntent(ulong intent)
    {
        if (!IsCurrentStateIntent(intent))
            throw new InvalidOperationException("The panel state changed during this edit.");
    }

    protected internal void PublishStateWrite() => ++_stateRevision;
    internal void SetActiveRestore(NativeToolPanelStateBinding? binding)
        => _activeRestore = binding;
    protected IVirtualListProvider? RowProviderOverride { get; set; }
    private IVirtualListProvider RowProvider => RowProviderOverride ?? _rows;

    protected SceneNode? Commands { get; private set; }
    protected int RowCount => RowProvider.Count;
    protected float MaximumScrollOffset(int rowCount)
        => MathF.Max(0, rowCount * 24.0f - ViewportHeight());
    protected bool TryGetBoundTextRowId(UIInteractableComponent target, out ulong id)
    {
        id = 0;
        if (_terminal || !_ownerActive || _list is null ||
            !ReferenceEquals(_list.VirtualProvider, _rows) ||
            target.IsDestroyed || target.IsDestroyQueued ||
            target.BoundableTransform is not { } row ||
            !_rows.TryGetBoundId(row, out id) ||
            !_list.TryGetVirtualRowLease(row, out UIVirtualRowLease lease) || lease.ItemId != id)
            return false;
        return true;
    }
    public float ScrollOffset
    {
        get => _ownerActive && !_terminal && IsActiveInHierarchy
            ? _list?.ContentScrollOffset ?? _storedScrollOffset : _storedScrollOffset;
        set
        {
            float next = float.IsFinite(value) ? Math.Max(0, value) : 0;
            SetScrollOffsetCore(next, BeginStateIntent(), null);
        }
    }

    internal void SetScrollOffsetOwned(float value, ulong intent,
        NativeToolPanelStateBinding binding)
        => SetScrollOffsetCore(float.IsFinite(value) ? Math.Max(0, value) : 0,
            intent, binding);

    protected void SetDerivedScrollOffset(float value)
    {
        if (_activeRestore is { RestoreIntent: not 0 } binding &&
            IsCurrentStateIntent(binding.RestoreIntent))
            SetScrollOffsetOwned(value, binding.RestoreIntent, binding);
        else
        {
            float next = float.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_storedScrollOffset == next &&
                (_list is null || _list.ContentScrollOffset == next)) return;
            ulong expectedIntent = _stateIntentRevision;
            ObjectConstructionIdentity panelIdentity = ObjectConstructionIdentity.Capture(this);
            float before = _storedScrollOffset;
            try
            {
                SetFieldValidated(ref _storedScrollOffset, next,
                    (Panel: this, Intent: expectedIntent, Identity: panelIdentity),
                    static (state, original, current, _) =>
                    {
                        if (!state.Identity.IsCurrent ||
                            state.Panel._stateIntentRevision != state.Intent ||
                            original != current)
                            throw new InvalidOperationException("The derived scroll state changed.");
                    }, publishNotifications: false, propertyName: nameof(ScrollOffset));
            }
            finally { if (before != _storedScrollOffset) PublishStateWrite(); }
            if (_terminal || !_ownerActive || !IsActiveInHierarchy || _list is not { } list)
                return;
            ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
            list.SetContentScrollOffsetGuarded(next,
                (Panel: this, Intent: expectedIntent, PanelIdentity: panelIdentity,
                    List: list, ListIdentity: listIdentity),
                static state =>
                {
                    if (!state.PanelIdentity.IsCurrent ||
                        state.Panel._stateIntentRevision != state.Intent ||
                        !state.ListIdentity.IsCurrent ||
                        !ReferenceEquals(state.Panel._list, state.List))
                        throw new InvalidOperationException("The derived scroll owner changed.");
                });
            UpdateScroll();
        }
    }

    private void SetScrollOffsetCore(float value, ulong intent,
        NativeToolPanelStateBinding? binding)
    {
        float before = _storedScrollOffset;
        try
        {
            SetFieldValidated(ref _storedScrollOffset, value,
                (Panel: this, Intent: intent, Binding: binding),
                static (state, original, current, _) =>
                {
                    state.Panel.RequireStateIntent(state.Intent);
                    state.Binding?.RequireOwnedState(state.Intent);
                    if (original != current)
                        throw new InvalidOperationException("The panel scroll changed during this edit.");
                }, publishNotifications: false, propertyName: nameof(ScrollOffset));
        }
        finally { if (before != _storedScrollOffset) PublishStateWrite(); }
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _list is not { } list)
            return;
        NativePanelScrollOwnerReceipt owner =
            NativePanelScrollOwnerReceipt.Capture(list, Transform) ??
            throw new InvalidOperationException("The panel Rows list lost its owner.");
        list.SetContentScrollOffsetGuarded(value,
            (Panel: this, Intent: intent, Binding: binding, List: list,
                Owner: owner),
            static state =>
            {
                state.Panel.RequireStateIntent(state.Intent);
                state.Binding?.RequireOwnedState(state.Intent);
                if (!ReferenceEquals(state.Panel._list, state.List) ||
                    !state.Owner.IsCurrent(state.List, state.Panel.Transform))
                    throw new InvalidOperationException("The panel scroll owner changed.");
            });
        if (!owner.IsCurrent(list, Transform))
            throw new InvalidOperationException("The panel Rows list moved during restore.");
        if (binding is not null && list.ContentScrollOffset != value)
            throw new InvalidOperationException("The panel scroll write was rejected.");
        if (!IsCurrentStateIntent(intent))
        {
            binding?.RequireOwnedState(intent);
            return;
        }
        binding?.RequireOwnedState(intent);
        if (binding is null)
            UpdateScroll();
        else
            UpdateScrollOwned(intent, binding, list, owner);
        binding?.RequireOwnedState(intent);
    }

    private void UpdateScrollOwned(ulong intent, NativeToolPanelStateBinding binding,
        UIListTransform list, NativePanelScrollOwnerReceipt owner)
    {
        float maximum = MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight());
        float requested = Math.Clamp(list.ContentScrollOffset, 0, maximum);
        list.SetContentScrollOffsetGuarded(requested,
            (Panel: this, Intent: intent, Binding: binding, List: list,
                Owner: owner),
            static state =>
            {
                state.Panel.RequireStateIntent(state.Intent);
                state.Binding.RequireOwnedState(state.Intent);
                if (!ReferenceEquals(state.Panel._list, state.List) ||
                    !state.Owner.IsCurrent(state.List, state.Panel.Transform))
                    throw new InvalidOperationException("The panel scroll owner changed.");
            });
        if (!owner.IsCurrent(list, Transform))
            throw new InvalidOperationException("The panel Rows list moved during clamp.");
        if (list.ContentScrollOffset != requested)
            throw new InvalidOperationException("The panel scroll clamp was rejected.");
        ulong scrollIntent = list.ContentScrollIntentRevision;
        binding.RequireOwnedState(intent);
        list.SetVirtualBoundsGuarded(ViewportHeight(), 0,
            (Panel: this, Intent: intent, Binding: binding, List: list,
                Owner: owner, ScrollIntent: scrollIntent),
            static state =>
            {
                state.Panel.RequireStateIntent(state.Intent);
                state.Binding.RequireOwnedState(state.Intent);
                if (!ReferenceEquals(state.Panel._list, state.List) ||
                    !state.Owner.IsCurrent(state.List, state.Panel.Transform) ||
                    state.List.ContentScrollIntentRevision != state.ScrollIntent)
                    throw new InvalidOperationException("The panel bounds owner changed.");
            });
        binding.RequireOwnedState(intent);
        if (!owner.IsCurrent(list, Transform) ||
            list.ContentScrollIntentRevision != scrollIntent ||
            !ReferenceEquals(_list, list))
            throw new InvalidOperationException("The panel scroll changed during restore.");
    }
    public event Action<float>? ScrollOffsetChanged;

    protected NativeVirtualTextPanel() => _rows.Activated = OnRowActivated;
    protected void ReleaseVirtualTextOwnerCallbacks()
    {
        _rows.Activated = null;
        _rows.BeginDrag = null;
    }
    protected void EnableRowDrag(Func<ulong, UIInteractableComponent, Vector2, bool> beginDrag)
        => _rows.BeginDrag = beginDrag;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_terminal || !IsActiveInHierarchy)
            return;
        _ownerActive = true;
        ulong revision = ++_ownerRevision;
        if (_list is not null)
        {
            UIListTransform current = _list;
            ObjectConstructionIdentity currentIdentity = ObjectConstructionIdentity.Capture(current);
            current.VirtualProvider = RowProvider;
            if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
                _ownerRevision != revision || !currentIdentity.IsCurrent ||
                !ReferenceEquals(_list, current))
                return;
            current.ContentScrollOffset = _storedScrollOffset;
            if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
                _ownerRevision != revision || !currentIdentity.IsCurrent ||
                !ReferenceEquals(_list, current))
                return;
            RefreshRows();
            return;
        }
        SceneNode root = SceneNode.NewChildWithTransform(out UIDualSplitTransform split, "PanelContent");
        split.VerticalSplit = true;
        split.FirstFixedSize = true;
        split.FixedSize = 30;
        split.SplitterSize = 0;
        Commands = root.NewChildWithTransform(out UIListTransform commands, "Commands");
        commands.DisplayHorizontal = true;
        commands.ItemAlignment = EListAlignment.TopOrLeft;
        commands.ItemSize = 88;
        commands.ItemSpacing = 2;
        CreateCommands(Commands);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _ownerRevision != revision)
            return;
        SceneNode content = root;
        if (FooterHeight > 0)
        {
            content = root.NewChildWithTransform(out UIDualSplitTransform contentSplit, "ContentAndFooter");
            contentSplit.VerticalSplit = true;
            contentSplit.FirstFixedSize = false;
            contentSplit.FixedSize = FooterHeight;
            contentSplit.SplitterSize = 0;
        }
        SceneNode rowsNode = content.NewChildWithTransform(out UIListTransform list, "Rows");
        rowsNode.AddComponent<UICompositionLayerComponent>()!.Kind = EUICompositionLayerKind.ScrollingContent;
        list.DisplayHorizontal = false;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.ItemSize = 24;
        list.ItemSpacing = 0;
        _list = list;
        ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
        list.VirtualProvider = RowProvider;
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _ownerRevision != revision ||
            !listIdentity.IsCurrent || !ReferenceEquals(_list, list))
            return;
        list.ContentScrollOffset = _storedScrollOffset;
        if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
            _ownerRevision != revision || !listIdentity.IsCurrent ||
            !ReferenceEquals(_list, list))
            return;
        if (FooterHeight > 0)
            CreateFooter(content);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _ownerRevision != revision)
            return;
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        UIListTransform? list = _list;
        ObjectConstructionIdentity? identity = list is null ? null :
            ObjectConstructionIdentity.Capture(list);
        _ownerActive = false;
        _ownerRevision++;
        try
        {
            if (list is not null)
            {
                list.VirtualProvider = null;
                if (!_ownerActive && identity?.IsCurrent == true &&
                    ReferenceEquals(_list, list))
                {
                    float previous = _storedScrollOffset;
                    _storedScrollOffset = list.ContentScrollOffset;
                    if (previous != _storedScrollOffset)
                        PublishStateWrite();
                }
            }
        }
        finally { base.OnComponentDeactivated(); }
    }

    protected override void OnDestroying()
    {
        _terminal = true;
        _ownerActive = false;
        _ownerRevision++;
        ScrollOffsetChanged = null;
        try { ReleaseVirtualTextOwnerCallbacks(); }
        finally
        {
            try
            {
                if (_list is not null)
                    _list.DetachVirtualProviderForOwnerTeardown();
            }
            finally { base.OnDestroying(); }
        }
    }

    protected virtual void CreateCommands(SceneNode commands) { }
    protected virtual float FooterHeight => 0;
    protected virtual void CreateFooter(SceneNode parent) { }
    protected virtual void OnRowActivated(ulong id) { }
    protected abstract void RefreshRows();

    protected void SetRows(IEnumerable<(ulong Id, string Text)> rows)
    {
        if (_terminal) return;
        int previousCount = _rows.Count;
        _rows.Replace(rows);
        SignalRowsChanged(previousCount);
    }

    protected void SignalRowsChanged(int previousCount)
    {
        if (_terminal) return;
        if (_ownerActive && IsActiveInHierarchy && _list is not null)
        {
            if (RowProvider.Count != previousCount)
                _list.InvalidateMeasure();
            else
                _list.InvalidateArrange();
        }
        UpdateScroll();
    }

    protected void AddCommand(string title, Action action)
    {
        if (Commands is not null)
            NativeUIElements.CreateButton(Commands, title,
                _ => { if (_ownerActive && !_terminal && IsActiveInHierarchy) action(); }, width: 88, height: 28);
    }

    public bool HandleMouseScroll(float diff)
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _list is null)
            return true;
        UIListTransform list = _list;
        float maximum = MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight());
        ScrollOffset = Math.Clamp(list.ContentScrollOffset - diff * 48.0f, 0, maximum);
        if (!ReferenceEquals(_list, list) || _terminal || !_ownerActive)
            return true;
        ScrollOffsetChanged?.Invoke(_storedScrollOffset);
        if (_ownerActive && !_terminal && IsActiveInHierarchy && ReferenceEquals(_list, list))
            list.SetVirtualBounds(ViewportHeight(), 0);
        return true;
    }

    private void UpdateScroll()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _list is null)
            return;
        UIListTransform list = _list;
        ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(list);
        if (_activeRestore is { RestoreIntent: not 0 } binding &&
            IsCurrentStateIntent(binding.RestoreIntent))
        {
            NativePanelScrollOwnerReceipt owner =
                NativePanelScrollOwnerReceipt.Capture(list, Transform) ??
                throw new InvalidOperationException("The panel Rows list lost its owner.");
            UpdateScrollOwned(binding.RestoreIntent, binding, list, owner);
            return;
        }
        float before = list.ContentScrollOffset;
        list.ContentScrollOffset = Math.Clamp(before, 0, MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight()));
        if (!identity.IsCurrent || !ReferenceEquals(_list, list) || _terminal || !_ownerActive)
            return;
        if (before != list.ContentScrollOffset)
        {
            _storedScrollOffset = list.ContentScrollOffset;
            PublishStateWrite();
        }
        list.SetVirtualBounds(ViewportHeight(), 0);
    }

    private float ViewportHeight() => MathF.Max(24, BoundableTransform.ActualHeight - 30 - FooterHeight);
}
