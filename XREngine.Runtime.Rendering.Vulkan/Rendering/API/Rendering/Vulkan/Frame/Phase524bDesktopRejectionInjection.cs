namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One-shot state machine used only by the explicit 5.2.4b validation launch.
/// It samples one accepted frame as history, then rejects the next eligible
/// frame so both numeric samples come from completed desktop-owned GPU state.
/// </summary>
internal sealed class Phase524bDesktopRejectionInjection
{
    private bool _armed;
    private bool _completed;
    private double _history;

    internal Phase524bDesktopRejectionDecision Observe(
        bool enabled,
        bool eligible,
        bool sampleSucceeded,
        double exposure,
        string diagnostic)
    {
        if (!enabled || _completed || !eligible)
            return new(EPhase524bDesktopRejectionAction.Wait, 0.0, _history, diagnostic);

        // The HDR validation scene requires positive exposure. Startup clears
        // are valid readbacks but are not completed exposure history and must
        // not arm the rejection sample.
        if (!sampleSucceeded || !double.IsFinite(exposure) || exposure <= double.Epsilon)
            return new(EPhase524bDesktopRejectionAction.Wait, 0.0, _history, diagnostic);

        if (!_armed)
        {
            _history = exposure;
            _armed = true;
            return new(EPhase524bDesktopRejectionAction.Armed, exposure, exposure, diagnostic);
        }

        _completed = true;
        return new(EPhase524bDesktopRejectionAction.Reject, exposure, _history, diagnostic);
    }
}
