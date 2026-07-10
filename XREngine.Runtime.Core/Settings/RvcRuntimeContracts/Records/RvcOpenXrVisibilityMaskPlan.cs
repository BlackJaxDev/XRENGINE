namespace XREngine;

public readonly record struct RvcOpenXrVisibilityMaskPlan(
    bool Requested,
    bool ExtensionEnabled,
    bool UseStencilPrepass,
    bool InvalidateOnRuntimeEvent,
    ERvcOpenXrVisibilityMaskStatus Status,
    ERvcFallbackReason FallbackReason,
    string Diagnostic)
{
    public bool CanStencil => Requested && ExtensionEnabled && UseStencilPrepass && FallbackReason == ERvcFallbackReason.None;

    public static RvcOpenXrVisibilityMaskPlan Resolve(bool requested, bool extensionEnabled)
    {
        if (!requested)
        {
            return new(
                Requested: false,
                ExtensionEnabled: extensionEnabled,
                UseStencilPrepass: false,
                InvalidateOnRuntimeEvent: false,
                ERvcOpenXrVisibilityMaskStatus.NotRequested,
                ERvcFallbackReason.None,
                "OpenXR visibility-mask stencil prepass is not requested.");
        }

        if (!extensionEnabled)
        {
            return new(
                Requested: true,
                ExtensionEnabled: false,
                UseStencilPrepass: false,
                InvalidateOnRuntimeEvent: false,
                ERvcOpenXrVisibilityMaskStatus.ExtensionMissing,
                ERvcFallbackReason.MissingVisibilityMask,
                "XR_KHR_visibility_mask is not enabled; hidden-area stencil prepass is unavailable.");
        }

        return new(
            Requested: true,
            ExtensionEnabled: true,
            UseStencilPrepass: true,
            InvalidateOnRuntimeEvent: true,
            ERvcOpenXrVisibilityMaskStatus.AwaitingRuntimeMesh,
            ERvcFallbackReason.None,
            "XR_KHR_visibility_mask is enabled; hidden-area meshes can feed the RVC stencil prepass.");
    }
}
