using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Generates stable logical IDs for a large list without creating source scene nodes.</summary>
internal sealed class VirtualCollectionFixtureProvider(int count) : IVirtualListProvider
{
    private readonly Dictionary<UIBoundableTransform, (UIButtonComponent Button, UITextComponent Label)> _rows = [];
    private ulong _selectedId;

    public int Count { get; } = count;
    public ulong Revision { get; private set; } = 1;
    public int CreatedRowCount => _rows.Count;
    public ulong SelectedId => _selectedId;

    public ulong GetItemId(int index) => (ulong)index + 1;

    public UIBoundableTransform CreateRow()
    {
        SceneNode node = new("VirtualCollectionRow");
        UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
        transform.MaxAnchor = Vector2.One;
        UIButtonComponent button = node.AddComponent<UIButtonComponent>()!;
        var labelNode = node.NewChild<UITextComponent>(out UITextComponent label);
        UIBoundableTransform labelTransform = labelNode.GetTransformAs<UIBoundableTransform>(true)!;
        labelTransform.MaxAnchor = Vector2.One;
        labelTransform.Margins = new Vector4(8, 2, 8, 2);
        label.FontSize = 14;
        label.Color = ColorF4.White;
        label.VerticalAlignment = EVerticalAlignment.Center;
        button.InteractAction += _ =>
        {
            if (button.UserData is ulong id)
            {
                _selectedId = id;
                Revision++;
            }
        };
        _rows.Add(transform, (button, label));
        return transform;
    }

    public void BindRow(UIBoundableTransform row, int index, ulong itemId, ulong incarnation)
    {
        var components = _rows[row];
        components.Button.UserData = itemId;
        components.Button.DefaultBackgroundColor = itemId == _selectedId
            ? new ColorF4(0.22f, 0.50f, 0.85f, 0.45f)
            : ColorF4.Transparent;
        components.Label.Text = $"Item {itemId:N0}";
    }

    public void UnbindRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
        => _rows[row].Button.UserData = null;
}
