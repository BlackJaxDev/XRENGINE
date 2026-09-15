using System.Runtime.InteropServices;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Represents the extended startup information for a worker process, including its standard startup configuration and additional attributes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WorkerProcessStartupExtended
{
    public WorkerProcessStartup Startup;
    public nint Attributes;
}
