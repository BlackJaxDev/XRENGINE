using System.Diagnostics;
using System.Threading;

namespace XREngine.Components.Scene.Mesh;

// TEMPORARY DIAGNOSTIC. Remove before commit.
public static class RenderableMeshStageTelemetry
{
    public const int StageCount = 16;
    private static readonly long[] s_ticks = new long[StageCount];
    private static readonly long[] s_calls = new long[StageCount];

    public static bool Enabled => XREngine.RuntimeWorldTickTelemetry.Enabled;

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void End(int stage, long start)
    {
        if (start == 0L)
            return;
        Interlocked.Add(ref s_ticks[stage], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref s_calls[stage]);
    }

    public static string Snapshot()
    {
        var parts = new string[StageCount];
        for (int i = 0; i < StageCount; i++)
            parts[i] = $"{i}:{Interlocked.Read(ref s_ticks[i])}/{Interlocked.Read(ref s_calls[i])}";
        return string.Join(";", parts);
    }
}
