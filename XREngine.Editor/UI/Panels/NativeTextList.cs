using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Shows a bounded set of visible text rows from a logical list.</summary>
internal sealed class NativeTextList : IVirtualListProvider
{
    private readonly List<(ulong Id, string Text)> _items = [];
    private readonly Dictionary<UIBoundableTransform, UITextComponent> _labels = [];
    private readonly Dictionary<UIBoundableTransform, ulong> _boundIds = [];
    private readonly Dictionary<UIInteractableComponent, Vector2> _dragStarts = [];
    private ulong _revision;

    public Action<ulong>? Activated { get; set; }
    public Func<ulong, UIInteractableComponent, Vector2, bool>? BeginDrag { get; set; }

    public int Count => _items.Count;
    public ulong Revision => _revision;

    public ulong GetItemId(int index) => _items[index].Id;

    public void Replace(IEnumerable<(ulong Id, string Text)> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _revision++;
    }

    public UIBoundableTransform CreateRow()
    {
        SceneNode node = new("NativeTextRow");
        UIBoundableTransform row = node.SetTransform<UIBoundableTransform>();
        row.MaxAnchor = Vector2.One;
        UIButtonComponent button = node.AddComponent<UIButtonComponent>()!;
        button.RegisterClickActions(_ =>
        {
            if (_boundIds.TryGetValue(row, out ulong id))
                Activated?.Invoke(id);
        });
        button.NeedsMouseMove = true;
        button.MouseMove += (x, y, source) =>
        {
            if (!_boundIds.TryGetValue(row, out ulong id) || !EditorDragDropUtility.IsLeftMouseButtonHeldFor(source))
            {
                _dragStarts.Remove(source);
                return;
            }
            Vector2 point = source.BoundableTransform.LocalToCanvas(new Vector2(x, y));
            if (!_dragStarts.TryGetValue(source, out Vector2 start))
            {
                _dragStarts[source] = point;
                return;
            }
            if (Vector2.Distance(point, start) >= EditorDragDropUtility.DefaultActivationDistance &&
                BeginDrag?.Invoke(id, source, point) == true)
                _dragStarts[source] = point;
        };
        button.MouseDirectOverlapLeave += source => _dragStarts.Remove(source);
        node.NewChild<UITextComponent>(out var label);
        label.FontSize = 13;
        label.Color = ColorF4.White;
        label.HorizontalAlignment = EHorizontalAlignment.Left;
        label.VerticalAlignment = EVerticalAlignment.Center;
        label.BoundableTransform.Margins = new Vector4(8, 0, 8, 0);
        label.ClipToBounds = true;
        _labels.Add(row, label);
        return row;
    }

    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
    {
        _boundIds[row] = itemId;
        _labels[row].Text = _items[index].Text;
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        _boundIds.Remove(row);
        if (row.SceneNode?.GetComponent<UIButtonComponent>() is { } button)
            _dragStarts.Remove(button);
        _labels[row].Text = string.Empty;
    }
}
