namespace XREngine;

[Flags]
public enum ERenderOutputFallbackPolicy : uint
{
    None = 0,
    AllowCadenceReduction = 1u << 0,
    AllowBudgetDeferral = 1u << 1,
    AllowStaleReuse = 1u << 2,
    AllowCompositionReuse = 1u << 3,
    AllowResolutionReduction = 1u << 4,
    AllowSequentialViews = 1u << 5,
}
