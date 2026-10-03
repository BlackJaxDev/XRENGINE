using System.Diagnostics;
using System.Text.Json;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Opt-in bounded span capture for a selected subset of Vulkan CPU stages. The normal aggregate
/// stage counter remains the zero-configuration path; detailed buffers must be warmed before a
/// measured interval, so an unprepared worker never allocates while capture is armed.
/// </summary>
public static partial class VulkanCpuSpanProfiler
{
    private const int MaxCaptureThreads = 64;
    private static readonly object s_configurationGate = new();
    private static readonly ThreadBuffer?[] s_buffers = new ThreadBuffer[MaxCaptureThreads];
    private static int s_bufferCount;
    private static bool[] s_targets = new bool[(int)EVulkanCpuStage.Count];
    private static int s_enabled;
    private static int s_capacity;
    private static int s_emitMarkers;
    private static long s_nextSpanId;
    private static long s_unwarmedSpans;
    private static long s_invalidNesting;

    /// <summary>Configures selected stages before any capture thread is warmed.</summary>
    public static void Configure(string[] stages, int capacityPerThread, bool emitMarkers = false)
    {
        ArgumentNullException.ThrowIfNull(stages);
        EVulkanCpuStage[] parsed = new EVulkanCpuStage[stages.Length];
        for (int index = 0; index < stages.Length; index++)
        {
            if (!Enum.TryParse(stages[index], true, out parsed[index]) ||
                !Enum.IsDefined(parsed[index]) || parsed[index] == EVulkanCpuStage.Count)
                throw new ArgumentOutOfRangeException(nameof(stages), $"Unknown Vulkan CPU stage '{stages[index]}'.");
        }
        Configure(parsed, capacityPerThread, emitMarkers);
    }

    public static void Configure(EVulkanCpuStage[] stages, int capacityPerThread, bool emitMarkers = false)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (capacityPerThread <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPerThread));

        bool[] targets = new bool[(int)EVulkanCpuStage.Count];
        foreach (EVulkanCpuStage stage in stages)
        {
            if ((uint)stage >= (uint)targets.Length)
                throw new ArgumentOutOfRangeException(nameof(stages), $"Unknown Vulkan CPU stage '{stage}'.");
            targets[(int)stage] = true;
        }

        Volatile.Write(ref s_enabled, 0);
        lock (s_configurationGate)
        {
            s_targets = targets;
            s_capacity = capacityPerThread;
            s_emitMarkers = emitMarkers ? 1 : 0;
            s_nextSpanId = 0;
            s_unwarmedSpans = 0;
            s_invalidNesting = 0;
            Array.Clear(s_buffers, 0, s_bufferCount);
            s_bufferCount = 0;
        }
    }

    /// <summary>Allocates this thread's fixed buffer before capture begins.</summary>
    public static void WarmCurrentThread(int workerId = -1)
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (FindThreadBuffer(threadId) is not null)
            return;

        int capacity = Volatile.Read(ref s_capacity);
        if (capacity <= 0)
            throw new InvalidOperationException("Configure targeted Vulkan CPU spans before warming worker threads.");
        lock (s_configurationGate)
        {
            if (FindThreadBuffer(threadId) is not null)
                return;
            if (s_bufferCount >= s_buffers.Length)
                throw new InvalidOperationException($"Vulkan CPU span capture supports at most {MaxCaptureThreads} warmed threads.");

            ThreadBuffer buffer = new(capacity, threadId, workerId);
            Volatile.Write(ref s_buffers[s_bufferCount], buffer);
            s_bufferCount++;
        }
    }

    public static void Arm()
    {
        if (!s_targets.Contains(true))
            throw new InvalidOperationException("Configure at least one targeted Vulkan CPU stage before arming capture.");
        if (Volatile.Read(ref s_bufferCount) == 0)
            throw new InvalidOperationException("Warm at least one capture thread before arming Vulkan CPU spans.");
        Volatile.Write(ref s_enabled, 1);
    }

    public static void Disarm() => Volatile.Write(ref s_enabled, 0);

    /// <summary>
    /// Reports whether a specific fine-grained stage is currently armed. The
    /// hot-path scope uses this to stay dormant in ordinary editor frames while
    /// preserving targeted capture on demand.
    /// </summary>
    internal static bool IsStageCaptureEnabled(EVulkanCpuStage stage)
        => Volatile.Read(ref s_enabled) != 0 &&
           (uint)stage < (uint)s_targets.Length && s_targets[(int)stage];

    /// <summary>Associates later scopes on this thread with the current measured frame.</summary>
    public static void SetFrameContext(long frameId)
    {
        if (Volatile.Read(ref s_enabled) == 0)
            return;
        ThreadBuffer? buffer = FindThreadBuffer(Environment.CurrentManagedThreadId);
        if (buffer is null)
        {
            Interlocked.Increment(ref s_unwarmedSpans);
            return;
        }
        if (buffer.FrameId != frameId)
        {
            buffer.FrameId = frameId;
            buffer.InvocationOrdinal = 0;
        }
    }

    internal static VulkanCpuSpanToken Begin(EVulkanCpuStage stage, long startTimestamp, long startAllocatedBytes)
    {
        if (!IsStageCaptureEnabled(stage))
            return default;

        ThreadBuffer? buffer = FindThreadBuffer(Environment.CurrentManagedThreadId);
        if (buffer is null)
        {
            Interlocked.Increment(ref s_unwarmedSpans);
            return default;
        }

        // Zero is the root sentinel in exported records, so real spans begin at one.
        long id = Interlocked.Increment(ref s_nextSpanId);
        VulkanCpuSpanToken token = new(buffer, stage, id, buffer.ActiveSpanId, startTimestamp, startAllocatedBytes,
            buffer.FrameId, ++buffer.InvocationOrdinal, buffer.WorkerId);
        buffer.ActiveSpanId = id;
        return token;
    }

    internal static void End(in VulkanCpuSpanToken token, long endTimestamp, long endAllocatedBytes)
    {
        if (token.Buffer is null)
            return;

        if (token.Buffer.ActiveSpanId != token.Id)
            Interlocked.Increment(ref s_invalidNesting);
        token.Buffer.ActiveSpanId = token.ParentSpanId;
        token.Buffer.Write(new(
            token.Stage,
            token.Id,
            token.ParentSpanId,
            token.StartTimestamp,
            endTimestamp,
            Math.Max(0, endAllocatedBytes - token.StartAllocatedBytes),
            token.Buffer.ThreadId,
            token.FrameId,
            token.WorkerId,
            token.InvocationOrdinal,
            token.Stage == EVulkanCpuStage.WorkerWait ? "WorkerWait" :
            token.Stage == EVulkanCpuStage.AuxiliaryFenceWait ? "AuxiliaryFenceWait" : null));
        if (Volatile.Read(ref s_emitMarkers) != 0 && VulkanCpuSpanEventSource.Log.IsEnabled())
            VulkanCpuSpanEventSource.Log.Span(token.Id, token.FrameId, (int)token.Stage,
                token.Buffer.ThreadId, token.WorkerId, token.StartTimestamp, endTimestamp);
    }

    /// <summary>Copies retained records after capture; never call from a measured frame.</summary>
    public static VulkanCpuSpanRecord[] GetSnapshot()
    {
        if (Volatile.Read(ref s_enabled) != 0)
            throw new InvalidOperationException("Disarm CPU span capture before copying records.");
        List<VulkanCpuSpanRecord> records = [];
        int count = Volatile.Read(ref s_bufferCount);
        for (int index = 0; index < count; index++)
            Volatile.Read(ref s_buffers[index])?.CopyTo(records);
        return [.. records];
    }

    /// <summary>Returns evidence of incomplete or invalid capture after disarming.</summary>
    public static VulkanCpuCaptureDiagnostics GetDiagnostics()
    {
        long overwritten = 0;
        for (int index = 0; index < Volatile.Read(ref s_bufferCount); index++)
            overwritten += Volatile.Read(ref s_buffers[index])?.OverwrittenCount ?? 0;
        return new(overwritten, Volatile.Read(ref s_unwarmedSpans), Volatile.Read(ref s_invalidNesting));
    }

    /// <summary>Analyzes retained records after capture; incomplete captures remain explicit.</summary>
    public static VulkanCpuSpanAnalysis Analyze()
    {
        if (Volatile.Read(ref s_enabled) != 0)
            throw new InvalidOperationException("Disarm CPU span capture before analyzing records.");
        return VulkanCpuSpanAnalysis.Create(GetSnapshot(), GetDiagnostics());
    }

    /// <summary>Writes a Chrome-compatible CPU trace after capture has stopped.</summary>
    public static void WriteChromeTrace(string path)
    {
        if (Volatile.Read(ref s_enabled) != 0)
            throw new InvalidOperationException("Disarm CPU span capture before exporting a trace.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        VulkanCpuSpanRecord[] records = GetSnapshot();
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("traceEvents");
        writer.WriteStartArray();
        foreach (VulkanCpuSpanRecord record in records)
        {
            writer.WriteStartObject();
            writer.WriteString("name", record.Stage.ToString());
            writer.WriteString("ph", "X");
            writer.WriteNumber("pid", Environment.ProcessId);
            writer.WriteNumber("tid", record.ThreadId);
            writer.WriteNumber("ts", record.StartTimestamp * 1_000_000.0 / Stopwatch.Frequency);
            writer.WriteNumber("dur", Math.Max(0, record.EndTimestamp - record.StartTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writer.WriteNumber("frameId", record.FrameId);
            writer.WriteNumber("spanId", record.SpanId);
            writer.WriteNumber("parentSpanId", record.ParentSpanId);
            writer.WriteNumber("workerId", record.WorkerId);
            writer.WriteNumber("ordinal", record.InvocationOrdinal);
            writer.WriteNumber("allocatedBytes", record.AllocatedBytes);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ThreadBuffer? FindThreadBuffer(int threadId)
    {
        int count = Volatile.Read(ref s_bufferCount);
        for (int index = 0; index < count; index++)
        {
            ThreadBuffer? buffer = Volatile.Read(ref s_buffers[index]);
            if (buffer?.ThreadId == threadId)
                return buffer;
        }

        return null;
    }

}
