using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

/// <summary>Exposes the normal text refresh path for material ownership tests.</summary>
public sealed class UITextMaterialRefreshProbe : UITextComponent
{
    public void RefreshMaterial() => UpdateText(false, false);
}
