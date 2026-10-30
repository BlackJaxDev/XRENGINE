namespace XREngine.Rendering;

/// <summary>Creates a secondary graphics owner on the template window's native thread.</summary>
public interface IRuntimeSecondaryGpuContextFactory
{
    IRuntimeSecondaryGpuContext CreateSecondaryGpuContext(XRWindow templateWindow, bool allowContextSharing);
}
