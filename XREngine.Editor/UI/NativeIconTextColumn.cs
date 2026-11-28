using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>
/// Creates virtual table columns whose cells show an editor icon at the left of a single-line text, such as a
/// folder or file icon before an asset name.
/// </summary>
public static class NativeIconTextColumn
{
    private const float IconSize = 14.0f;
    private const float IconInset = 24.0f;

    /// <summary>
    /// Creates the column. <paramref name="getIcon"/> returns an icon file name from <see cref="SvgEditorIcons"/>,
    /// or null for no icon.
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
                UIMaterialComponent icon = NativeEditorIcons.CreateIcon(cell.SceneNode!, SvgEditorIcons.IconFile,
                    new Vector2(0.0f, 0.5f), new Vector2(IconInset * 0.5f, 0.0f), IconSize);
                icon.SetBatchSolidColor(ColorF4.Transparent);
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

    private static void Apply(UIBoundableTransform cell, string? iconName)
    {
        if (FindIcon(cell) is not { } icon)
            return;
        if (iconName is null || !NativeEditorIcons.SetIcon(icon, iconName))
            icon.SetBatchSolidColor(ColorF4.Transparent);
    }

    private static UIMaterialComponent? FindIcon(UIBoundableTransform cell)
    {
        for (int index = 0; index < cell.Children.Count; index++)
            if (cell.Children[index].SceneNode is { Name: { } name } node && name.StartsWith("Icon ", StringComparison.Ordinal))
                return node.GetComponent<UIMaterialComponent>();
        return null;
    }
}
