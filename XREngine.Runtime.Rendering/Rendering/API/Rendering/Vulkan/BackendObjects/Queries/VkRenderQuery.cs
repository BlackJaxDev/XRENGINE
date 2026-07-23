using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Lightweight Vulkan query handle backed by a renderer-owned arena range.
    /// </summary>
    public sealed class VkRenderQuery(VulkanRenderer api, XRRenderQuery data) : VkObject<XRRenderQuery>(api, data)
    {
        private readonly record struct RecordedEpoch(
            RenderQueryTicket Ticket,
            bool ForceVisible,
            ERenderQuerySlotState State);

        private readonly record struct SubmittedEpoch(
            RenderQueryTicket Ticket,
            ulong CommandBufferHandle,
            bool ForceVisible,
            VulkanLifetimeSubmission Submission,
            ERenderQuerySlotState State)
        {
            public bool IsValid => Ticket.IsValid;
        }

        private readonly object _epochLock = new();
        private readonly Dictionary<ulong, RecordedEpoch> _recordedEpochs = [];
        private VulkanQueryPoolAllocation _allocation;
        private VulkanQueryPlan _plan;
        private SubmittedEpoch _submittedEpoch;
        private RenderQueryTicket _latestTicket;
        private ulong _nextEpoch;
        private ulong _activeCommandBufferHandle;
        private bool _queryActive;
        private bool _destroyRequested;

        public override VkObjectType Type => VkObjectType.Query;
        public override bool IsGenerated => IsActive;
        public RenderQueryTicket Ticket => _latestTicket;
        public RenderQueryResultLayout ResultLayout => _plan.ResultLayout;

        protected override uint CreateObjectInternal() => CacheObject(this);

        protected override void DeleteObjectInternal()
        {
            _destroyRequested = true;
            lock (_epochLock)
            {
                if (!_submittedEpoch.IsValid && _recordedEpochs.Count == 0)
                    ReleaseAllocationNoLock();
            }
            RemoveCachedObject(BindingId);
        }

        protected override void LinkData()
        {
        }

        protected override void UnlinkData()
            => _queryActive = false;

        /// <summary>
        /// Records an execution-ordered reset and allocates an immutable result epoch.
        /// </summary>
        internal bool PrepareForRecording(CommandBuffer commandBuffer, uint viewSlotCount = 1u)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            if (commandBufferHandle == 0ul || Renderer.IsDeviceLost)
                return false;

            VulkanQueryPlan plan = VulkanQueryDescriptorMapper.Map(
                Data.Descriptor,
                Renderer.QueryCapabilities,
                viewSlotCount);
            if (!plan.Supported)
            {
                RenderQueryTelemetry.RecordUnsupported();
                Debug.VulkanWarningEvery(
                    $"Vulkan.RenderQuery.Unsupported.{Data.Descriptor.GetHashCode()}",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan.Query] Descriptor rejected before recording. descriptor={0} reason={1}",
                    Data.Descriptor,
                    plan.UnsupportedReason ?? "unspecified");
                return false;
            }

            lock (_epochLock)
            {
                if (_destroyRequested || _submittedEpoch.IsValid ||
                    (_recordedEpochs.Count != 0 && !_recordedEpochs.ContainsKey(commandBufferHandle)))
                    return false;
                if (!EnsureAllocationNoLock(plan))
                    return false;

                ulong epoch = ++_nextEpoch;
                if (epoch == 0ul)
                    epoch = ++_nextEpoch;
                RenderQueryTicket ticket = new(
                    epoch,
                    _allocation.PoolIdentity,
                    _allocation.FirstQuery,
                    plan.ResultLayout.QueryCount,
                    0ul);

                _plan = plan;
                _latestTicket = ticket;
                _recordedEpochs[commandBufferHandle] = new(ticket, false, ERenderQuerySlotState.ResetRecorded);
                _queryActive = false;
                _activeCommandBufferHandle = 0ul;

                TrackPool(commandBuffer, "Query.Reset");
                Api!.CmdResetQueryPool(
                    commandBuffer,
                    _allocation.Pool,
                    _allocation.FirstQuery,
                    ticket.QueryCount);
                Renderer.QueryPoolArenas.RecordResetEpoch();
                return true;
            }
        }

        /// <summary>
        /// Re-arms a cached command buffer. The recorded reset remains the queue-
        /// ordered epoch boundary; no host reset or global wait is used.
        /// </summary>
        internal bool PrepareForCommandBufferReuse(CommandBuffer commandBuffer)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (_destroyRequested || _submittedEpoch.IsValid || !_allocation.IsValid ||
                    !_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch recorded))
                {
                    return false;
                }

                ulong epoch = ++_nextEpoch;
                if (epoch == 0ul)
                    epoch = ++_nextEpoch;
                RenderQueryTicket ticket = recorded.Ticket with
                {
                    Epoch = epoch,
                    SubmissionValue = 0ul,
                };
                _latestTicket = ticket;
                _recordedEpochs[commandBufferHandle] = new(ticket, false, ERenderQuerySlotState.ResetRecorded);
                return true;
            }
        }

        public ERenderQueryReadStatus BeginQuery(CommandBuffer commandBuffer)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (_queryActive || !_allocation.IsValid ||
                    !_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch epoch) ||
                    epoch.State != ERenderQuerySlotState.ResetRecorded)
                {
                    return ERenderQueryReadStatus.InvalidState;
                }

                TrackPool(commandBuffer, "Query.Begin");
                switch (_plan.Provider)
                {
                    case EVulkanQueryRecordingProvider.BeginEnd:
                        Api!.CmdBeginQuery(
                            commandBuffer,
                            _allocation.Pool,
                            _allocation.FirstQuery,
                            _plan.ControlFlags);
                        break;
                    case EVulkanQueryRecordingProvider.TransformFeedbackIndexed:
                    case EVulkanQueryRecordingProvider.PrimitivesGeneratedIndexed:
                        if (Renderer._extTransformFeedback is null)
                            return ERenderQueryReadStatus.Unsupported;
                        Renderer._extTransformFeedback.CmdBeginQueryIndexed(
                            commandBuffer,
                            _allocation.Pool,
                            _allocation.FirstQuery,
                            _plan.ControlFlags,
                            Data.Descriptor.StreamIndex);
                        break;
                    default:
                        return ERenderQueryReadStatus.InvalidState;
                }

                _queryActive = true;
                RenderQueryTelemetry.RecordRecording(Data.Descriptor.Kind);
                _activeCommandBufferHandle = commandBufferHandle;
                _recordedEpochs[commandBufferHandle] = epoch with { State = ERenderQuerySlotState.Recording };
                return ERenderQueryReadStatus.Ready;
            }
        }

        public ERenderQueryReadStatus EndQuery(CommandBuffer commandBuffer)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (!_queryActive || commandBufferHandle != _activeCommandBufferHandle ||
                    !_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch epoch) ||
                    epoch.State != ERenderQuerySlotState.Recording)
                {
                    return ERenderQueryReadStatus.InvalidState;
                }

                TrackPool(commandBuffer, "Query.End");
                if (_plan.Provider is EVulkanQueryRecordingProvider.TransformFeedbackIndexed or
                    EVulkanQueryRecordingProvider.PrimitivesGeneratedIndexed)
                {
                    if (Renderer._extTransformFeedback is null)
                        return ERenderQueryReadStatus.Unsupported;
                    Renderer._extTransformFeedback.CmdEndQueryIndexed(
                        commandBuffer,
                        _allocation.Pool,
                        _allocation.FirstQuery,
                        Data.Descriptor.StreamIndex);
                }
                else
                {
                    Api!.CmdEndQuery(commandBuffer, _allocation.Pool, _allocation.FirstQuery);
                }

                _queryActive = false;
                _activeCommandBufferHandle = 0ul;
                _recordedEpochs[commandBufferHandle] = epoch with { State = ERenderQuerySlotState.Ended };
                return ERenderQueryReadStatus.Ready;
            }
        }

        public ERenderQueryReadStatus WriteTimestamp(
            CommandBuffer commandBuffer,
            PipelineStageFlags2 stage,
            uint pointIndex = 0u)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (!_allocation.IsValid ||
                    !_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch epoch) ||
                    epoch.State is not (ERenderQuerySlotState.ResetRecorded or ERenderQuerySlotState.Recording) ||
                    _plan.Provider != EVulkanQueryRecordingProvider.Timestamp ||
                    pointIndex >= epoch.Ticket.QueryCount)
                {
                    return ERenderQueryReadStatus.InvalidState;
                }

                if (!VulkanQueryDescriptorMapper.IsTimestampStageSupported((ulong)stage, Renderer.QueryCapabilities))
                    return ERenderQueryReadStatus.Unsupported;

                TrackPool(commandBuffer, "Query.Timestamp");
                uint queryIndex = _allocation.FirstQuery + pointIndex;
                if (Renderer.QueryCapabilities.Synchronization2Enabled)
                    Renderer.CmdWriteTimestamp2Compat(commandBuffer, stage, _allocation.Pool, queryIndex);
                else
                    Api!.CmdWriteTimestamp(commandBuffer, (PipelineStageFlags)(ulong)stage, _allocation.Pool, queryIndex);
                RenderQueryTelemetry.RecordRecording(Data.Descriptor.Kind);

                bool ended = Data.Descriptor.Kind == ERenderQueryKind.Timestamp ||
                    pointIndex + 1u == epoch.Ticket.QueryCount;
                _recordedEpochs[commandBufferHandle] = epoch with
                {
                    State = ended ? ERenderQuerySlotState.Ended : ERenderQuerySlotState.Recording,
                };
                return ERenderQueryReadStatus.Ready;
            }
        }

        public ERenderQueryReadStatus WriteProperties(
            CommandBuffer commandBuffer,
            ReadOnlySpan<ulong> sourceHandles)
        {
            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (!_allocation.IsValid ||
                    !_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch epoch) ||
                    epoch.State != ERenderQuerySlotState.ResetRecorded ||
                    !Renderer.TryGetSpecializedQueryProvider(Data.Descriptor.Kind, out IVulkanSpecializedQueryProvider provider))
                {
                    return ERenderQueryReadStatus.SubsystemUnavailable;
                }
                if (!provider.HasRequiredExternalOwnership)
                    return ERenderQueryReadStatus.InvalidState;

                TrackPool(commandBuffer, "Query.Properties");
                if (!provider.TryRecord(
                        Renderer,
                        commandBuffer,
                        _allocation.Pool,
                        _allocation.FirstQuery,
                        Data.Descriptor,
                        sourceHandles,
                        out string? reason))
                {
                    Debug.VulkanWarning("[Vulkan.Query] Specialized provider rejected recording: {0}", reason ?? "unspecified");
                    return ERenderQueryReadStatus.ApiError;
                }

                _recordedEpochs[commandBufferHandle] = epoch with { State = ERenderQuerySlotState.Ended };
                return ERenderQueryReadStatus.Ready;
            }
        }

        public ERenderQueryReadStatus CopyResults(
            CommandBuffer commandBuffer,
            Silk.NET.Vulkan.Buffer destination,
            ulong destinationOffset,
            ulong stride,
            bool includeAvailability = true)
        {
            if (!_allocation.IsValid || destination.Handle == 0)
                return ERenderQueryReadStatus.InvalidState;

            ulong minimumStride = _plan.ResultLayout.ValuesPerQuery * sizeof(ulong) +
                (includeAvailability ? sizeof(ulong) : 0u);
            if (stride < minimumStride || (destinationOffset & 7ul) != 0ul || (stride & 7ul) != 0ul)
                return ERenderQueryReadStatus.InvalidState;

            QueryResultFlags flags = QueryResultFlags.Result64Bit;
            if (includeAvailability)
                flags |= QueryResultFlags.ResultWithAvailabilityBit;
            TrackPool(commandBuffer, "Query.CopyResults");
            Api!.CmdCopyQueryPoolResults(
                commandBuffer,
                _allocation.Pool,
                _allocation.FirstQuery,
                _plan.ResultLayout.QueryCount,
                destination,
                destinationOffset,
                stride,
                flags);
            RenderQueryTelemetry.RecordCopiedBytes(checked((long)(_plan.ResultLayout.NativeStrideBytes * _plan.ResultLayout.QueryCount)));
            return ERenderQueryReadStatus.Ready;
        }

        public RenderQueryReadResult TryReadRaw(
            Span<ulong> destination,
            in RenderQueryTicket expectedTicket = default,
            bool wait = false,
            string? waitingCaller = null)
        {
            SubmittedEpoch epoch;
            lock (_epochLock)
                epoch = _submittedEpoch;

            RenderQueryResultLayout layout = _plan.ResultLayout;
            if (Renderer.IsDeviceLost)
                return new(ERenderQueryReadStatus.DeviceLost, epoch.Ticket, layout, 0, "The Vulkan device is lost.");
            if (!epoch.IsValid)
                return new(ERenderQueryReadStatus.InvalidState, _latestTicket, layout, 0, "No submitted query epoch is pending.");
            if (expectedTicket.IsValid && expectedTicket != epoch.Ticket)
                return new(ERenderQueryReadStatus.StaleTicket, epoch.Ticket, layout, 0, "The requested epoch does not own the pending result.");
            if (!layout.FitsNativeResult(destination.Length))
                return new(ERenderQueryReadStatus.BufferTooSmall, epoch.Ticket, layout, 0, "Caller storage must include native availability words.");

            if (!Renderer.IsVulkanSubmissionCompleted(epoch.Submission))
            {
                if (!wait)
                {
                    RenderQueryTelemetry.RecordRead(ERenderQueryReadStatus.NotReady);
                    return new(ERenderQueryReadStatus.NotReady, epoch.Ticket, layout, 0);
                }
                if (string.IsNullOrWhiteSpace(waitingCaller))
                    return new(ERenderQueryReadStatus.InvalidState, epoch.Ticket, layout, 0, "Explicit waits require a diagnostic caller name.");
                RenderQueryTelemetry.RecordWait();
                if (!Renderer.WaitForVulkanSubmissionCompletion(epoch.Submission, $"query:{waitingCaller}"))
                    return new(Renderer.IsDeviceLost ? ERenderQueryReadStatus.DeviceLost : ERenderQueryReadStatus.ApiError, epoch.Ticket, layout, 0);
            }

            if (epoch.ForceVisible && Data.Descriptor.Kind == ERenderQueryKind.Occlusion)
            {
                destination[0] = 1ul;
                if (!TryConsumeSubmittedEpoch(epoch.Ticket.Epoch))
                    return new(ERenderQueryReadStatus.StaleTicket, epoch.Ticket, layout, 0);
                return new(ERenderQueryReadStatus.Ready, epoch.Ticket, layout, 1);
            }

            QueryResultFlags flags = QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit;
            if (wait)
                flags |= QueryResultFlags.ResultWaitBit;

            Result result;
            fixed (ulong* data = destination)
            {
                result = Api!.GetQueryPoolResults(
                    Device,
                    _allocation.Pool,
                    epoch.Ticket.FirstQuery,
                    epoch.Ticket.QueryCount,
                    layout.NativeSizeBytes,
                    data,
                    layout.NativeStrideBytes,
                    flags);
            }

            if (result is not (Result.Success or Result.NotReady))
            {
                if (result == Result.ErrorDeviceLost)
                    Renderer.MarkDeviceLost("vkGetQueryPoolResults returned ErrorDeviceLost");
                return new(
                    result == Result.ErrorDeviceLost ? ERenderQueryReadStatus.DeviceLost : ERenderQueryReadStatus.ApiError,
                    epoch.Ticket,
                    layout,
                    0,
                    $"vkGetQueryPoolResults returned {result}.");
            }

            if (!CompactAvailableValues(destination, layout))
            {
                RenderQueryTelemetry.RecordRead(ERenderQueryReadStatus.NotReady);
                return new(ERenderQueryReadStatus.NotReady, epoch.Ticket, layout, 0);
            }
            if (!TryConsumeSubmittedEpoch(epoch.Ticket.Epoch))
                return new(ERenderQueryReadStatus.StaleTicket, epoch.Ticket, layout, 0);

            RenderQueryTelemetry.RecordRead(ERenderQueryReadStatus.Ready);
            RenderQueryTelemetry.RecordHostReadBytes(checked((long)(layout.ValueCount * sizeof(ulong))));
            return new(ERenderQueryReadStatus.Ready, epoch.Ticket, layout, checked((int)layout.ValueCount));
        }

        public ERenderQueryReadStatus TryGetAnySamplesPassed(
            out OcclusionQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.Occlusion ||
                Data.Descriptor.OcclusionMode == EOcclusionResultMode.ExactSamplesPassed)
            {
                return ERenderQueryReadStatus.InvalidState;
            }

            Span<ulong> values = stackalloc ulong[checked((int)Math.Max(_plan.ResultLayout.NativeValueCount, 2u))];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;

            bool any = false;
            for (int index = 0; index < read.ValuesWritten; index++)
                any |= values[index] != 0ul;
            result = new(any, any ? 1ul : 0ul, read.Layout.ViewSlotCount);
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetExactSamplesPassed(
            out OcclusionQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.Occlusion ||
                Data.Descriptor.OcclusionMode != EOcclusionResultMode.ExactSamplesPassed)
            {
                return ERenderQueryReadStatus.InvalidState;
            }

            Span<ulong> values = stackalloc ulong[checked((int)Math.Max(_plan.ResultLayout.NativeValueCount, 2u))];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;

            ulong samples = 0ul;
            for (int index = 0; index < read.ValuesWritten; index++)
                samples = ulong.MaxValue - samples < values[index] ? ulong.MaxValue : samples + values[index];
            result = new(samples != 0ul, samples, read.Layout.ViewSlotCount);
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetTimestamp(
            out TimestampQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.Timestamp)
                return ERenderQueryReadStatus.InvalidState;

            Span<ulong> values = stackalloc ulong[2];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;

            ulong ticks = RenderQueryTimestampMath.MaskTicks(values[0], Renderer.QueryCapabilities.GraphicsTimestampValidBits);
            result = new(ticks, RenderQueryTimestampMath.TicksToNanoseconds(ticks, Renderer.QueryCapabilities.TimestampPeriodNanoseconds));
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetElapsedTime(
            out ElapsedTimeQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.ElapsedTime)
                return ERenderQueryReadStatus.InvalidState;

            Span<ulong> values = stackalloc ulong[4];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;

            uint validBits = Renderer.QueryCapabilities.GraphicsTimestampValidBits;
            ulong start = RenderQueryTimestampMath.MaskTicks(values[0], validBits);
            ulong end = RenderQueryTimestampMath.MaskTicks(values[1], validBits);
            ulong delta = RenderQueryTimestampMath.DeltaTicks(start, end, validBits);
            result = new(start, end, RenderQueryTimestampMath.TicksToNanoseconds(delta, Renderer.QueryCapabilities.TimestampPeriodNanoseconds));
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetTransformFeedback(
            out TransformFeedbackQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.TransformFeedback)
                return ERenderQueryReadStatus.InvalidState;

            Span<ulong> values = stackalloc ulong[3];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;
            result = new(values[0], values[1]);
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetPrimitivesGenerated(
            out PrimitivesGeneratedQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind is not (ERenderQueryKind.PrimitivesGenerated or ERenderQueryKind.MeshPrimitivesGenerated))
                return ERenderQueryReadStatus.InvalidState;

            Span<ulong> values = stackalloc ulong[2];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;
            result = new(values[0]);
            return ERenderQueryReadStatus.Ready;
        }

        public ERenderQueryReadStatus TryGetPipelineStatistics(
            out PipelineStatisticsQueryResult result,
            in RenderQueryTicket expectedTicket = default)
        {
            result = default;
            if (Data.Descriptor.Kind != ERenderQueryKind.PipelineStatistics)
                return ERenderQueryReadStatus.InvalidState;

            Span<ulong> values = stackalloc ulong[14];
            RenderQueryReadResult read = TryReadRaw(values, expectedTicket);
            if (!read.IsReady)
                return read.Status;

            Span<ulong> decoded = stackalloc ulong[13];
            uint source = 0u;
            for (uint bit = 0u; bit < 13u; bit++)
            {
                if (((uint)Data.Descriptor.Statistics & (1u << (int)bit)) != 0u)
                    decoded[(int)bit] = values[(int)source++];
            }
            result = new(
                Data.Descriptor.Statistics,
                decoded[0], decoded[1], decoded[2], decoded[3], decoded[4], decoded[5], decoded[6],
                decoded[7], decoded[8], decoded[9], decoded[10], decoded[11], decoded[12]);
            return ERenderQueryReadStatus.Ready;
        }

        internal void InvalidateRecordedResultEpoch(CommandBuffer commandBuffer)
        {
            ulong handle = unchecked((ulong)commandBuffer.Handle);
            lock (_epochLock)
            {
                if (_recordedEpochs.TryGetValue(handle, out RecordedEpoch epoch))
                    _recordedEpochs[handle] = epoch with { ForceVisible = true };
            }
        }

        internal void MarkResultEpochSubmitted(
            ulong commandBufferHandle,
            in VulkanLifetimeSubmission submission)
        {
            lock (_epochLock)
            {
                if (!_recordedEpochs.TryGetValue(commandBufferHandle, out RecordedEpoch recorded))
                    return;

                if (_submittedEpoch.IsValid)
                {
                    _submittedEpoch = _submittedEpoch with { ForceVisible = true };
                    Debug.VulkanWarningEvery(
                        $"Vulkan.RenderQuery.OverlappingSubmission.{_allocation.PoolIdentity}.{_allocation.FirstQuery}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan.Query] Rejected overlapping submission ownership. pool={0} firstQuery={1} existingEpoch={2} rejectedCommandBuffer=0x{3:X}",
                        _allocation.PoolIdentity,
                        _allocation.FirstQuery,
                        _submittedEpoch.Ticket.Epoch,
                        commandBufferHandle);
                    return;
                }

                RenderQueryTicket submittedTicket = recorded.Ticket with
                {
                    SubmissionValue = submission.QueueSequence,
                };
                _latestTicket = submittedTicket;
                _submittedEpoch = new(
                    submittedTicket,
                    commandBufferHandle,
                    recorded.ForceVisible,
                    submission,
                    ERenderQuerySlotState.Submitted);
                _recordedEpochs[commandBufferHandle] = recorded with { State = ERenderQuerySlotState.Submitted };
            }
        }

        internal static bool TryDecodeOcclusionResult(
            ReadOnlySpan<ulong> resultAndAvailability,
            out ulong result)
        {
            result = 0ul;
            if (resultAndAvailability.Length == 0 || (resultAndAvailability.Length & 1) != 0)
                return false;

            bool anySamplesPassed = false;
            for (int index = 0; index < resultAndAvailability.Length; index += 2)
            {
                if (resultAndAvailability[index + 1] == 0ul)
                    return false;
                if (resultAndAvailability[index] != 0ul)
                    anySamplesPassed = true;
            }
            result = anySamplesPassed ? 1ul : 0ul;
            return true;
        }

        internal static uint ResolveOcclusionQueryViewSlotCount(uint viewMask)
            => viewMask == 0u ? 1u : (uint)System.Numerics.BitOperations.PopCount(viewMask);

        private bool EnsureAllocationNoLock(in VulkanQueryPlan plan)
        {
            uint queryCount = plan.ResultLayout.QueryCount;
            VulkanQueryPoolKey key = new(
                plan.QueryType,
                plan.PipelineStatistics,
                plan.Provider,
                Renderer.QueryCapabilities.GraphicsQueueFamily,
                plan.ResultLayout.ValuesPerQuery,
                Data.Descriptor.Property);

            if (_allocation.IsValid && _allocation.Key == key && _allocation.QueryCount >= queryCount)
                return true;
            if (_allocation.IsValid)
                ReleaseAllocationNoLock();

            if (!Renderer.QueryPoolArenas.TryAllocate(key, queryCount, out _allocation, out string? reason))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.QueryArena.Exhausted.{key.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan.Query] Arena allocation failed. descriptor={0} count={1} reason={2}",
                    Data.Descriptor,
                    queryCount,
                    reason ?? "unspecified");
                return false;
            }

            Renderer.RegisterVulkanRenderQuery(_allocation.Pool, this);
            RenderQueryTelemetry.RecordAllocation();
            return true;
        }

        private void ReleaseAllocationNoLock()
        {
            if (!_allocation.IsValid)
                return;
            Renderer.UnregisterVulkanRenderQuery(_allocation.Pool, this);
            Renderer.QueryPoolArenas.Release(_allocation);
            RenderQueryTelemetry.RecordRelease();
            _allocation = default;
            _plan = default;
            _recordedEpochs.Clear();
            _latestTicket = default;
        }

        private void TrackPool(CommandBuffer commandBuffer, string operation)
            => Renderer.TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.QueryPool,
                _allocation.Pool.Handle,
                operation);

        private bool TryConsumeSubmittedEpoch(ulong epoch)
        {
            lock (_epochLock)
            {
                if (!_submittedEpoch.IsValid || _submittedEpoch.Ticket.Epoch != epoch)
                    return false;

                ulong commandBufferHandle = _submittedEpoch.CommandBufferHandle;
                _submittedEpoch = default;
                _recordedEpochs.Remove(commandBufferHandle);
                if (_destroyRequested)
                    ReleaseAllocationNoLock();
                return true;
            }
        }

        private static bool CompactAvailableValues(
            Span<ulong> nativeValues,
            in RenderQueryResultLayout layout)
        {
            uint sourceStride = layout.NativeValuesPerQuery;
            uint destination = 0u;
            for (uint query = 0u; query < layout.QueryCount; query++)
            {
                uint source = query * sourceStride;
                uint availability = source + (uint)layout.AvailabilityValueOffset;
                if (nativeValues[(int)availability] == 0ul)
                    return false;

                for (uint value = 0u; value < layout.ValuesPerQuery; value++)
                    nativeValues[(int)destination++] = nativeValues[(int)(source + value)];
            }
            return true;
        }
    }
}
