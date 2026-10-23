using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

/// <summary>Exposes the normal text refresh path for material ownership tests.</summary>
public sealed class UITextMaterialRefreshProbe : UITextComponent
{
    public void RefreshMaterial() => UpdateText(false, false);
    protected override bool CanCapturePublicationUniforms => true;
    public UIDrawPayloadBuilder CreateBuilder() => new(OnMaterialSettingUniforms);
    public bool Capture(UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
    {
        UILayoutNodeOutput output = default;
        return TryCapturePublicationPayload(in output, builder, out payload, out failure);
    }
}
