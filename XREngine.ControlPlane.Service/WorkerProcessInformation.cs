using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the basic information about a worker process, including its process and thread handles and their respective IDs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerProcessInformation
{
    public nint Process, Thread;
    public uint ProcessId, ThreadId;
}
