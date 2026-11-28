using System.Numerics;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// Creates virtual table columns whose cells show a Lucide icon (<see cref="EditorIcons"/>) at the left of a
/// single-line text, such as a folder or file icon before an asset name.
/// </summary>
public static class NativeIconTextColumn
{
    private const float IconSize = 14.0f;
    private const float IconInset = 24.0f;

    /// <summary>
    /// Creates the column. <paramref name="getIcon"/> returns an icon constant of <see cref="EditorIcons"/>, or null
    /// for no icon.
    /// </summary>
    public static UIVirtualTableColumn<T> Create<T>(string key, string header, float width,
        Func<T, string> getText, Func<T, string?> getIcon)
    {
        ArgumentNullException.ThrowIfNull(getText);
        ArgumentNullException.ThrowIfNull(getIcon);
        UIVirtualTableColumn<T> text = UIVirtualTableColumn<T>.Text(key, header, width, getText, IconInset);
        return new(key, header, width,
            parent =>
            {
                UIBoundableTransform cell = text.CreateCell(parent);
                // The icon is a sibling of the clipped label, so the label clip does not cut it.
                EditorIcons.CreateGlyph(cell.SceneNode!, string.Empty, new Vector2(0.0f, 0.5f),
                    new Vector2(IconInset * 0.5f, 0.0f), IconSize, EditorGlassTheme.TextSecondary);
                return cell;
            },
            (cell, item, id) =>
            {
                text.BindCell(cell, item, id);
                Apply(cell, getIcon(item));
            },
            cell =>
            {
                text.UnbindCell(cell);
                Apply(cell, null);
            },
            (cell, item, id) =>
            {
                text.RefreshCell?.Invoke(cell, item, id);
                Apply(cell, getIcon(item));
            });
    }

    private static void Apply(UIBoundableTransform cell, string? glyph)
    {
        string text = glyph ?? string.Empty;
        if (FindIcon(cell) is { } icon && icon.Text != text)
            icon.Text = text;
    }

    private static UITextComponent? FindIcon(UIBoundableTransform cell)
    {
        for (int index = 0; index < cell.Children.Count; index++)
            if (cell.Children[index].SceneNode is { Name: "Icon" } node)
                return node.GetComponent<UITextComponent>();
        return null;
    }
}
