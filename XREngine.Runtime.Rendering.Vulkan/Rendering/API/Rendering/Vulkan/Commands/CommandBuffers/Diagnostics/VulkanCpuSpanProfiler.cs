using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Opt-in bounded span capture for a selected subset of Vulkan CPU stages. The normal aggregate
/// stage counter remains the zero-configuration path; detailed buffers must be warmed before a
/// measured interval, so an unprepared worker never allocates while capture is armed.
/// </summary>
internal static partial class VulkanCpuSpanProfiler
{
    private const int MaxCaptureThreads = 64;
    private static readonly object s_configurationGate = new();
    private static readonly ThreadBuffer?[] s_buffers = new ThreadBuffer[MaxCaptureThreads];
    private static int s_bufferCount;
    private static long s_targetMask;
    private static int s_enabled;
    private static int s_capacity;

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
        lock (s_configurationGate)
        {
            Array.Clear(s_buffers, 0, s_bufferCount);
            s_bufferCount = 0;
        }
    }

    /// <summary>Allocates this thread's fixed buffer before capture begins.</summary>
    public static void WarmCurrentThread()
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

            ThreadBuffer buffer = new(capacity, threadId);
            Volatile.Write(ref s_buffers[s_bufferCount], buffer);
            s_bufferCount++;
        }
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

        ThreadBuffer? buffer = FindThreadBuffer(Environment.CurrentManagedThreadId);
        if (buffer is null)
            return default;

        // Zero is the root sentinel in exported records, so real spans begin at one.
        long id = ++buffer.NextSpanId;
        VulkanCpuSpanToken token = new(buffer, stage, id, buffer.ActiveSpanId, startTimestamp, startAllocatedBytes);
        buffer.ActiveSpanId = id;
        return token;
    }

    public static void End(in VulkanCpuSpanToken token, long endTimestamp, long endAllocatedBytes)
    {
        if (token.Buffer is null)
            return;

        token.Buffer.ActiveSpanId = token.ParentSpanId;
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
        int count = Volatile.Read(ref s_bufferCount);
        for (int index = 0; index < count; index++)
            Volatile.Read(ref s_buffers[index])?.CopyTo(records);
        return [.. records];
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
