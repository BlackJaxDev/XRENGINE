using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the basic resource limits for a worker job, including time, memory, process, and scheduling constraints.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerJobBasicLimits
{
    public long PerProcessUserTime;
    public long PerJobUserTime;
    public uint Flags;
    public nuint MinimumWorkingSet;
    public nuint MaximumWorkingSet;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}
