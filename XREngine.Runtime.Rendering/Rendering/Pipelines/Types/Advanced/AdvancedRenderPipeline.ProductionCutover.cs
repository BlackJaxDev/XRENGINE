namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private bool _isDesktopDefault = true;

    /// <summary>
    /// Whether this pipeline is the production desktop rendering default.
    /// </summary>
    public bool IsDesktopDefault
    {
        get => _isDesktopDefault;
        set => SetField(ref _isDesktopDefault, value);
    }

    /// <summary>
    /// Current structured cutover status for this unbound pipeline profile.
    /// </summary>
    public AdvancedProductionCutoverStatus ProductionCutover
        => AdvancedProductionCutoverContract.EvaluateUnboundProfile(this, CapabilityResult);

    /// <summary>
    /// Whether explicit production acceptance evidence exists for this profile.
    /// </summary>
    public bool IsProductionReady => ProductionCutover.IsProductionAccepted;

    /// <summary>
    /// Human-readable production cutover status summary.
    /// </summary>
    public string ProductionCutoverStatus => ProductionCutover.Diagnostic;
}
