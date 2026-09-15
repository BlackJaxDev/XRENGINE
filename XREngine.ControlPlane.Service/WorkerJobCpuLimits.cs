using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the CPU resource limits for a worker job, including flags and the CPU rate.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerJobCpuLimits
{
    public uint Flags;
    public uint CpuRate;
}
