using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public class VkRenderQuery(VulkanRenderer api, XRRenderQuery data) : VkObject<XRRenderQuery>(api, data)
        {
            private QueryPool _queryPool;
            private QueryType _queryType = QueryType.Occlusion;
            private bool _queryActive;

            public override VkObjectType Type => VkObjectType.Query;
            public override bool IsGenerated => IsActive;
            protected override uint CreateObjectInternal() => CacheObject(this);

            protected override void DeleteObjectInternal()
            {
                DestroyQueryPool();
                RemoveCachedObject(BindingId);
            }

            protected override void LinkData()
            {
            }

            protected override void UnlinkData()
            {
                if (_queryActive)
                    _queryActive = false;
            }

            public bool BeginQuery(CommandBuffer commandBuffer, EQueryTarget target, QueryControlFlags flags = QueryControlFlags.None)
            {
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion))
                    return false;

                if (!isOcclusion)
                {
                    Debug.VulkanWarning($"Unsupported BeginQuery target '{target}' in Vulkan query wrapper. Use WriteTimestamp for timestamp queries.");
                    return false;
                }

                if (!EnsureQueryPool(queryType))
                    return false;

                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Api.CmdBeginQuery(commandBuffer, _queryPool, 0, flags);
                Data.CurrentQuery = target;
                _queryActive = true;
                return true;
            }

            public void EndQuery(CommandBuffer commandBuffer)
            {
                if (!_queryActive || _queryPool.Handle == 0)
                    return;

                Api!.CmdEndQuery(commandBuffer, _queryPool, 0);
                _queryActive = false;
                Data.CurrentQuery = null;
            }

            public bool WriteTimestamp(CommandBuffer commandBuffer, PipelineStageFlags stage = PipelineStageFlags.BottomOfPipeBit)
            {
                if (!EnsureQueryPool(QueryType.Timestamp))
                    return false;

                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Api.CmdWriteTimestamp(commandBuffer, stage, _queryPool, 0);
                Data.CurrentQuery = EQueryTarget.Timestamp;
                _queryActive = false;
                return true;
            }

            public bool TryGetResult(out ulong result, bool wait = false)
            {
                result = 0ul;
                if (_queryPool.Handle == 0)
                    return false;

                QueryResultFlags flags = QueryResultFlags.Result64Bit;
                if (wait)
                    flags |= QueryResultFlags.ResultWaitBit;

                ulong value = 0ul;
                Result queryResult = Api!.GetQueryPoolResults(
                    Device,
                    _queryPool,
                    0,
                    1,
                    (nuint)sizeof(ulong),
                    &value,
                    (ulong)sizeof(ulong),
                    flags);

                if (queryResult == Result.Success)
                {
                    result = value;
                    return true;
                }

                if (!wait && queryResult == Result.NotReady)
                    return false;

                Debug.VulkanWarning($"GetQueryPoolResults failed for query '{Data.Name ?? "<unnamed>"}'. Result={queryResult}.");
                return false;
            }

            public bool TryGetResultAvailable(out bool available)
            {
                available = false;
                if (_queryPool.Handle == 0)
                    return false;

                ulong[] data = new ulong[2];
                fixed (ulong* pData = data)
                {
                    Result queryResult = Api!.GetQueryPoolResults(
                        Device,
                        _queryPool,
                        0,
                        1,
                        (nuint)(sizeof(ulong) * 2),
                        pData,
                        (ulong)(sizeof(ulong) * 2),
                        QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);

                    if (queryResult == Result.Success || queryResult == Result.NotReady)
                    {
                        available = data[1] != 0;
                        return true;
                    }

                    return false;
                }
            }

            private bool EnsureQueryPool(QueryType queryType)
            {
                if (_queryPool.Handle != 0 && _queryType == queryType)
                    return true;

                DestroyQueryPool();

                QueryPoolCreateInfo createInfo = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = queryType,
                    QueryCount = 1,
                    PipelineStatistics = QueryPipelineStatisticFlags.None,
                };

                if (Api!.CreateQueryPool(Device, ref createInfo, null, out _queryPool) != Result.Success)
                {
                    Debug.VulkanWarning($"Failed to create Vulkan query pool for target type '{queryType}'.");
                    return false;
                }

                _queryType = queryType;
                return true;
            }

            private void DestroyQueryPool()
            {
                if (_queryPool.Handle != 0)
                {
                    Api!.DestroyQueryPool(Device, _queryPool, null);
                    _queryPool = default;
                }

                _queryActive = false;
                Data.CurrentQuery = null;
            }

            private static bool TryMapQueryType(EQueryTarget target, out QueryType queryType, out bool isOcclusion)
            {
                switch (target)
                {
                    case EQueryTarget.SamplesPassed:
                    case EQueryTarget.AnySamplesPassed:
                    case EQueryTarget.AnySamplesPassedConservative:
                        queryType = QueryType.Occlusion;
                        isOcclusion = true;
                        return true;
                    case EQueryTarget.Timestamp:
                        queryType = QueryType.Timestamp;
                        isOcclusion = false;
                        return true;
                    default:
                        queryType = QueryType.Occlusion;
                        isOcclusion = false;
                        return false;
                }
            }
        }
    }
}
