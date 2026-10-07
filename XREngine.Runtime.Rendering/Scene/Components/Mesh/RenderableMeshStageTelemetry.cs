using System.Diagnostics;
using System.Threading;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Opt-in timing counters for per-frame <see cref="RenderableMesh"/> stages. They
/// record only when world tick telemetry is enabled, and they allocate nothing on
/// the measured path. <see cref="Snapshot"/> returns cumulative ticks and calls.
/// </summary>
public static class RenderableMeshStageTelemetry
{
    public const int StageCount = (int)RenderableMeshStage.Count;
    private static readonly string[] s_stageNames = Enum.GetNames<RenderableMeshStage>();
    private static readonly long[] s_ticks = new long[StageCount];
    private static readonly long[] s_calls = new long[StageCount];

    public static bool Enabled => XREngine.RuntimeWorldTickTelemetry.Enabled;

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void End(RenderableMeshStage stage, long start)
    {
        if (start == 0L)
            return;
        Interlocked.Add(ref s_ticks[(int)stage], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref s_calls[(int)stage]);
    }

    public static string Snapshot()
    {
        var parts = new string[StageCount];
        for (int i = 0; i < StageCount; i++)
            parts[i] = $"{s_stageNames[i]}:{Interlocked.Read(ref s_ticks[i])}/{Interlocked.Read(ref s_calls[i])}";
        return string.Join(";", parts);
    }
}
