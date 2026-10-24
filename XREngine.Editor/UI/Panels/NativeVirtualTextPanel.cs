using System.Numerics;
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
            _storedScrollOffset = float.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_terminal || !_ownerActive || !IsActiveInHierarchy) return;
            if (_list is not null)
            {
                _list.ContentScrollOffset = _storedScrollOffset;
                UpdateScroll();
            }
        }
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
            current.VirtualProvider = RowProvider;
            if (_terminal || !_ownerActive || !IsActiveInHierarchy ||
                _ownerRevision != revision || !ReferenceEquals(_list, current))
                return;
            current.ContentScrollOffset = _storedScrollOffset;
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
        list.VirtualProvider = RowProvider;
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _ownerRevision != revision ||
            !ReferenceEquals(_list, list))
            return;
        list.ContentScrollOffset = _storedScrollOffset;
        if (FooterHeight > 0)
            CreateFooter(content);
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _ownerRevision != revision)
            return;
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        if (_ownerActive && _list is not null)
            _storedScrollOffset = _list.ContentScrollOffset;
        _ownerActive = false;
        _ownerRevision++;
        try
        {
            if (_list is not null)
                _list.VirtualProvider = null;
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
        list.ContentScrollOffset = Math.Clamp(list.ContentScrollOffset - diff * 48.0f, 0, maximum);
        _storedScrollOffset = list.ContentScrollOffset;
        ScrollOffsetChanged?.Invoke(_storedScrollOffset);
        if (_ownerActive && !_terminal && IsActiveInHierarchy && ReferenceEquals(_list, list))
            list.SetVirtualBounds(ViewportHeight(), 0);
        return true;
    }

    private void UpdateScroll()
    {
        if (_terminal || !_ownerActive || !IsActiveInHierarchy || _list is null)
            return;
        _list.ContentScrollOffset = Math.Clamp(_list.ContentScrollOffset, 0, MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight()));
        _list.SetVirtualBounds(ViewportHeight(), 0);
    }

    private float ViewportHeight() => MathF.Max(24, BoundableTransform.ActualHeight - 30 - FooterHeight);
}
