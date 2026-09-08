namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private AdvancedRenderPipelineOutputBinding _advancedOutputBinding;
    private IRuntimeRendererHost? _advancedOutputOwnerRenderer;

    /// <summary>
    /// Output-local binding for the configured advanced pipeline definition.
    /// Pipeline assets may be shared, so backend reservations must remain on
    /// the physical viewport pipeline instance that owns the output identity.
    /// </summary>
    public AdvancedRenderPipelineOutputBinding AdvancedOutputBinding
        => _advancedOutputBinding;

    internal void ApplyAdvancedOutputBinding(
        in AdvancedRenderPipelineOutputBinding binding,
        IRuntimeRendererHost? ownerRenderer = null)
    {
        if (binding.State == EAdvancedRenderPipelineOutputBindingState.Bound &&
            !binding.IsBound)
        {
            throw new ArgumentException(
                "A bound advanced output must carry a valid reservation for the same output identity.",
                nameof(binding));
        }

        AdvancedVisibilityFamilyReservation previous = _advancedOutputBinding.Reservation;
        IRuntimeRendererHost? previousOwner = _advancedOutputOwnerRenderer;
        IRuntimeRendererHost? nextOwner = binding.IsBound
            ? ownerRenderer ?? AbstractRenderer.Current as IRuntimeRendererHost
            : null;
        bool bindingChanged = SetField(ref _advancedOutputBinding, binding);
        if (binding.IsBound)
        {
            if (!bindingChanged && ReferenceEquals(previousOwner, nextOwner))
                return;
            _advancedOutputOwnerRenderer = nextOwner;
            if (previous != binding.Reservation || !ReferenceEquals(previousOwner, nextOwner))
                ReleaseAdvancedOutputBindingOwner(previousOwner, in previous);
            return;
        }

        // Admission can reserve before a later cutover check rejects the
        // binding. Pending or rejected bindings never become bank owners.
        _advancedOutputOwnerRenderer = null;
        ReleaseAdvancedOutputBindingOwner(previousOwner, in previous);
        if (binding.Reservation != previous || !ReferenceEquals(previousOwner, ownerRenderer))
        {
            AdvancedVisibilityFamilyReservation transient = binding.Reservation;
            ReleaseAdvancedOutputBindingOwner(ownerRenderer, in transient);
        }
    }

    internal void ClearAdvancedOutputBinding()
    {
        AdvancedVisibilityFamilyReservation previous = _advancedOutputBinding.Reservation;
        IRuntimeRendererHost? previousOwner = _advancedOutputOwnerRenderer;
        if (!SetField(ref _advancedOutputBinding, default))
            return;
        _advancedOutputOwnerRenderer = null;
        ReleaseAdvancedOutputBindingOwner(previousOwner, in previous);
    }

    private static void ReleaseAdvancedOutputBindingOwner(
        IRuntimeRendererHost? owner,
        in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid || owner is null)
            return;
        owner.ReleaseAdvancedVisibilityFamilyOwner(in reservation);
    }
}
