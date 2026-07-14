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
            private uint _activeQueryCount = 1;

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
                // PrepareForRecording resets the resolved query before recording begins.
                // The occlusion coordinator never schedules a new epoch until the prior
                // result is available, so the host reset is externally synchronized.
                if (!EnsureQueryPool(queryType))
                    return false;

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

            /// <summary>
            /// Resets the query before its next begin is recorded. CpuQueryAsync only
            /// reaches this point after the prior result has resolved, which makes a
            /// host reset safe and avoids carrying reset state between independently
            /// submitted desktop and OpenXR command buffers.
            /// </summary>
            internal bool PrepareForRecording(CommandBuffer commandBuffer, EQueryTarget target, uint queryCount = 1)
            {
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) || !isOcclusion)
                    return false;
                if (!EnsureQueryPool(queryType))
                    return false;

                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Reset");
                _activeQueryCount = Math.Clamp(queryCount, 1u, 2u);
                if (Renderer.SupportsHostQueryReset)
                {
                    Renderer.EnsureVulkanResourceMutationAllowed(
                        ObjectType.QueryPool,
                        _queryPool.Handle,
                        "ResetQueryPoolBeforeRecording");
                    Api!.ResetQueryPool(Device, _queryPool, 0, _activeQueryCount);
                }
                // Keep an execution-ordered reset in the command buffer as well. A
                // reusable OpenXR primary can be submitted after desktop work that was
                // recorded after the host reset; the queued reset establishes the query
                // epoch at the exact execution boundary in both cases.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, _activeQueryCount);
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
                Api!.ResetQueryPool(Device, _queryPool, 0, _activeQueryCount);
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

                ulong* values = stackalloc ulong[2];
                Result queryResult = Api!.GetQueryPoolResults(
                    Device,
                    _queryPool,
                    0,
                    _activeQueryCount,
                    (nuint)(sizeof(ulong) * _activeQueryCount),
                    values,
                    (ulong)sizeof(ulong),
                    flags);

                if (queryResult == Result.Success)
                {
                    Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
                    result = values[0] | (_activeQueryCount > 1 ? values[1] : 0UL);
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

                ulong* data = stackalloc ulong[4];
                Result queryResult = Api!.GetQueryPoolResults(
                    Device,
                    _queryPool,
                    0,
                    _activeQueryCount,
                    (nuint)(sizeof(ulong) * 2 * _activeQueryCount),
                    data,
                    (ulong)(sizeof(ulong) * 2),
                    QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);

                if (queryResult == Result.Success || queryResult == Result.NotReady)
                {
                    available = data[1] != 0 && (_activeQueryCount == 1 || data[3] != 0);
                    if (available)
                        Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
                    return true;
                }

                return false;
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
                    // Multiview occlusion queries consume one consecutive query index
                    // per active view. Reserve both stereo indices even for mono use.
                    QueryCount = 2,
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
