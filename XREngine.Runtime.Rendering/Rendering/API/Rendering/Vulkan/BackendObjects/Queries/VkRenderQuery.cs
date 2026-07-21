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
            private bool _queryActive;
            private uint _activeQueryCount = 1u;
            private readonly object _resultEpochLock = new();
            private readonly Dictionary<ulong, RecordedResultEpoch> _recordedResultEpochs = [];
            private SubmittedResultEpoch _submittedResultEpoch;
            private ulong _nextSubmittedResultEpochSerial;

            private readonly record struct RecordedResultEpoch(uint QueryCount, bool ForceVisible);

            private readonly record struct SubmittedResultEpoch(
                ulong Serial,
                ulong CommandBufferHandle,
                uint QueryCount,
                bool ForceVisible,
                VulkanLifetimeSubmission Submission)
            {
                public bool IsValid => Serial != 0ul;
            }

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
                // PrepareForRecording records an execution-ordered reset before the next
                // query epoch. Keeping the reset on the GPU avoids racing a host reset
                // against an older submitted command buffer that still references the pool.
                if (!EnsureQueryPool(queryType))
                    return false;

                // A non-zero dynamic-rendering view mask is authoritative. Legacy
                // multiview scopes pass their slot count through PrepareForRecording.
                if (viewMask != 0u)
                    _activeQueryCount = ResolveOcclusionQueryViewSlotCount(viewMask);

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                lock (_resultEpochLock)
                {
                    if (!_recordedResultEpochs.TryGetValue(commandBufferHandle, out RecordedResultEpoch recordedEpoch))
                        return false;

                    _recordedResultEpochs[commandBufferHandle] = recordedEpoch with
                    {
                        QueryCount = _activeQueryCount,
                    };
                }

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
            /// Records a reset before the query's next begin. The reset executes on the
            /// graphics queue, after older submissions that may still reference the pool.
            /// </summary>
            internal bool PrepareForRecording(CommandBuffer commandBuffer, EQueryTarget target, uint queryCount = 1)
            {
                // Prepare the query for recording by ensuring it is mapped and the query pool is available.
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) ||
                    !isOcclusion ||
                    !EnsureQueryPool(queryType))
                    return false;

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                if (commandBufferHandle == 0)
                    return false;

                uint clampedQueryCount = Math.Clamp(queryCount, 1u, _queryPoolCapacity);
                lock (_resultEpochLock)
                {
                    // One VkQueryPool range represents exactly one unresolved epoch.
                    // Recording another use while that epoch is pending would let a
                    // queued reset overwrite the result before the CPU consumes it.
                    if (_submittedResultEpoch.IsValid)
                    {
                        _recordedResultEpochs.Remove(commandBufferHandle);
                        return false;
                    }

                    _recordedResultEpochs[commandBufferHandle] = new RecordedResultEpoch(
                        clampedQueryCount,
                        ForceVisible: false);
                }

                // Track the query pool resource usage for the current command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Reset");
                _activeQueryCount = clampedQueryCount;
                // The queued reset is the sole epoch boundary. A host reset here is not
                // safe merely because a result was available: Vulkan requires all
                // submitted commands that refer to the range to have completed.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, _queryPoolCapacity);
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
            internal bool PrepareForCommandBufferReuse(CommandBuffer commandBuffer, EQueryTarget target)
            {
                // Only occlusion queries are currently supported for command buffer reuse.
                if (!TryMapQueryType(target, out QueryType queryType, out bool isOcclusion) || !isOcclusion)
                    return false;

                // The recorded command buffer already contains CmdResetQueryPool before
                // its first query begin, so replay establishes a new epoch in queue order.
                if (!EnsureQueryPool(queryType))
                    return false;

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                lock (_resultEpochLock)
                {
                    // Reuse is legal only after the prior epoch was consumed and only
                    // for a command buffer whose recorded view-slot metadata is known.
                    return !_submittedResultEpoch.IsValid &&
                        _recordedResultEpochs.ContainsKey(commandBufferHandle);
                }
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

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                lock (_resultEpochLock)
                {
                    if (_submittedResultEpoch.IsValid || commandBufferHandle == 0)
                        return false;

                    _recordedResultEpochs[commandBufferHandle] = new RecordedResultEpoch(
                        QueryCount: 1u,
                        ForceVisible: false);
                }

                // Track the query pool resource usage for the command buffer.
                Renderer.TrackVulkanCommandBufferResource(
                    commandBuffer,
                    ObjectType.QueryPool,
                    _queryPool.Handle,
                    "Query.Timestamp");

                // Reset the timestamp query in the command buffer before writing the new timestamp.
                Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, 1);
                Api.CmdWriteTimestamp(commandBuffer, stage, _queryPool, 0);
                _activeQueryCount = 1u;

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

                // A queued CmdResetQueryPool does not make the previous result unavailable
                // until the reset actually executes. Do not poll the pool until submission
                // completion proves that this epoch's reset, begin, draw, and end all ran.
                if (_queryPool.Handle == 0 || !TryGetSubmittedResultEpoch(out SubmittedResultEpoch epoch))
                    return false;
                if (!Renderer.IsVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle))
                {
                    if (!wait)
                        return false;

                    // Waiting on the query alone is insufficient: availability from the
                    // previous pool use remains observable until the queued reset executes.
                    // Wait only for the exact submission which produced this epoch; a
                    // device-wide idle would stall unrelated frame, eye, and probe work.
                    if (!Renderer.WaitForVulkanSubmissionCompletion(epoch.Submission, "query-result-epoch"))
                        return false;
                    if (!Renderer.IsVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle))
                        return false;
                }

                // A recorder-side scope interruption ended the Vulkan query without its
                // intended draw. Never turn that legal empty result into false occlusion.
                if (epoch.ForceVisible)
                {
                    if (!TryConsumeSubmittedResultEpoch(epoch.Serial))
                        return false;

                    result = 1ul;
                    return true;
                }

                // Read each value and its availability atomically. An unavailable value is
                // undefined without PARTIAL, so it must never drive a culling decision.
                QueryResultFlags flags = QueryResultFlags.Result64Bit |
                    QueryResultFlags.ResultWithAvailabilityBit;
                if (wait)
                    flags |= QueryResultFlags.ResultWaitBit;

                // Clamp the number of queries to retrieve to the valid range of the query pool.
                uint queryCount = Math.Clamp(epoch.QueryCount, 1u, _queryPoolCapacity);
                Span<ulong> data = stackalloc ulong[checked((int)queryCount * 2)];
                Result queryResult;
                fixed (ulong* pData = data)
                {
                    // Retrieve the query results from the Vulkan query pool into the allocated span.
                    queryResult = Api!.GetQueryPoolResults(
                        Device,
                        _queryPool,
                        0,
                        queryCount,
                        checked((nuint)(queryCount * sizeof(ulong) * 2u)),
                        pData,
                        (ulong)(sizeof(ulong) * 2),
                        flags);
                }

                if (queryResult == Result.Success || queryResult == Result.NotReady)
                {
                    if (!TryDecodeOcclusionResult(data, out result))
                        return false;
                    if (!TryConsumeSubmittedResultEpoch(epoch.Serial))
                        return false;

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
                if (_queryPool.Handle == 0 || !TryGetSubmittedResultEpoch(out SubmittedResultEpoch epoch))
                    return false;
                if (!Renderer.IsVulkanResourceUseCompleted(ObjectType.QueryPool, _queryPool.Handle))
                    return true;

                if (epoch.ForceVisible)
                {
                    available = true;
                    return true;
                }

                // Determine the number of queries to retrieve, clamping it to the valid range.
                uint queryCount = Math.Clamp(epoch.QueryCount, 1u, _queryPoolCapacity);
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
                        available = TryDecodeOcclusionResult(data, out _);

                        // Return true to indicate that the query results were successfully retrieved (or are not yet ready).
                        return true;
                    }

                    // If the query results retrieval was not successful and the results are not yet ready, return false.
                    return false;
                }
            }

            /// <summary>
            /// Decodes Vulkan's interleaved 64-bit result/availability pairs. Every
            /// multiview slot must be available before the epoch can resolve; visibility
            /// is the OR of all slot results.
            /// </summary>
            internal static bool TryDecodeOcclusionResult(
                ReadOnlySpan<ulong> resultAndAvailability,
                out ulong result)
            {
                result = 0ul;
                if (resultAndAvailability.Length == 0 ||
                    (resultAndAvailability.Length & 1) != 0)
                {
                    return false;
                }

                bool anyVisible = false;
                for (int index = 0; index < resultAndAvailability.Length; index += 2)
                {
                    if (resultAndAvailability[index + 1] == 0ul)
                        return false;
                    if (resultAndAvailability[index] != 0ul)
                        anyVisible = true;
                }

                result = anyVisible ? 1ul : 0ul;
                return true;
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

                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                lock (_resultEpochLock)
                {
                    if (_submittedResultEpoch.IsValid || commandBufferHandle == 0)
                        return false;

                    _recordedResultEpochs[commandBufferHandle] = new RecordedResultEpoch(
                        QueryCount: 1u,
                        ForceVisible: false);
                }

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

                // Reset query metadata. Keep the serial monotonic so a racing stale
                // consumer cannot match an epoch created after pool recreation.
                _queryPoolCapacity = 0u;
                _activeQueryCount = 1u;
                lock (_resultEpochLock)
                {
                    _submittedResultEpoch = default;
                    _recordedResultEpochs.Clear();
                }

                // Mark the query as inactive and clear the current query data.
                _queryActive = false;
                Data.CurrentQuery = null;
            }

            internal void InvalidateRecordedResultEpoch(CommandBuffer commandBuffer)
            {
                ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
                if (commandBufferHandle == 0)
                    return;

                lock (_resultEpochLock)
                {
                    if (_recordedResultEpochs.TryGetValue(commandBufferHandle, out RecordedResultEpoch epoch))
                    {
                        _recordedResultEpochs[commandBufferHandle] = epoch with
                        {
                            ForceVisible = true,
                        };
                    }
                    else
                    {
                        _recordedResultEpochs[commandBufferHandle] = new RecordedResultEpoch(
                            Math.Max(_activeQueryCount, 1u),
                            ForceVisible: true);
                    }
                }
            }

            private void MarkResultEpochSubmitted(
                ulong commandBufferHandle,
                in VulkanLifetimeSubmission submission)
            {
                lock (_resultEpochLock)
                {
                    bool overlappingEpoch = _submittedResultEpoch.IsValid;
                    if (!_recordedResultEpochs.TryGetValue(commandBufferHandle, out RecordedResultEpoch recordedEpoch))
                    {
                        recordedEpoch = new RecordedResultEpoch(
                            QueryCount: 1u,
                            ForceVisible: true);
                    }

                    ulong serial = ++_nextSubmittedResultEpochSerial;
                    if (serial == 0ul)
                        serial = ++_nextSubmittedResultEpochSerial;

                    _submittedResultEpoch = new SubmittedResultEpoch(
                        serial,
                        commandBufferHandle,
                        Math.Clamp(recordedEpoch.QueryCount, 1u, Math.Max(_queryPoolCapacity, 1u)),
                        recordedEpoch.ForceVisible || overlappingEpoch,
                        submission);
                }
            }

            private bool TryGetSubmittedResultEpoch(out SubmittedResultEpoch epoch)
            {
                lock (_resultEpochLock)
                {
                    epoch = _submittedResultEpoch;
                    return epoch.IsValid;
                }
            }

            private bool TryConsumeSubmittedResultEpoch(ulong serial)
            {
                lock (_resultEpochLock)
                {
                    if (!_submittedResultEpoch.IsValid ||
                        _submittedResultEpoch.Serial != serial)
                    {
                        return false;
                    }

                    _submittedResultEpoch = default;
                    return true;
                }
            }

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
