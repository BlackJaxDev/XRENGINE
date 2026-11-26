using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

/// <summary>Creates one reusable cell and owns its bind and unbind lifetime.</summary>
public sealed class UIVirtualTableColumn<T>
{
    public UIVirtualTableColumn(string key, string header, float width,
        Func<SceneNode, UIBoundableTransform> createCell,
        Action<UIBoundableTransform, T, ulong> bindCell,
        Action<UIBoundableTransform> unbindCell,
        Action<UIBoundableTransform, T, ulong>? refreshCell = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A table column needs a stable key.", nameof(key));
        if (!float.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        Key = key;
        Header = header ?? string.Empty;
        Width = width;
        CreateCell = createCell ?? throw new ArgumentNullException(nameof(createCell));
        BindCell = bindCell ?? throw new ArgumentNullException(nameof(bindCell));
        UnbindCell = unbindCell ?? throw new ArgumentNullException(nameof(unbindCell));
        RefreshCell = refreshCell;
    }

    public string Key { get; }
    public string Header { get; }
    public float Width { get; }
    public Func<SceneNode, UIBoundableTransform> CreateCell { get; }
    public Action<UIBoundableTransform, T, ulong> BindCell { get; }
    public Action<UIBoundableTransform> UnbindCell { get; }
    /// <summary>Updates a cell only while its item and row lease remain unchanged.</summary>
    public Action<UIBoundableTransform, T, ulong>? RefreshCell { get; }

    /// <summary>
    /// Creates a column of single-line text cells.
    /// </summary>
    /// <param name="leadingInset">
    /// Space in pixels at the left of the text. When it is above zero, the cell is a container and the text
    /// is its child, so the caller can put a small icon in the inset. The text clips to its own bounds, and a
    /// clip also applies to all children, so the icon must not be a child of the text.
    /// </param>
    public static UIVirtualTableColumn<T> Text(string key, string header, float width,
        Func<T, string> getText, float leadingInset = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(getText);
        return new(key, header, width,
            parent =>
            {
                SceneNode cell = new(parent) { Name = key };
                UIBoundableTransform bounds = cell.SetTransform<UIBoundableTransform>();
                SceneNode labelNode = cell;
                if (leadingInset > 0.0f)
                {
                    labelNode = new SceneNode(cell) { Name = "label" };
                    UIBoundableTransform labelBounds = labelNode.SetTransform<UIBoundableTransform>();
                    labelBounds.MaxAnchor = Vector2.One;
                }
                UITextComponent label = labelNode.AddComponent<UITextComponent>()!;
                label.FontSize = 12;
                label.Color = ColorF4.White;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.ClipToBounds = true;
                label.BoundableTransform.Margins = leadingInset > 0.0f
                    ? new Vector4(leadingInset, 0, 6, 0)
                    : new Vector4(6, 0, 6, 0);
                return bounds;
            },
            (cell, item, _) => FindTextCellLabel(cell).Text = getText(item),
            cell => FindTextCellLabel(cell).Text = string.Empty,
            (cell, item, _) => FindTextCellLabel(cell).Text = getText(item));
    }

    /// <summary>
    /// Gets the text of a cell that <see cref="Text"/> created: the cell's own text, or the text of its label
    /// child when the cell has a leading inset.
    /// </summary>
    public static UITextComponent FindTextCellLabel(UIBoundableTransform cell)
    {
        SceneNode node = cell.SceneNode!;
        if (node.GetComponent<UITextComponent>() is { } own)
            return own;
        for (int index = 0; index < cell.Children.Count; index++)
            if (cell.Children[index].SceneNode?.GetComponent<UITextComponent>() is { } child)
                return child;
        throw new InvalidOperationException($"The text cell '{node.Name}' has no label.");
    }
}
