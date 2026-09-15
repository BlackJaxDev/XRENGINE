namespace XREngine.Rendering.API.Rendering.OpenXR;

public enum OpenXrDeviceLossSettlementState
{
    NotStarted,
    Quiescing,
    Settling,
    Settled,
    StickyQuarantined,
}
