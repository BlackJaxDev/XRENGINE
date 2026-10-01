using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Opt-in performance-query replay for the immutable no-op control fixture.
/// Recording and all pass submissions occur after ordinary timestamp capture has drained.
/// </summary>
public static unsafe class RenderBenchPerformanceCounterDiagnostic
{
    private const string ExtensionName = "VK_KHR_performance_query";
    private const string ReplayFixtureName = "noop-control";
    private const uint MaximumSelectedCounters = 32;
    private const uint MaximumEnumeratedCounters = 4096;
    private const uint MaximumPasses = 32;

    /// <summary>Probe and replay selected queue-family counters. Required policy throws on unsupported devices.</summary>
    public static RenderBenchPerformanceCounterSnapshot Capture(
        VulkanExplicitTargetRendererHost host,
        RenderBenchFixtureManifest fixture,
        ReadOnlySpan<uint> selectedCounterIndices,
        bool required,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Kind != RenderBenchFixtureKind.Control ||
            !fixture.Name.Equals(ReplayFixtureName, StringComparison.Ordinal))
            throw new NotSupportedException("Performance counter replay is restricted to the immutable no-op control fixture.");
        if (selectedCounterIndices.IsEmpty || selectedCounterIndices.Length > MaximumSelectedCounters)
            throw new ArgumentOutOfRangeException(nameof(selectedCounterIndices), "Select 1 to 32 counter indices.");
        if (!host.EnabledDeviceExtensions.Contains(ExtensionName, StringComparer.Ordinal) ||
            !host.Api.TryGetDeviceExtension(host.Instance, host.Device, out KhrPerformanceQuery? extension) ||
            extension is null)
            return Unsupported("VK_KHR_performance_query was not enabled on the Vulkan device.");

        PhysicalDevicePerformanceQueryFeaturesKHR features = new()
        {
            SType = StructureType.PhysicalDevicePerformanceQueryFeaturesKhr,
        };
        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features,
        };
        host.Api.GetPhysicalDeviceFeatures2(host.PhysicalDevice, &features2);
        if (!features.PerformanceCounterQueryPools)
            return Unsupported("The device does not support performance counter query pools.");

        cancellationToken.ThrowIfCancellationRequested();
        uint family = host.GraphicsQueueFamilyIndex;
        uint availableCount = 0;
        Check(extension.EnumeratePhysicalDeviceQueueFamilyPerformanceQueryCounters(
            host.PhysicalDevice, family, &availableCount, null, null), "enumerate performance counter count");
        if (availableCount == 0)
            return Unsupported("The graphics queue family exposes no performance counters.");
        if (availableCount > MaximumEnumeratedCounters)
            return Unsupported($"The graphics queue family exposes {availableCount} counters, beyond the bounded probe limit.");
        PerformanceCounterKHR[] counters = new PerformanceCounterKHR[availableCount];
        PerformanceCounterDescriptionKHR[] descriptions = new PerformanceCounterDescriptionKHR[availableCount];
        for (int index = 0; index < counters.Length; index++)
        {
            counters[index].SType = StructureType.PerformanceCounterKhr;
            descriptions[index].SType = StructureType.PerformanceCounterDescriptionKhr;
        }
        fixed (PerformanceCounterKHR* counterPointer = counters)
        fixed (PerformanceCounterDescriptionKHR* descriptionPointer = descriptions)
        {
            Check(extension.EnumeratePhysicalDeviceQueueFamilyPerformanceQueryCounters(
                host.PhysicalDevice, family, &availableCount, counterPointer, descriptionPointer),
                "enumerate performance counters");
        }
        RenderBenchPerformanceCounterMetadata[] available = new RenderBenchPerformanceCounterMetadata[availableCount];
        for (uint index = 0; index < availableCount; index++)
        {
            fixed (byte* uuid = counters[index].Uuid)
            fixed (byte* name = descriptions[index].Name)
            fixed (byte* category = descriptions[index].Category)
            fixed (byte* description = descriptions[index].Description)
            {
                available[index] = new(index, Convert.ToHexString(new ReadOnlySpan<byte>(uuid, 16)),
                    Marshal.PtrToStringUTF8((nint)name) ?? string.Empty,
                    Marshal.PtrToStringUTF8((nint)category) ?? string.Empty,
                    Marshal.PtrToStringUTF8((nint)description) ?? string.Empty,
                    counters[index].Unit.ToString(), counters[index].Scope.ToString(),
                    counters[index].Storage.ToString(), descriptions[index].Flags.ToString());
            }
        }
        HashSet<uint> unique = [];
        uint[] selected = selectedCounterIndices.ToArray();
        foreach (uint index in selected)
        {
            if (index >= availableCount || !unique.Add(index))
                throw new ArgumentOutOfRangeException(nameof(selectedCounterIndices),
                    $"Counter index {index} is unavailable or repeated; queue family exposes {availableCount} counters.");
        }

        fixed (uint* indices = selected)
        {
            QueryPoolPerformanceCreateInfoKHR performanceInfo = new()
            {
                SType = StructureType.QueryPoolPerformanceCreateInfoKhr,
                QueueFamilyIndex = family,
                CounterIndexCount = (uint)selected.Length,
                PCounterIndices = indices,
            };
            uint passCount = 0;
            extension.GetPhysicalDeviceQueueFamilyPerformanceQueryPasses(
                host.PhysicalDevice, &performanceInfo, &passCount);
            if (passCount is 0 or > MaximumPasses)
                return Unsupported($"Performance query needs {passCount} passes, outside the bounded 1..{MaximumPasses} range.");
            return Replay(host, extension, fixture, selected, counters, available, performanceInfo, passCount,
                required, cancellationToken);
        }

        RenderBenchPerformanceCounterSnapshot Unsupported(string reason)
        {
            if (required)
                throw new NotSupportedException(reason);
            return new("Unsupported", reason, host.GraphicsQueueFamilyIndex, 0, fixture.Name,
                ExtensionName, [], []);
        }
    }

    private static RenderBenchPerformanceCounterSnapshot Replay(
        VulkanExplicitTargetRendererHost host,
        KhrPerformanceQuery extension,
        RenderBenchFixtureManifest fixture,
        uint[] selected,
        PerformanceCounterKHR[] counters,
        RenderBenchPerformanceCounterMetadata[] available,
        QueryPoolPerformanceCreateInfoKHR performanceInfo,
        uint passCount,
        bool required,
        CancellationToken cancellationToken)
    {
        Vk api = host.Api;
        Device device = host.Device;
        api.GetDeviceQueue(device, host.GraphicsQueueFamilyIndex, 0, out Queue queue);
        if (queue.Handle == 0)
            throw new InvalidOperationException("The Vulkan host graphics queue is unavailable for counter replay.");
        QueryPool queryPool = default;
        CommandPool commandPool = default;
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
        bool lockHeld = false;
        bool submissionPending = false;
        try
        {
            QueryPoolCreateInfo queryInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                PNext = &performanceInfo,
                QueryType = QueryType.PerformanceQueryKhr,
                QueryCount = 1,
            };
            Check(api.CreateQueryPool(device, &queryInfo, null, &queryPool), "create performance query pool");
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = host.GraphicsQueueFamilyIndex,
            };
            Check(api.CreateCommandPool(device, &poolInfo, null, &commandPool), "create replay command pool");
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 2,
            };
            Check(api.AllocateCommandBuffers(device, &allocateInfo, commandBuffers), "allocate replay command buffers");

            AcquireProfilingLockInfoKHR lockInfo = new()
            {
                SType = StructureType.AcquireProfilingLockInfoKhr,
                Timeout = 5_000_000_000,
            };
            Result lockResult = extension.AcquireProfilingLock(device, &lockInfo);
            if (lockResult != Result.Success)
            {
                if (required) Check(lockResult, "acquire profiling lock");
                return new("Unsupported", $"Could not acquire profiling lock: {lockResult}.",
                    host.GraphicsQueueFamilyIndex, passCount, fixture.Name, ExtensionName, available, []);
            }
            lockHeld = true;
            cancellationToken.ThrowIfCancellationRequested();
            // Reset in a separate submission; hold the lock before recording any query-pool command buffer.
            Begin(api, commandBuffers[0]);
            api.CmdResetQueryPool(commandBuffers[0], queryPool, 0, 1);
            Check(api.EndCommandBuffer(commandBuffers[0]), "end query reset");
            SubmitAndWait(api, device, queue, commandBuffers[0], null,
                ref submissionPending, cancellationToken);

            Begin(api, commandBuffers[1]);
            api.CmdBeginQuery(commandBuffers[1], queryPool, 0, 0);
            // The dedicated no-op control is immutable and records no resource mutation.
            api.CmdEndQuery(commandBuffers[1], queryPool, 0);
            Check(api.EndCommandBuffer(commandBuffers[1]), "end performance query replay");
            for (uint passIndex = 0; passIndex < passCount; passIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PerformanceQuerySubmitInfoKHR passInfo = new()
                {
                    SType = StructureType.PerformanceQuerySubmitInfoKhr,
                    CounterPassIndex = passIndex,
                };
                SubmitAndWait(api, device, queue, commandBuffers[1], &passInfo,
                    ref submissionPending, cancellationToken);
            }

            PerformanceCounterResultKHR[] results = new PerformanceCounterResultKHR[selected.Length];
            fixed (PerformanceCounterResultKHR* resultPointer = results)
            {
                ulong bytes = (ulong)(results.Length * sizeof(PerformanceCounterResultKHR));
                Check(api.GetQueryPoolResults(device, queryPool, 0, 1, (nuint)bytes,
                    resultPointer, bytes, 0), "read performance query results");
            }
            List<RenderBenchPerformanceCounterValue> values = new(selected.Length);
            for (int index = 0; index < selected.Length; index++)
            {
                PerformanceCounterKHR counter = counters[selected[index]];
                PerformanceCounterResultKHR result = results[index];
                RenderBenchPerformanceCounterMetadata metadata = available[selected[index]];
                values.Add(new(selected[index], metadata.Uuid, metadata.Name,
                    counter.Unit.ToString(), counter.Scope.ToString(),
                    counter.Storage.ToString(), FormatResult(counter.Storage.ToString(), result)));
            }
            return new("Captured", null, host.GraphicsQueueFamilyIndex, passCount,
                fixture.Name, ExtensionName, available, values);
        }
        finally
        {
            // A timed-out native submission still owns these handles and the profiling lock.
            // Device teardown must recover them; freeing executable/pending query buffers is invalid.
            if (!submissionPending && commandPool.Handle != 0)
                api.DestroyCommandPool(device, commandPool, null);
            if (!submissionPending && lockHeld)
                extension.ReleaseProfilingLock(device);
            if (!submissionPending && queryPool.Handle != 0)
                api.DestroyQueryPool(device, queryPool, null);
        }
    }

    private static void Begin(Vk api, CommandBuffer buffer)
    {
        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        Check(api.BeginCommandBuffer(buffer, &beginInfo), "begin replay command buffer");
    }

    private static void SubmitAndWait(Vk api, Device device, Queue queue,
        CommandBuffer commandBuffer, void* next, ref bool submissionPending,
        CancellationToken cancellationToken)
    {
        Fence fence = default;
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Check(api.CreateFence(device, &fenceInfo, null, &fence), "create replay completion fence");
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            PNext = next,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        try
        {
            Check(api.QueueSubmit(queue, 1, &submit, fence), "submit replay command buffer");
            submissionPending = true;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            bool cancelled = false;
            while (true)
            {
                Result wait = api.WaitForFences(device, 1, &fence, true, 10_000_000);
                if (wait == Result.Success)
                {
                    submissionPending = false;
                    break;
                }
                if (wait != Result.Timeout)
                    Check(wait, "wait for replay submission");
                cancelled |= cancellationToken.IsCancellationRequested;
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException("Vulkan performance replay submission did not complete within 10 seconds.");
            }
            if (cancelled)
                cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (!submissionPending)
                api.DestroyFence(device, fence, null);
        }
    }

    private static string FormatResult(string storage, PerformanceCounterResultKHR value)
        => storage switch
        {
            "Int32" => value.Int32.ToString(CultureInfo.InvariantCulture),
            "Int64" => value.Int64.ToString(CultureInfo.InvariantCulture),
            "Uint32" => value.Uint32.ToString(CultureInfo.InvariantCulture),
            "Uint64" => value.Uint64.ToString(CultureInfo.InvariantCulture),
            "Float32" => value.Float32.ToString("R", CultureInfo.InvariantCulture),
            "Float64" => value.Float64.ToString("R", CultureInfo.InvariantCulture),
            _ => value.Uint64.ToString("X16", CultureInfo.InvariantCulture),
        };

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan {operation} failed: {result}.");
    }
}
