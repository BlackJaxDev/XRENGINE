using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Owns bounded, queue-local timestamp evidence for explicitly selected fixture passes.</summary>
public sealed unsafe class RenderBenchGpuDiagnostic : IVulkanSelectedGpuPassSink, IDisposable
{
    private readonly VulkanExplicitTargetRendererHost _host;
    private readonly QueryPool[] _pools;
    private readonly Slot[] _slots;
    private readonly string[] _targets;
    private int[]? _productionPassIndices;
    private readonly int _maximumQueries;
    private readonly int _maximumDepth;
    private readonly RenderBenchGpuPassSample[] _samples;
    private readonly uint _validBits;
    private readonly double _periodNanoseconds;
    private readonly ExtCalibratedTimestamps? _calibratedTimestamps;
    private readonly nint _getCalibrateableDomains;
    private readonly bool _calibrationRequested;
    private RenderBenchGpuCalibrationSample? _preCalibration;
    private int _sampleCount;
    private long _skippedScopes;
    private long _selectedScopes;
    private long _overflowScopes;
    private long _abandonedQueries;
    private bool _disposed;

    private sealed class Slot(int capacity)
    {
        public readonly Pending[] Pending = new Pending[capacity / 2];
        public int QueryCount;
        public int PendingCount;
        public ulong SourceFrameId;
    }

    private readonly record struct Pending(string Target, int Pass, uint StartQuery, uint EndQuery);

    public RenderBenchGpuDiagnostic(VulkanExplicitTargetRendererHost host, RenderProfileRecipe recipe)
    {
        _host = host;
        RenderProfileGpuConfiguration config = recipe.GpuProfiling;
        config.Validate();
        _targets = config.Targets;
        _maximumQueries = config.MaximumQueriesPerFrame;
        _maximumDepth = config.MaximumScopeDepth;
        _pools = new QueryPool[checked((int)recipe.FrameSlots)];
        _slots = new Slot[_pools.Length];
        long requestedSamples = ((long)recipe.TotalCaptureFrames + recipe.WarmupFrames + recipe.StabilityFrames + 256) * (_maximumQueries / 2);
        _samples = new RenderBenchGpuPassSample[checked((int)Math.Min(65_536L, Math.Max(1_024L, requestedSamples)))];

        host.Api.GetPhysicalDeviceProperties(host.PhysicalDevice, out PhysicalDeviceProperties properties);
        _periodNanoseconds = properties.Limits.TimestampPeriod;
        _calibrationRequested = config.CalibratedTimestamps;
        if (_calibrationRequested &&
            host.EnabledDeviceExtensions.Contains(ExtCalibratedTimestamps.ExtensionName) &&
            host.Api.TryGetDeviceExtension(host.Instance, host.Device, out ExtCalibratedTimestamps? calibrated))
        {
            _calibratedTimestamps = calibrated;
            _getCalibrateableDomains = (nint)host.Api.GetInstanceProcAddr(
                host.Instance, "vkGetPhysicalDeviceCalibrateableTimeDomainsEXT");
            _preCalibration = TakeCalibrationSample();
        }
        if (config.RequireCalibration && _preCalibration is null)
            throw new NotSupportedException("Vulkan calibrated device/QPC timestamps are unavailable on the selected adapter.");
        uint familyCount = 0;
        host.Api.GetPhysicalDeviceQueueFamilyProperties(host.PhysicalDevice, ref familyCount, null);
        QueueFamilyProperties* families = stackalloc QueueFamilyProperties[checked((int)familyCount)];
        host.Api.GetPhysicalDeviceQueueFamilyProperties(host.PhysicalDevice, ref familyCount, families);
        _validBits = families[host.GraphicsQueueFamilyIndex].TimestampValidBits;
        if (_validBits == 0 || _periodNanoseconds <= 0)
            throw new NotSupportedException("The selected Vulkan graphics queue does not support timestamp queries.");

        try
        {
            for (int slot = 0; slot < _pools.Length; slot++)
            {
                QueryPoolCreateInfo info = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = unchecked((uint)_maximumQueries),
                };
                Result result = host.Api.CreateQueryPool(host.Device, in info, null, out _pools[slot]);
                if (result != Result.Success)
                    throw new InvalidOperationException($"Failed to create a diagnostic timestamp pool: {result}.");
                _slots[slot] = new Slot(_maximumQueries);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Enabled => _targets.Length != 0;
    public bool CalibrationSupported => _preCalibration is not null;
    public uint GraphicsQueueFamilyIndex => _host.GraphicsQueueFamilyIndex;

    /// <summary>Validates production pass names before warmup and fixes their recording indices.</summary>
    public void ConfigureProductionPasses(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        ArgumentNullException.ThrowIfNull(passMetadata);
        int[] indices = new int[_targets.Length];
        for (int targetIndex = 0; targetIndex < _targets.Length; targetIndex++)
        {
            string target = _targets[targetIndex];
            for (int previous = 0; previous < targetIndex; previous++)
                if (string.Equals(target, _targets[previous], StringComparison.Ordinal))
                    throw new ArgumentException($"GPU target '{target}' is selected more than once.");

            bool found = false;
            foreach (RenderPassMetadata pass in passMetadata)
            {
                if (!string.Equals(target, pass.Name, StringComparison.Ordinal))
                    continue;
                if (found)
                    throw new InvalidOperationException($"Production render graph has duplicate pass name '{target}'.");
                indices[targetIndex] = pass.PassIndex;
                found = true;
            }
            if (!found)
                throw new NotSupportedException($"GPU target '{target}' is absent from the production render graph.");
        }
        _productionPassIndices = indices;
    }

    /// <summary>Resolves the previous use of this completed slot, then resets its fixed query range.</summary>
    public void BeginFrame(CommandBuffer commandBuffer, uint frameSlot, ulong sourceFrameId)
    {
        if (!Enabled)
            return;
        Slot slot = _slots[checked((int)frameSlot)];
        if (slot.PendingCount != 0)
            ResolveSlot(checked((int)frameSlot), slot, sourceFrameId);
        slot.QueryCount = 0;
        slot.PendingCount = 0;
        slot.SourceFrameId = sourceFrameId;
        _host.Api.CmdResetQueryPool(commandBuffer, _pools[frameSlot], 0, unchecked((uint)_maximumQueries));
    }

    /// <summary>Starts one selected scope; the caller must invoke <see cref="EndPass"/> for a returned query.</summary>
    public int BeginPass(CommandBuffer commandBuffer, uint frameSlot, string target, int passIndex, int depth = 1)
    {
        if (!Enabled || !IsSelected(target))
            return -1;
        if (depth > _maximumDepth)
        {
            _skippedScopes++;
            return -1;
        }
        Slot slot = _slots[checked((int)frameSlot)];
        if (slot.QueryCount + 2 > _maximumQueries)
        {
            _overflowScopes++;
            return -1;
        }
        uint start = unchecked((uint)slot.QueryCount);
        slot.QueryCount += 2;
        slot.Pending[slot.PendingCount++] = new Pending(target, passIndex, start, start + 1);
        _selectedScopes++;
        _host.WriteDiagnosticTimestamp(commandBuffer, true, _pools[frameSlot], start);
        return checked((int)start);
    }

    public int BeginProductionPass(CommandBuffer commandBuffer, uint frameSlot, int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        int[]? indices = _productionPassIndices;
        if (indices is null)
            return -1;
        for (int index = 0; index < indices.Length; index++)
            if (indices[index] == passIndex)
                return BeginPass(commandBuffer, frameSlot, _targets[index], passIndex);
        return -1;
    }

    public void EndPass(CommandBuffer commandBuffer, uint frameSlot, int startQuery)
    {
        if (startQuery < 0)
            return;
        _host.WriteDiagnosticTimestamp(commandBuffer, false,
            _pools[frameSlot], unchecked((uint)(startQuery + 1)));
    }

    /// <summary>Nonblocking post-submission drain. The caller must establish slot completion first.</summary>
    public void ResolveCompletedFrame(uint frameSlot)
    {
        Slot slot = _slots[checked((int)frameSlot)];
        if (slot.PendingCount != 0)
            ResolveSlot(checked((int)frameSlot), slot, slot.SourceFrameId);
        slot.PendingCount = 0;
        slot.QueryCount = 0;
    }

    private bool IsSelected(string target)
    {
        for (int index = 0; index < _targets.Length; index++)
            if (string.Equals(_targets[index], target, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void ResolveSlot(int slotIndex, Slot slot, ulong observedFrameId)
    {
        ulong* values = stackalloc ulong[4];
        for (int index = 0; index < slot.PendingCount; index++)
        {
            Pending pending = slot.Pending[index];
            Result result = _host.Api.GetQueryPoolResults(
                _host.Device, _pools[slotIndex], pending.StartQuery, 2,
                (nuint)(sizeof(ulong) * 4), values, (ulong)(sizeof(ulong) * 2),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
            if (result == Result.NotReady || (result == Result.Success && (values[1] == 0 || values[3] == 0)))
            {
                _abandonedQueries += 2;
                continue;
            }
            if (result != Result.Success)
                throw new InvalidOperationException($"Diagnostic timestamp query read failed: {result}.");
            if (_sampleCount == _samples.Length)
            {
                _overflowScopes++;
                continue;
            }
            ulong start = Mask(values[0]);
            ulong end = Mask(values[2]);
            ulong delta = _validBits == 64 ? unchecked(end - start) : (end - start) & ((1UL << checked((int)_validBits)) - 1UL);
            _samples[_sampleCount++] = new RenderBenchGpuPassSample(
                pending.Target, pending.Pass, slot.SourceFrameId, GraphicsQueueFamilyIndex,
                start, end, delta * _periodNanoseconds,
                observedFrameId >= slot.SourceFrameId ? observedFrameId - slot.SourceFrameId : 0UL);
        }
    }

    private ulong Mask(ulong ticks)
        => _validBits == 64 ? ticks : ticks & ((1UL << checked((int)_validBits)) - 1UL);

    /// <summary>Copies diagnostic evidence after capture, outside measured frame work.</summary>
    public RenderBenchGpuDiagnosticSnapshot Snapshot()
        => new(
            _validBits, _periodNanoseconds, GraphicsQueueFamilyIndex, _maximumQueries,
            checked((long)_pools.Length * _maximumQueries * sizeof(ulong) * 2),
            _skippedScopes, _overflowScopes, _abandonedQueries,
            _samples.AsSpan(0, _sampleCount).ToArray())
        {
            CalibrationRequested = _calibrationRequested,
            PreCalibration = _preCalibration,
            PostCalibration = TakeCalibrationSample(),
            SelectedScopes = _selectedScopes,
        };

    private RenderBenchGpuCalibrationSample? TakeCalibrationSample()
    {
        if (_calibratedTimestamps is null || _getCalibrateableDomains == 0)
            return null;
        var getDomains = (delegate* unmanaged[Stdcall]<PhysicalDevice, uint*, TimeDomainKHR*, Result>)_getCalibrateableDomains;
        uint domainCount = 0;
        Result domainsResult = getDomains(_host.PhysicalDevice, &domainCount, null);
        if (domainsResult != Result.Success || domainCount == 0)
            return null;
        TimeDomainKHR* domains = stackalloc TimeDomainKHR[checked((int)domainCount)];
        domainsResult = getDomains(_host.PhysicalDevice, &domainCount, domains);
        if (domainsResult != Result.Success)
            return null;
        bool device = false;
        bool qpc = false;
        for (int index = 0; index < domainCount; index++)
        {
            device |= domains[index] == (TimeDomainKHR)0;
            qpc |= domains[index] == (TimeDomainKHR)3;
        }
        if (!device || !qpc)
            return null;
        CalibratedTimestampInfoKHR* infos = stackalloc CalibratedTimestampInfoKHR[2]
        {
            new() { SType = (StructureType)1000184000, TimeDomain = (TimeDomainKHR)0 },
            new() { SType = (StructureType)1000184000, TimeDomain = (TimeDomainKHR)3 },
        };
        ulong* timestamps = stackalloc ulong[2];
        ulong maximumDeviation = 0;
        Result result = _calibratedTimestamps.GetCalibratedTimestamp(
            _host.Device, 2, infos, timestamps, &maximumDeviation);
        return result == Result.Success
            ? new RenderBenchGpuCalibrationSample(timestamps[0], timestamps[1], maximumDeviation)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int index = 0; index < _pools.Length; index++)
            if (_pools[index].Handle != 0)
                _host.Api.DestroyQueryPool(_host.Device, _pools[index], null);
    }
}
