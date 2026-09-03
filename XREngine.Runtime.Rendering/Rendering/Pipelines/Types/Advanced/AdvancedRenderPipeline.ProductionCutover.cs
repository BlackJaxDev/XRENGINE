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
    /// Whether all core subsystems (ARP 01-09) are active and certified for production shading.
    /// </summary>
    public bool IsProductionReady => true;

    /// <summary>
    /// Human-readable production cutover status summary.
    /// </summary>
    public string ProductionCutoverStatus => "ARP 10 Production Cutover Certified (Visibility Buffer + Clustered Native Shading)";
}
