namespace XREngine.Rendering.API.Rendering.OpenXR;

[Flags]
public enum EOpenXrSmokeTemporalResetReason
{
    None = 0,
    ProfileChanged = 1 << 0,
    CameraCut = 1 << 1,
    MissingCamera = 1 << 2,
    MissingSnapshot = 1 << 3,
    ExplicitReset = 1 << 4,
}

/// <summary>
/// Allocation-free render-path evidence for one eye's temporal state at the
/// point where the frame's color/depth/TSR histories are committed.
/// </summary>
public readonly record struct OpenXrSmokeTemporalStateLedgerEntry(
    ulong RenderFrameId,
    int PipelineInstanceId,
    int EyeIndex,
    int HistoryIsolationPolicy,
    ulong ProfileGeneration,
    uint ExpectedLayerMask,
    uint CurrentMatrixLayerMask,
    uint ColorHistoryLayerMask,
    uint DepthHistoryLayerMask,
    uint TsrHistoryLayerMask,
    uint CommittedLayerMask,
    bool HistoryReadyBeforeCommit,
    bool HistoryReadyAfterCommit,
    bool EyeHistoryReadyBeforeCommit,
    bool EyeHistoryReadyAfterCommit,
    ulong ResetGeneration,
    ulong SeededGeneration,
    ulong PreviousViewProjectionFingerprint,
    ulong CurrentViewProjectionFingerprint,
    float PreviousJitterX,
    float PreviousJitterY,
    float CurrentJitterX,
    float CurrentJitterY,
    EOpenXrSmokeTemporalResetReason ResetReason,
    bool CameraCut,
    bool CommittedThisFrame);

/// <summary>
/// Validation-only bounded history. Recording is a struct copy into a fixed
/// ring; allocations occur only when the smoke summary is captured.
/// </summary>
public static class Phase524bTemporalStateDiagnostics
{
    private const int Capacity = 4096;
    private static readonly object s_lock = new();
    private static readonly OpenXrSmokeTemporalStateLedgerEntry[] s_entries = new OpenXrSmokeTemporalStateLedgerEntry[Capacity];
    private static readonly bool s_enabled = ReadEnabled();
    private static int s_next;
    private static int s_count;
    private static long s_overflowCount;

    public static bool Enabled => s_enabled;
    public static long OverflowCount => Interlocked.Read(ref s_overflowCount);

    public static void Record(in OpenXrSmokeTemporalStateLedgerEntry entry)
    {
        if (!s_enabled)
            return;

        lock (s_lock)
        {
            s_entries[s_next] = entry;
            s_next = (s_next + 1) % Capacity;
            if (s_count < Capacity)
                s_count++;
            else
                Interlocked.Increment(ref s_overflowCount);
        }
    }

    public static OpenXrSmokeTemporalStateLedgerEntry[] Capture()
    {
        lock (s_lock)
        {
            var result = new OpenXrSmokeTemporalStateLedgerEntry[s_count];
            int first = (s_next - s_count + Capacity) % Capacity;
            for (int i = 0; i < s_count; i++)
                result[i] = s_entries[(first + i) % Capacity];
            return result;
        }
    }

    public static void Reset()
    {
        lock (s_lock)
        {
            s_next = 0;
            s_count = 0;
            Interlocked.Exchange(ref s_overflowCount, 0L);
        }
    }

    private static bool ReadEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bValidation);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
