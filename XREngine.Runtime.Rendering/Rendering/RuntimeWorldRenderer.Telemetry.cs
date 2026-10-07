using System.Diagnostics;

namespace XREngine.Rendering;

public sealed partial class RuntimeWorldRenderer
{
    private static long s_collectCalls;
    private static long s_collectMatrixTicks;
    private static long s_collectMeshTicks;
    private static long s_collectSceneTicks;
    private static long s_swapCalls;
    private static long s_swapMatrixTicks;
    private static long s_swapMeshTicks;
    private static long s_swapSceneTicks;

    /// <summary>Reads process-wide collection timing when world tick telemetry is enabled.</summary>
    public static RuntimeWorldCollectionTelemetrySnapshot CollectionTelemetry => new(
        RuntimeWorldTickTelemetry.Enabled,
        Stopwatch.Frequency,
        Interlocked.Read(ref s_collectCalls),
        Interlocked.Read(ref s_collectMatrixTicks),
        Interlocked.Read(ref s_collectMeshTicks),
        Interlocked.Read(ref s_collectSceneTicks),
        Interlocked.Read(ref s_swapCalls),
        Interlocked.Read(ref s_swapMatrixTicks),
        Interlocked.Read(ref s_swapMeshTicks),
        Interlocked.Read(ref s_swapSceneTicks));
}
