using System.Collections.Concurrent;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Opt-in bounded span capture for a selected subset of Vulkan CPU stages. The normal aggregate
/// stage counter remains the zero-configuration path; detailed buffers must be warmed before a
/// measured interval, so an unprepared worker never allocates while capture is armed.
/// </summary>
internal static partial class VulkanCpuSpanProfiler
{
    private static readonly ConcurrentBag<ThreadBuffer> s_buffers = [];
    private static long s_targetMask;
    private static int s_enabled;
    private static int s_capacity;
    [ThreadStatic] private static ThreadBuffer? t_buffer;
    [ThreadStatic] private static long t_activeSpanId;

    public static void Configure(EVulkanCpuStage[] stages, int capacityPerThread)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (capacityPerThread <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPerThread));

        long targetMask = 0;
        foreach (EVulkanCpuStage stage in stages)
        {
            if (stage is EVulkanCpuStage.Count || (int)stage >= sizeof(long) * 8)
                throw new ArgumentOutOfRangeException(nameof(stages), $"Stage '{stage}' cannot be represented by the targeted span mask.");
            targetMask |= 1L << (int)stage;
        }

        Volatile.Write(ref s_enabled, 0);
        Volatile.Write(ref s_targetMask, targetMask);
        Volatile.Write(ref s_capacity, capacityPerThread);
    }

    /// <summary>Allocates this thread's fixed buffer before capture begins.</summary>
    public static void WarmCurrentThread()
    {
        if (t_buffer is not null)
            return;

        int capacity = Volatile.Read(ref s_capacity);
        if (capacity <= 0)
            throw new InvalidOperationException("Configure targeted Vulkan CPU spans before warming worker threads.");
        ThreadBuffer buffer = new(capacity, Environment.CurrentManagedThreadId);
        t_buffer = buffer;
        s_buffers.Add(buffer);
    }

    public static void Arm()
    {
        if (Volatile.Read(ref s_targetMask) == 0)
            throw new InvalidOperationException("Configure at least one targeted Vulkan CPU stage before arming capture.");
        Volatile.Write(ref s_enabled, 1);
    }

    public static void Disarm() => Volatile.Write(ref s_enabled, 0);

    public static VulkanCpuSpanToken Begin(EVulkanCpuStage stage, long startTimestamp, long startAllocatedBytes)
    {
        if (Volatile.Read(ref s_enabled) == 0 || (Volatile.Read(ref s_targetMask) & (1L << (int)stage)) == 0)
            return default;

        ThreadBuffer? buffer = t_buffer;
        if (buffer is null)
            return default;

        // Zero is the root sentinel in exported records, so real spans begin at one.
        long id = ++buffer.NextSpanId;
        VulkanCpuSpanToken token = new(buffer, stage, id, t_activeSpanId, startTimestamp, startAllocatedBytes);
        t_activeSpanId = id;
        return token;
    }

    public static void End(in VulkanCpuSpanToken token, long endTimestamp, long endAllocatedBytes)
    {
        if (token.Buffer is null)
            return;

        t_activeSpanId = token.ParentSpanId;
        token.Buffer.Write(new(
            token.Stage,
            token.Id,
            token.ParentSpanId,
            token.StartTimestamp,
            endTimestamp,
            Math.Max(0, endAllocatedBytes - token.StartAllocatedBytes),
            token.Buffer.ThreadId));
    }

    /// <summary>Copies retained records after capture; never call from a measured frame.</summary>
    public static VulkanCpuSpanRecord[] GetSnapshot()
    {
        List<VulkanCpuSpanRecord> records = [];
        foreach (ThreadBuffer buffer in s_buffers)
            buffer.CopyTo(records);
        return [.. records];
    }

}
