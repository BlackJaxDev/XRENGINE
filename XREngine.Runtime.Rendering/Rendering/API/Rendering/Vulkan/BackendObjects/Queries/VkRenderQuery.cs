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
            private static bool _loggedHostQueryResetUnsupported;

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
                if (target == EQueryTarget.TransformFeedbackPrimitivesWritten)
                    return BeginTransformFeedbackQuery(commandBuffer, flags);

                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion))
                    return false;

                if (!isOcclusion)
                {
                    Debug.VulkanWarning($"Unsupported BeginQuery target '{target}' in Vulkan query wrapper. Use WriteTimestamp for timestamp queries.");
                    return false;
                }

                // Pool lifetime: the pool is created once and reused across submissions.
                // It must NOT be destroyed here — previously recorded command buffers
                // (including the still-pending previous frame) reference it, and destroying
                // it invalidates them (InvalidCommandBuffer-VkQueryPool validation errors
                // followed by an access violation inside vkQueueSubmit2).
                //
                // Reuse is safe because the reset below happens on the HOST, immediately:
                // the old hazard was that vkCmdResetQueryPool is queued on the GPU, letting
                // a CPU poll observe a stale availability bit before the queued reset ran.
                // vkResetQueryPool has no such window, and the CPU occlusion coordinator
                // only re-begins a query after its prior result has resolved, so the pool
                // has no pending GPU use when we reset it.
                //
                // Occlusion QueryOps are recorded while the target's render pass is
                // active, and vkCmdResetQueryPool must only be recorded outside a
                // render pass instance (VUID-vkCmdResetQueryPool-renderpass), so the
                // host-side reset (core 1.2 hostQueryReset) is also the only valid
                // reset point for this path.
                if (!Renderer.SupportsHostQueryReset)
                {
                    if (!_loggedHostQueryResetUnsupported)
                    {
                        _loggedHostQueryResetUnsupported = true;
                        Debug.VulkanWarning(
                            "Occlusion query skipped: device does not support hostQueryReset (Vulkan 1.2), " +
                            "and vkCmdResetQueryPool cannot be recorded inside an active render pass. " +
                            "Occlusion candidates remain visible.");
                    }

                    return false;
                }

                if (!EnsureQueryPool(queryType))
                    return false;

                Renderer.EnsureVulkanResourceMutationAllowed(
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "ResetQueryPool");
                Api!.ResetQueryPool(Device, _queryPool, 0, 1);
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Begin");
                Api.CmdBeginQuery(commandBuffer, _queryPool, 0, flags);
                Data.CurrentQuery = target;
                _queryActive = true;
                return true;
            }

            public void EndQuery(CommandBuffer commandBuffer)
            {
                if (!_queryActive || _queryPool.Handle == 0)
                    return;

                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.End");

                if (_queryType == QueryType.TransformFeedbackStreamExt && Renderer.SupportsTransformFeedbackQueries && Renderer._extTransformFeedback is not null)
                    Renderer._extTransformFeedback.CmdEndQueryIndexed(commandBuffer, _queryPool, 0, 0);
                else
                    Api!.CmdEndQuery(commandBuffer, _queryPool, 0);

                _queryActive = false;
                Data.CurrentQuery = null;
            }

            internal bool PrepareForCommandBufferReuse(EQueryTarget target)
            {
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) || !isOcclusion)
                    return false;

                if (!Renderer.SupportsHostQueryReset || !EnsureQueryPool(queryType))
                    return false;

                // The occlusion coordinator only schedules a new query after the prior
                // result resolves. Reset the persistent pool on the host before replaying
                // the already-recorded begin/end commands; the command buffer itself does
                // not need to be re-recorded merely to establish a new query epoch.
                Renderer.EnsureVulkanResourceMutationAllowed(
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "ResetQueryPoolForCommandBufferReuse");
                Api!.ResetQueryPool(Device, _queryPool, 0, 1);
                return true;
            }

            public bool WriteTimestamp(CommandBuffer commandBuffer, PipelineStageFlags stage = PipelineStageFlags.BottomOfPipeBit)
            {
                if (!EnsureQueryPool(QueryType.Timestamp))
                    return false;

                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Timestamp");
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
                    Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
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
                        if (available)
                            Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
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

                Renderer.RegisterVulkanResource(
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    $"QueryPool.{Data.Name ?? "<unnamed>"}");
                _queryType = queryType;
                return true;
            }

            private bool BeginTransformFeedbackQuery(CommandBuffer commandBuffer, QueryControlFlags flags)
            {
                if (!Renderer.SupportsTransformFeedbackQueries || Renderer._extTransformFeedback is null)
                {
                    Debug.VulkanWarning(
                        "Transform feedback query skipped: {0} is unavailable or transformFeedbackQueries is false.",
                        Silk.NET.Vulkan.Extensions.EXT.ExtTransformFeedback.ExtensionName);
                    return false;
                }

                if (!EnsureQueryPool(QueryType.TransformFeedbackStreamExt))
                    return false;

                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.TransformFeedback");
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Renderer._extTransformFeedback.CmdBeginQueryIndexed(commandBuffer, _queryPool, 0, flags, 0);
                Data.CurrentQuery = EQueryTarget.TransformFeedbackPrimitivesWritten;
                _queryActive = true;
                return true;
            }

            private void DestroyQueryPool()
            {
                if (_queryPool.Handle != 0)
                {
                    Renderer.RetireQueryPool(_queryPool);
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
