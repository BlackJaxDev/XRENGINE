using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

[RequiresTransform(typeof(UIBoundableTransform))]
public partial class EditorPanel : XRComponent
{
    public UIBoundableTransform BoundableTransform => SceneNode.GetTransformAs<UIBoundableTransform>()!;

    public EditorPanel() { }

    /// <summary>
    /// The window that this panel is displayed in.
    /// </summary>
    public XRWindow? Window { get; set; }

    /// <summary>Applies the shared solid panel style without an implicit scene capture.</summary>
    public static void ApplyBackground(UIMaterialComponent target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetBatchSolidColor(new ColorF4(166 / 255.0f, 179 / 255.0f, 178 / 255.0f, 1.0f));
    }
}
