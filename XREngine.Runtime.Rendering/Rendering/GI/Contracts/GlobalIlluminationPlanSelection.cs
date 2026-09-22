using XREngine;

namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Resolves a selected, supported provider without making reusable commands
/// depend on a concrete pipeline type or per-method feature flags.
/// </summary>
public static class GlobalIlluminationPlanSelection
{
    public static bool IsSelectedAndSupported(object? host, EGlobalIlluminationMode mode)
        => host is IGlobalIlluminationPlanHost { GlobalIlluminationPlan: { } plan } &&
            plan.IsSelectedAndSupported(mode);
}
