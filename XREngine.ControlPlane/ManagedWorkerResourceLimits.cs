namespace XREngine.ControlPlane;

public sealed class ManagedWorkerResourceLimits
{
    public int CpuPercentLimit { get; set; }
    public long MemoryBytesLimit { get; set; }
    public int MaxOpenHandles { get; set; }
}
