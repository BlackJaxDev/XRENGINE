using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Immutable output-local realization of a configured
/// <see cref="AdvancedRenderPipeline"/> source asset.
/// </summary>
public readonly record struct AdvancedRenderPipelineOutputBinding(
    RenderPipelineRequest Request,
    AdvancedRenderPipelineCapabilityResult CapabilityResult,
    AdvancedVisibilityFamilyReservation Reservation,
    EAdvancedRenderPipelineOutputBindingState State,
    string? FailureReason,
    AdvancedProductionCutoverStatus CutoverStatus = default)
{
    public bool IsBound
        => State == EAdvancedRenderPipelineOutputBindingState.Bound &&
           Reservation.IsValid &&
           Reservation.OutputId == Request.OutputId;
}
