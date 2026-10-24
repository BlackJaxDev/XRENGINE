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
    protected IVirtualListProvider? RowProviderOverride { get; set; }
    private IVirtualListProvider RowProvider => RowProviderOverride ?? _rows;

    protected SceneNode? Commands { get; private set; }
    protected int RowCount => RowProvider.Count;
    public float ScrollOffset
    {
        get => _list?.ContentScrollOffset ?? _storedScrollOffset;
        set
        {
            _storedScrollOffset = float.IsFinite(value) ? Math.Max(0, value) : 0;
            if (_list is not null)
            {
                _list.ContentScrollOffset = _storedScrollOffset;
                UpdateScroll();
            }
        }
    }
    public event Action<float>? ScrollOffsetChanged;

    protected NativeVirtualTextPanel() => _rows.Activated = OnRowActivated;
    protected void EnableRowDrag(Func<ulong, UIInteractableComponent, Vector2, bool> beginDrag)
        => _rows.BeginDrag = beginDrag;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_list is not null)
        {
            _list.VirtualProvider = RowProvider;
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
        list.VirtualProvider = RowProvider;
        _list = list;
        list.ContentScrollOffset = _storedScrollOffset;
        if (FooterHeight > 0)
            CreateFooter(content);
        RefreshRows();
    }

    protected override void OnComponentDeactivated()
    {
        if (_list is not null)
            _list.VirtualProvider = null;
        base.OnComponentDeactivated();
    }

    protected virtual void CreateCommands(SceneNode commands) { }
    protected virtual float FooterHeight => 0;
    protected virtual void CreateFooter(SceneNode parent) { }
    protected virtual void OnRowActivated(ulong id) { }
    protected abstract void RefreshRows();

    protected void SetRows(IEnumerable<(ulong Id, string Text)> rows)
    {
        int previousCount = _rows.Count;
        _rows.Replace(rows);
        SignalRowsChanged(previousCount);
    }

    protected void SignalRowsChanged(int previousCount)
    {
        if (_list is not null)
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
            NativeUIElements.CreateButton(Commands, title, _ => action(), width: 88, height: 28);
    }

    public bool HandleMouseScroll(float diff)
    {
        if (_list is null)
            return true;
        float maximum = MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight());
        _list.ContentScrollOffset = Math.Clamp(_list.ContentScrollOffset - diff * 48.0f, 0, maximum);
        _storedScrollOffset = _list.ContentScrollOffset;
        ScrollOffsetChanged?.Invoke(_storedScrollOffset);
        _list.SetVirtualBounds(ViewportHeight(), 0);
        return true;
    }

    private void UpdateScroll()
    {
        if (_list is null)
            return;
        _list.ContentScrollOffset = Math.Clamp(_list.ContentScrollOffset, 0, MathF.Max(0, RowProvider.Count * 24.0f - ViewportHeight()));
        _list.SetVirtualBounds(ViewportHeight(), 0);
    }

    private float ViewportHeight() => MathF.Max(24, BoundableTransform.ActualHeight - 30 - FooterHeight);
}
