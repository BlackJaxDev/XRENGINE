using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanTextureUploadService
{
    // A bounded pool prevents upload telemetry from allocating query pools in
    // the transfer recorder or retaining a pool after its batch is complete.
    private const int TransferGpuTimestampPoolCount = 4;
    private readonly object _transferGpuTimingSync = new();
    private readonly QueryPool[] _transferGpuTimestampPools = new QueryPool[TransferGpuTimestampPoolCount];
    private readonly bool[] _transferGpuTimestampLeased = new bool[TransferGpuTimestampPoolCount];
    // 0 = not attempted, 1 = available, 2 = unavailable for this device generation.
    private int _transferGpuTimestampInitializationState;
    private uint _transferGpuTimestampValidBits;
    private double _transferGpuTimestampPeriodNanoseconds;

    /// <summary>
    /// Creates the fixed query-pool inventory during owned worker preparation,
    /// never from admission or the per-chunk recorder. Failure is diagnostic-only: transfer work
    /// remains valid without a GPU timing sample.
    /// </summary>
    private unsafe void EnsureTransferGpuTimingPools(VulkanTextureUploadSchedulingContext context)
    {
        lock (_transferGpuTimingSync)
        {
            if (_transferGpuTimestampInitializationState != 0)
                return;

            uint validBits = context.Resources.Queries.Capabilities.GraphicsTimestampValidBits;
            if (validBits == 0 || !context.Commands.IsDeviceOperational)
            {
                _transferGpuTimestampInitializationState = 2;
                return;
            }

            context.Commands.DeviceContext.Api.GetPhysicalDeviceProperties(
                context.Commands.DeviceContext.PhysicalDevice,
                out PhysicalDeviceProperties properties);
            double periodNanoseconds = properties.Limits.TimestampPeriod;
            if (periodNanoseconds <= 0.0)
            {
                _transferGpuTimestampInitializationState = 2;
                return;
            }

            QueryPoolCreateInfo createInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = 2,
            };

            for (int index = 0; index < _transferGpuTimestampPools.Length; index++)
            {
                Result result = context.Commands.DeviceContext.Api.CreateQueryPool(
                    context.Commands.DeviceContext.Device,
                    ref createInfo,
                    null,
                    out QueryPool queryPool);
                if (result != Result.Success || queryPool.Handle == 0)
                {
                    RetireUnleasedTransferGpuTimingPoolsNoLock(context.Resources);
                    _transferGpuTimestampInitializationState = 2;
                    return;
                }

                _transferGpuTimestampPools[index] = queryPool;
                context.Resources.RegisterResource(
                    ObjectType.QueryPool,
                    queryPool.Handle,
                    $"TextureUpload.TransferGpuTiming[{index}]");
            }

            _transferGpuTimestampValidBits = validBits;
            _transferGpuTimestampPeriodNanoseconds = periodNanoseconds;
            _transferGpuTimestampInitializationState = 1;
        }
    }

    internal bool TryAcquireTransferGpuTimestampLease(out VulkanTextureUploadGpuTimestampLease lease)
    {
        lock (_transferGpuTimingSync)
        {
            if (_transferGpuTimestampInitializationState != 1)
            {
                lease = default;
                RecordImportedTextureTransferGpuUnavailable();
                return false;
            }

            for (int index = 0; index < _transferGpuTimestampPools.Length; index++)
            {
                if (_transferGpuTimestampLeased[index] || _transferGpuTimestampPools[index].Handle == 0)
                    continue;

                _transferGpuTimestampLeased[index] = true;
                lease = new VulkanTextureUploadGpuTimestampLease(
                    index,
                    _transferGpuTimestampPools[index],
                    _transferGpuTimestampValidBits,
                    _transferGpuTimestampPeriodNanoseconds);
                return true;
            }
        }

        lease = default;
        RecordImportedTextureTransferGpuUnavailable();
        return false;
    }

    internal void ReleaseTransferGpuTimestampLease(in VulkanTextureUploadGpuTimestampLease lease)
    {
        if (!lease.IsValid)
            return;

        lock (_transferGpuTimingSync)
        {
            if ((uint)lease.Slot < (uint)_transferGpuTimestampLeased.Length &&
                _transferGpuTimestampPools[lease.Slot].Handle == lease.QueryPool.Handle)
            {
                _transferGpuTimestampLeased[lease.Slot] = false;
            }
        }
    }

    /// <summary>
    /// Queues the fixed pools for normal lifetime retirement only after every
    /// submitted batch has released its query-pair lease. A quarantined batch
    /// deliberately keeps its pair registered through device teardown.
    /// </summary>
    internal void TryRetireTransferGpuTimingPools(VulkanResourceRuntime resources)
    {
        lock (_transferGpuTimingSync)
        {
            if (_transferGpuTimestampInitializationState != 1)
                return;
            for (int index = 0; index < _transferGpuTimestampLeased.Length; index++)
                if (_transferGpuTimestampLeased[index])
                    return;

            RetireUnleasedTransferGpuTimingPoolsNoLock(resources);
            _transferGpuTimestampInitializationState = 2;
        }
    }

    private void RetireUnleasedTransferGpuTimingPoolsNoLock(VulkanResourceRuntime resources)
    {
        for (int index = 0; index < _transferGpuTimestampPools.Length; index++)
        {
            QueryPool queryPool = _transferGpuTimestampPools[index];
            _transferGpuTimestampPools[index] = default;
            if (queryPool.Handle != 0)
                resources.RetireQueryPool(queryPool, "TextureUpload.TransferGpuTiming");
        }
    }
}
