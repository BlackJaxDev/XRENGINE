using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Vulkan render query wrapper class.
        /// Provides functionality to begin, end, and manage Vulkan render queries.
        /// </summary>
        /// <param name="api">The Vulkan renderer instance.</param>
        /// <param name="data">The render query data associated with this Vulkan query.</param>
        public class VkRenderQuery(VulkanRenderer api, XRRenderQuery data) : VkObject<XRRenderQuery>(api, data)
        {
            private QueryPool _queryPool;
            private QueryType _queryType = QueryType.Occlusion;
            private uint _queryPoolCapacity;
            private uint _resultQueryCount = 1u;
            private bool _queryActive;
            private int _hasSubmittedResultEpoch;

            private const uint MaxOcclusionQueryViewSlots = 32u;

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

            /// <summary>
            /// Begins a Vulkan render query for the specified target.
            /// </summary>
            /// <param name="commandBuffer">The Vulkan command buffer to record the query commands into.</param>
            /// <param name="target">The type of query to begin.</param>
            /// <param name="flags">Optional query control flags.</param>
            /// <param name="viewMask">The view mask for occlusion queries.</param>
            /// <returns>True if the query was successfully begun; otherwise, false.</returns>
            public bool BeginQuery(
                CommandBuffer commandBuffer,
                EQueryTarget target,
                QueryControlFlags flags = QueryControlFlags.None,
                uint viewMask = 0u)
            {
                // Begin the query based on the target type.
                // Transform feedback queries are handled separately.
                if (target == EQueryTarget.TransformFeedbackPrimitivesWritten)
                    return BeginTransformFeedbackQuery(commandBuffer, flags);

                // Map the high-level query target to the Vulkan query type
                // and determine if it is an occlusion query.
                // If the mapping fails, the query cannot be started.
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion))
                    return false;

                // Ensure that only occlusion queries are supported for now.
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
                // PrepareForRecording emits the reset before any render pass begins.
                // Keeping reset/begin/end queue ordered also prevents another output's
                // in-flight query epoch from racing a host-side reset.
                if (!EnsureQueryPool(queryType))
                    return false;

                // Resolve the number of query slots needed for the current view mask.
                _resultQueryCount = ResolveOcclusionQueryViewSlotCount(viewMask);

                // Track the query pool resource usage for the current command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Begin");

                // Begin the Vulkan query.
                Api.CmdBeginQuery(commandBuffer, _queryPool, 0, flags);

                // Mark the current query as active and store the target type.
                Data.CurrentQuery = target;
                _queryActive = true;

                // Return true to indicate that the query has been successfully begun.
                return true;
            }

            /// <summary>
            /// Records the required query reset before any render scope begins. Keeping
            /// reset and begin in one queue-ordered command buffer avoids host resets
            /// racing another output that still has the prior query epoch in flight.
            /// </summary>
            internal bool PrepareForRecording(CommandBuffer commandBuffer, EQueryTarget target)
            {
                // Prepare the query for recording by ensuring it is mapped and the query pool is available.
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) ||
                    !isOcclusion ||
                    !EnsureQueryPool(queryType))
                    return false;

                // Track the query pool resource usage for the current command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Reset");

                // Reset the Vulkan query pool to ensure it is ready for a new query.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, _queryPoolCapacity);
                Volatile.Write(ref _hasSubmittedResultEpoch, 0);

                // Mark the query as prepared for recording.
                return true;
            }

            /// <summary>
            /// Ends the currently active Vulkan render query.
            /// </summary>
            /// <param name="commandBuffer">The Vulkan command buffer to record the query end command into.</param>
            public void EndQuery(CommandBuffer commandBuffer)
            {
                // End the currently active Vulkan render query if it is valid.
                if (!_queryActive || _queryPool.Handle == 0)
                    return;

                // Track the query pool resource usage for the current command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.End");

                // End the query based on its type and the capabilities of the renderer.
                if (_queryType == QueryType.TransformFeedbackStreamExt &&
                    Renderer.SupportsTransformFeedbackQueries &&
                    Renderer._extTransformFeedback is not null)
                    Renderer._extTransformFeedback.CmdEndQueryIndexed(commandBuffer, _queryPool, 0, 0);
                else
                    Api!.CmdEndQuery(commandBuffer, _queryPool, 0);

                // Mark the query as no longer active.
                _queryActive = false;
                Data.CurrentQuery = null;
            }

            /// <summary>
            /// Prepares the Vulkan render query for reuse with a new command buffer.
            /// </summary>
            /// <param name="target">The query target indicating the type of query to prepare for reuse.</param>
            /// <returns>True if the query was successfully prepared for reuse; otherwise, false.</returns>
            internal bool PrepareForCommandBufferReuse(EQueryTarget target)
            {
                // Only occlusion queries are currently supported for command buffer reuse.
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) || !isOcclusion)
                    return false;

                // Ensure that the query pool is available and supports host query reset before proceeding.
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

                // Reset the query pool on the host to prepare for command buffer reuse.
                Api!.ResetQueryPool(Device, _queryPool, 0, _queryPoolCapacity);
                Volatile.Write(ref _hasSubmittedResultEpoch, 0);

                // The query pool has been reset and is now ready for reuse with the new command buffer.
                return true;
            }

            /// <summary>
            /// Writes a timestamp to the Vulkan command buffer at the specified pipeline stage.
            /// </summary>
            /// <param name="commandBuffer">The Vulkan command buffer to record the timestamp command into.</param>
            /// <param name="stage">The pipeline stage at which to write the timestamp.</param>
            /// <returns>True if the timestamp was successfully written; otherwise, false.</returns>
            public bool WriteTimestamp(CommandBuffer commandBuffer, PipelineStageFlags stage = PipelineStageFlags.BottomOfPipeBit)
            {
                // Ensure that the timestamp query pool is available before writing the timestamp.
                if (!EnsureQueryPool(QueryType.Timestamp))
                    return false;

                // Track the query pool resource usage for the command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Timestamp");

                // Reset the timestamp query in the command buffer before writing the new timestamp.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Api.CmdWriteTimestamp(commandBuffer, stage, _queryPool, 0);
                Volatile.Write(ref _hasSubmittedResultEpoch, 0);

                // Mark the current query as a timestamp query and indicate that it is no longer active.
                Data.CurrentQuery = EQueryTarget.Timestamp;
                _queryActive = false;

                // The timestamp has been written to the command buffer.
                return true;
            }

            /// <summary>
            /// Attempts to retrieve the result of the Vulkan render query.
            /// </summary>
            /// <param name="result">The variable to store the query result.</param>
            /// <param name="wait">Indicates whether to wait for the result if it is not yet available.</param>
            /// <returns>True if the result was successfully retrieved; otherwise, false.</returns>
            public bool TryGetResult(out ulong result, bool wait = false)
            {
                result = 0ul;

                // Ensure that the query pool is valid before attempting to retrieve the result.
                if (_queryPool.Handle == 0 || Volatile.Read(ref _hasSubmittedResultEpoch) == 0)
                    return false;

                // Determine the appropriate flags for retrieving the query result based on whether waiting is allowed.
                QueryResultFlags flags = QueryResultFlags.Result64Bit;
                if (wait)
                    flags |= QueryResultFlags.ResultWaitBit;

                // Clamp the number of queries to retrieve to the valid range of the query pool.
                uint queryCount = Math.Clamp(_resultQueryCount, 1u, _queryPoolCapacity);
                Span<ulong> values = stackalloc ulong[checked((int)queryCount)];
                Result queryResult;
                fixed (ulong* pValues = values)
                {
                    // Retrieve the query results from the Vulkan query pool into the allocated span.
                    queryResult = Api!.GetQueryPoolResults(
                        Device,
                        _queryPool,
                        0,
                        queryCount,
                        checked((nuint)(queryCount * sizeof(ulong))),
                        pValues,
                        (ulong)sizeof(ulong),
                        flags);
                }

                // Check if the query results were successfully retrieved.
                if (queryResult == Result.Success)
                {
                    // Notify the renderer that the Vulkan query pool resource has been used.
                    Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
                    Volatile.Write(ref _hasSubmittedResultEpoch, 0);

                    // Check the retrieved query results and set the result accordingly.
                    // Iterate through the retrieved query results to determine if any of them indicate a successful query.
                    for (int index = 0; index < values.Length; index++)
                    {
                        // Check if the current query result is non-zero, indicating a successful query.
                        if (values[index] != 0ul)
                        {
                            result = 1ul;
                            break;
                        }
                    }
                    return true;
                }

                // Handle the case where the query result is not ready and waiting is not allowed.
                if (!wait && queryResult == Result.NotReady)
                    return false;

                // Log a warning indicating that retrieving the query results from the Vulkan query pool failed.
                Debug.VulkanWarning($"GetQueryPoolResults failed for query '{Data.Name ?? "<unnamed>"}'. Result={queryResult}.");
                return false;
            }

            /// <summary>
            /// Tries to get the availability status of the query result.
            /// </summary>
            /// <param name="available">Indicates whether the query result is available.</param>
            /// <returns>True if the availability status could be determined, false otherwise.</returns>
            public bool TryGetResultAvailable(out bool available)
            {
                available = false;

                // Check if the Vulkan query pool handle is valid before attempting to retrieve the query results.
                if (_queryPool.Handle == 0 || Volatile.Read(ref _hasSubmittedResultEpoch) == 0)
                    return false;

                // Determine the number of queries to retrieve, clamping it to the valid range.
                uint queryCount = Math.Clamp(_resultQueryCount, 1u, _queryPoolCapacity);
                // Allocate a span to hold the query results, with space for both the result and availability status for each query.
                Span<ulong> data = stackalloc ulong[checked((int)queryCount * 2)];
                fixed (ulong* pData = data)
                {
                    // Retrieve the query results from the Vulkan query pool.
                    Result queryResult = Api!.GetQueryPoolResults(
                        Device,
                        _queryPool,
                        0,
                        queryCount,
                        checked((nuint)(queryCount * sizeof(ulong) * 2u)),
                        pData,
                        (ulong)(sizeof(ulong) * 2),
                        QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
                    // The query results are now stored in the 'data' span, with each query's result followed by its availability status.

                    // Check if the query results retrieval was successful or if the results are not yet ready.
                    if (queryResult == Result.Success || queryResult == Result.NotReady)
                    {
                        // Initially assume that the query results are available.
                        available = true;
                        // Iterate through the query results to check their availability.
                        for (int index = 0; index < data.Length; index += 2)
                        {
                            // Check the availability status of the current query.
                            if (data[index + 1] == 0ul)
                            {
                                // If the availability status is 0, it means the query result is not yet available.
                                available = false;
                                break;
                            }
                        }

                        // If all queries are available, notify that the Vulkan resource use is completed.
                        if (available)
                            Renderer.NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle);
                        
                        // Return true to indicate that the query results were successfully retrieved (or are not yet ready).
                        return true;
                    }

                    // If the query results retrieval was not successful and the results are not yet ready, return false.
                    return false;
                }
            }

            /// <summary>
            /// Ensures that a query pool exists for the specified query type, creating one if necessary.
            /// </summary>
            /// <param name="queryType">The type of query for which to ensure a query pool exists.</param>
            /// <returns>True if the query pool exists or was successfully created, false otherwise.</returns>
            private bool EnsureQueryPool(QueryType queryType)
            {
                // Check if a query pool already exists and matches the requested query type.
                if (_queryPool.Handle != 0 && _queryType == queryType)
                    return true;

                // Destroy the existing query pool if it exists, as it does not match the requested query type.
                DestroyQueryPool();

                // Determine the capacity of the new query pool based on the query type.
                uint queryPoolCapacity = queryType == QueryType.Occlusion
                    ? MaxOcclusionQueryViewSlots
                    : 1u;

                // Create the query pool with the determined capacity.
                QueryPoolCreateInfo createInfo = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = queryType,
                    QueryCount = queryPoolCapacity,
                    PipelineStatistics = QueryPipelineStatisticFlags.None,
                };

                // Attempt to create the query pool using the Vulkan API.
                if (Api!.CreateQueryPool(Device, ref createInfo, null, out _queryPool) != Result.Success)
                {
                    Debug.VulkanWarning($"Failed to create Vulkan query pool for target type '{queryType}'.");
                    return false;
                }

                // Register the newly created query pool with the renderer for resource tracking.
                Renderer.RegisterVulkanResource(
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    $"QueryPool.{Data.Name ?? "<unnamed>"}");
                Renderer.RegisterVulkanRenderQuery(_queryPool, this);

                // Update the internal state to reflect the newly created query pool.
                _queryType = queryType;
                _queryPoolCapacity = queryPoolCapacity;

                // Return true to indicate that the query pool was successfully created.
                return true;
            }

            /// <summary>
            /// Resolves the number of occlusion query view slots required for the given view mask.
            /// </summary>
            /// <param name="viewMask">The view mask for which to determine the required number of query view slots.</param>
            /// <returns>The number of query view slots required for the specified view mask, capped at the maximum allowed.</returns>
            private static uint ResolveOcclusionQueryViewSlotCount(uint viewMask)
            {
                if (viewMask == 0u)
                    return 1u;

                // Vulkan assigns one consecutive query result per enabled view,
                // so sparse masks consume popcount(mask), not highestBit + 1.
                uint slotCount = 0u;
                while (viewMask != 0u)
                {
                    // Count the number of set bits in the view mask to determine the number of required query view slots.
                    viewMask &= viewMask - 1u;
                    slotCount++;
                }

                // Cap the slot count at the maximum allowed number of occlusion query view slots.
                return Math.Min(slotCount, MaxOcclusionQueryViewSlots);
            }

            /// <summary>
            /// Begins a transform feedback query on the specified command buffer with the given control flags.
            /// </summary>
            /// <param name="commandBuffer">The command buffer on which to begin the transform feedback query.</param>
            /// <param name="flags">The control flags for the query.</param>
            /// <returns>True if the query was successfully begun, false otherwise.</returns>
            private bool BeginTransformFeedbackQuery(CommandBuffer commandBuffer, QueryControlFlags flags)
            {
                // Begin a transform feedback query on the specified command buffer with the given control flags.
                if (!Renderer.SupportsTransformFeedbackQueries ||
                    Renderer._extTransformFeedback is null)
                {
                    // If transform feedback queries are not supported or the extension is unavailable, skip the query.
                    Debug.VulkanWarning(
                        "Transform feedback query skipped: {0} is unavailable or transformFeedbackQueries is false.",
                        Silk.NET.Vulkan.Extensions.EXT.ExtTransformFeedback.ExtensionName);
                    return false;
                }

                // Ensure that the query pool for transform feedback queries is available before beginning the query.
                if (!EnsureQueryPool(QueryType.TransformFeedbackStreamExt))
                    return false;

                // Track the query pool resource for the command buffer before resetting and beginning the query.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.TransformFeedback");

                // Reset the query pool and begin the transform feedback query.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Renderer._extTransformFeedback.CmdBeginQueryIndexed(commandBuffer, _queryPool, 0, flags, 0);
                
                // Mark the current query as a transform feedback primitives written query.
                Data.CurrentQuery = EQueryTarget.TransformFeedbackPrimitivesWritten;
                _queryActive = true;

                // Return true to indicate that the transform feedback query has been successfully begun.
                return true;
            }

            /// <summary>
            /// Destroys the query pool associated with this render query and resets its state.
            /// </summary>
            /// <remarks>
            /// This method should be called when the render query is no longer needed to free up Vulkan resources.
            /// </remarks>
            private void DestroyQueryPool()
            {
                // Destroy the query pool if it has been created and reset the associated state.
                if (_queryPool.Handle != 0)
                {
                    Renderer.UnregisterVulkanRenderQuery(_queryPool, this);
                    Renderer.RetireQueryPool(_queryPool);
                    _queryPool = default;
                }

                // Reset the query pool capacity and result query count to their default values.
                _queryPoolCapacity = 0u;
                _resultQueryCount = 1u;
                Volatile.Write(ref _hasSubmittedResultEpoch, 0);

                // Mark the query as inactive and clear the current query data.
                _queryActive = false;
                Data.CurrentQuery = null;
            }

            internal void MarkResultEpochSubmitted()
                => Volatile.Write(ref _hasSubmittedResultEpoch, 1);

            /// <summary>
            /// Attempts to map a high-level query target to the corresponding Vulkan query type and occlusion status.
            /// </summary>
            /// <param name="target">The high-level query target to map.</param>
            /// <param name="queryType">Outputs the corresponding Vulkan query type.</param>
            /// <param name="isOcclusion">Outputs whether the query is an occlusion query.</param>
            /// <returns>True if the mapping was successful, false otherwise.</returns>
            private static bool TryMapQueryType(EQueryTarget target, out QueryType queryType, out bool isOcclusion)
            {
                // Attempt to map the high-level query target to the corresponding Vulkan query type and occlusion status.
                switch (target)
                {
                    case EQueryTarget.SamplesPassed: // Occlusion query for samples passed
                    case EQueryTarget.AnySamplesPassed: // Occlusion query for any samples passed
                    case EQueryTarget.AnySamplesPassedConservative: // Occlusion query for any samples passed conservatively
                        queryType = QueryType.Occlusion;
                        isOcclusion = true;
                        return true;
                    case EQueryTarget.Timestamp: // Timestamp query
                        queryType = QueryType.Timestamp;
                        isOcclusion = false;
                        return true;
                    default: // Unknown query target
                        queryType = QueryType.Occlusion;
                        isOcclusion = false;
                        return false;
                }
            }
        }
    }
}
