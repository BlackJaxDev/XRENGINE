namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    private AdvancedRenderPipelineOutputBinding _advancedOutputBinding;

    /// <summary>
    /// Output-local binding for the configured advanced pipeline definition.
    /// Pipeline assets may be shared, so backend reservations must remain on
    /// the physical viewport pipeline instance that owns the output identity.
    /// </summary>
    public AdvancedRenderPipelineOutputBinding AdvancedOutputBinding
        => _advancedOutputBinding;

    internal void ApplyAdvancedOutputBinding(
        in AdvancedRenderPipelineOutputBinding binding)
    {
        if (binding.State == EAdvancedRenderPipelineOutputBindingState.Bound &&
            !binding.IsBound)
        {
            throw new ArgumentException(
                "A bound advanced output must carry a valid reservation for the same output identity.",
                nameof(binding));
        }

        SetField(ref _advancedOutputBinding, binding);
    }

    internal void ClearAdvancedOutputBinding()
        => SetField(ref _advancedOutputBinding, default);
}
