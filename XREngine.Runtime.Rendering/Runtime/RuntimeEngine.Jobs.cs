using XREngine.Rendering;

namespace XREngine;

public static partial class RuntimeEngine
{
    /// <summary>
    /// Temporary compatibility facade over the host-installed scheduler's general
    /// domain. It never constructs or owns a second worker pool.
    /// </summary>
    public static JobManager Jobs => RuntimeRenderingHostServices.Work.GeneralJobs;
}
