namespace XREngine.Data.Profiling;

/// <summary>Provides optional host hardware inventory without operating-system dependencies.</summary>
public interface IHardwareInventory
{
    bool TryGetActiveGpuCount(out int count, out string? diagnostic);
}
