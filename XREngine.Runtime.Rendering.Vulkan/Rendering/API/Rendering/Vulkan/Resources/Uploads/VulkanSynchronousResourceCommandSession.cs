using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One-shot graphics command buffer with an explicit fence-complete lifetime
/// receipt. It is a scope, not a callback: wrapper code encodes directly through
/// <see cref="Encoder"/>.
/// </summary>
internal unsafe sealed class VulkanSynchronousResourceCommandSession : IDisposable
{
    private readonly VulkanBackendObjectContext _context;
    private readonly VulkanCommandRuntime _commands;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly CommandPool _pool;
    private readonly string _owner;
    private bool _completed;
    private bool _nativeSubmissionAccepted;
    private bool _commandBufferReleased;

    internal VulkanSynchronousResourceCommandSession(
        VulkanBackendObjectContext context,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry,
        string owner)
    {
        _context = context;
        _commands = commands;
        _telemetry = telemetry;
        _owner = owner;
        _pool = commands.GetThreadGraphicsCommandPool(context.Api, context.DeviceContext, resources);
        CommandBuffer = commands.AllocateTrackedCommandBuffer(
            context.Api,
            context.DeviceContext,
            resources,
            _pool,
            CommandBufferLevel.Primary,
            owner);
        Encoder = new VulkanTrackedCommandEncoder(commands);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result;
        try
        {
            result = commands.BeginTrackedCommandBuffer(
                CommandBuffer,
                ref beginInfo,
                owner);
            context.DeviceContext.ObserveNativeResult($"vkBeginCommandBuffer.{owner}", result);
        }
        catch
        {
            AbandonAndReleaseCommandBuffer();
            throw;
        }

        if (result != Result.Success)
        {
            AbandonAndReleaseCommandBuffer();
            throw new InvalidOperationException($"Failed to begin synchronous resource command buffer ({result}).");
        }
    }

    internal CommandBuffer CommandBuffer { get; }
    internal VulkanTrackedCommandEncoder Encoder { get; }

    /// <summary>
    /// Gets whether the graphics queue accepted the submission. After that, the GPU runs the
    /// recorded commands even if the fence wait or the completion bookkeeping then fails.
    /// </summary>
    internal bool NativeSubmissionAccepted => _nativeSubmissionAccepted;

    internal void CompleteAndWait()
        => CompleteAndWait(null, default);

    internal void CompleteAndWait(
        VulkanFrameDataArena? arena,
        in VulkanFrameDataSlice slice)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (Encoder.End(CommandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to end synchronous resource command buffer.");

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result = _context.Api.CreateFence(_context.Device, ref fenceInfo, null, out Fence fence);
        _context.DeviceContext.ObserveNativeResult($"vkCreateFence.{_owner}", result);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create synchronous resource fence ({result}).");

        bool arenaPrepared = false;
        bool arenaSubmitted = false;
        bool debtRetired = false;
        try
        {
            if (arena is not null)
            {
                if (!slice.IsValid || slice.ArenaIdentity != arena.Identity ||
                    !arena.TryPrepareFrameSlotForSubmission(0, slice.Generation))
                {
                    throw new InvalidOperationException(
                        "Failed to prepare the synchronous frame-data slice for submission.");
                }
                arenaPrepared = true;
            }

            CommandBuffer commandBuffer = CommandBuffer;
            SubmitInfo submit = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            VulkanSubmissionDiagnosticContext diagnosticContext = default;
            VulkanSubmissionReceipt receipt =
                _commands.SubmitToQueueTrackedWithDisposition(
                _context.DeviceContext.GraphicsQueue,
                ref submit,
                fence,
                in diagnosticContext,
                out _,
                out _,
                _owner);
            if (!receipt.SubmissionAccepted)
                throw new InvalidOperationException($"Failed to submit synchronous resource command ({receipt.Result}).");
            _nativeSubmissionAccepted = true;

            if (arena is not null)
            {
                arena.MarkFrameSlotSubmitted(0, slice.Generation);
                arenaSubmitted = true;
            }

            Fence* fencePtr = &fence;
            result = _context.Api.WaitForFences(_context.Device, 1, fencePtr, true, ulong.MaxValue);
            _context.DeviceContext.ObserveNativeResult($"vkWaitForFences.{_owner}", result);
            if (result != Result.Success)
            {
                debtRetired = true;
                _commands.RetireIncompleteSynchronousSubmission(
                    CommandBuffer,
                    _pool,
                    fence,
                    arena,
                    in slice,
                    removeOneTimeOwner: false,
                    _owner,
                    completeSynchronousLifetime: true);
                throw new InvalidOperationException($"Failed to wait for synchronous resource command ({result}).");
            }
            try
            {
                if (!receipt.LifetimePinsTransferred)
                    _commands.ReleaseSubmissionResourceLifetimePins(ref submit);
                _commands.CompleteTrackedFence(fence);
                if (arena is not null &&
                    !arena.TryResetFrameSlot(0, slice.Generation, submissionCompletionProven: true))
                {
                    throw new InvalidOperationException(
                        "The synchronous frame-data slot could not be reopened after fence completion.");
                }
            }
            catch
            {
                debtRetired = true;
                _commands.RetireIncompleteSynchronousSubmission(
                    CommandBuffer,
                    _pool,
                    fence,
                    arena,
                    in slice,
                    removeOneTimeOwner: false,
                    _owner,
                    completeSynchronousLifetime: true);
                throw;
            }
            _completed = true;
        }
        finally
        {
            if (arenaPrepared && !arenaSubmitted && arena is not null)
                _ = arena.TryCancelFrameSlotSubmission(0, slice.Generation);
            if (!debtRetired)
                _context.Api.DestroyFence(_context.Device, fence, null);
            if (_completed)
                ReleaseCommandBuffer();
        }
    }

    /// <summary>
    /// Discards and frees the command buffer when the session ends before the queue accepted it.
    /// It does nothing after a completed session, or after an accepted submission, because
    /// completion or debt retirement then owns the command buffer.
    /// </summary>
    /// <remarks>
    /// A <c>using</c> scope also calls this method when a recording exception leaves the scope.
    /// So this method logs cleanup failures and does not throw, and the caller gets the original exception.
    /// </remarks>
    public void Dispose()
    {
        if (!_completed && !_nativeSubmissionAccepted)
            AbandonAndReleaseCommandBuffer();
    }

    /// <summary>
    /// Discards the recording state of the command buffer, then frees the command buffer.
    /// Use this only before the queue accepted the command buffer. It logs failures and does not throw.
    /// </summary>
    private void AbandonAndReleaseCommandBuffer()
    {
        if (_commandBufferReleased)
            return;

        try
        {
            // The free rejects a command buffer whose tracking batch still records.
            // Abandon removes that batch and the recorded dependencies first.
            Encoder.Abandon(CommandBuffer);
            ReleaseCommandBuffer();
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Could not free synchronous resource command buffer 0x{0:X} for {1} after the session ended before submission: {2}: {3}",
                unchecked((ulong)CommandBuffer.Handle),
                _owner,
                exception.GetType().Name,
                exception.Message);
        }
    }

    private void ReleaseCommandBuffer()
    {
        if (_commandBufferReleased)
            return;

        CommandBuffer commandBuffer = CommandBuffer;
        _commands.FreeCompletedSynchronousCommandBuffer(
            _pool,
            ref commandBuffer,
            _owner);
        // Set the flag only after the free returns. A failed free keeps the
        // command buffer owned by this session, so a later dispose can try again.
        _commandBufferReleased = true;
    }
}
