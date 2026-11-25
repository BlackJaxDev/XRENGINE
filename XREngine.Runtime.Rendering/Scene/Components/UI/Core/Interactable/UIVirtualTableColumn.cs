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

    public static UIVirtualTableColumn<T> Text(string key, string header, float width,
        Func<T, string> getText)
    {
        ArgumentNullException.ThrowIfNull(getText);
        return new(key, header, width,
            parent =>
            {
                SceneNode cell = new(parent) { Name = key };
                UIBoundableTransform bounds = cell.SetTransform<UIBoundableTransform>();
                UITextComponent label = cell.AddComponent<UITextComponent>()!;
                label.FontSize = 12;
                label.Color = ColorF4.White;
                label.VerticalAlignment = EVerticalAlignment.Center;
                label.ClipToBounds = true;
                label.BoundableTransform.Margins = new Vector4(6, 0, 6, 0);
                return bounds;
            },
            (cell, item, _) => cell.SceneNode!.GetComponent<UITextComponent>()!.Text = getText(item),
            cell => cell.SceneNode!.GetComponent<UITextComponent>()!.Text = string.Empty,
            (cell, item, _) => cell.SceneNode!.GetComponent<UITextComponent>()!.Text = getText(item));
    }
}
