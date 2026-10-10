using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Rendering;

/// <summary>Reports whether a light-owned shadow image still contains a committed WebGPU frame's data.</summary>
public interface IBrowserShadowReuseCapability
{
    bool CanReuseCommittedShadow(XRTexture texture);
    bool WasShadowProducedInCurrentFrame(XRTexture texture);
    ulong GetShadowProductionTicket(XRTexture texture);
    void AuthorizeShadowReuse(LightComponent light, XRTexture texture);
}
