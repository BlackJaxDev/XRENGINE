using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the extended resource limits for a worker job, including basic limits and additional metrics for I/O operations and memory usage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerJobExtendedLimits
{
    public WorkerJobBasicLimits Basic;
    public ulong ReadOperations;
    public ulong WriteOperations;
    public ulong OtherOperations;
    public ulong ReadBytes;
    public ulong WriteBytes;
    public ulong OtherBytes;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemory;
    public nuint PeakJobMemory;
}
