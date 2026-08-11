using Silk.NET.Vulkan;
using System.Runtime.ExceptionServices;

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
    private readonly VulkanResourceRuntime _resources;
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
        _resources = resources;
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
        commands.ResetBindState(Encoder, CommandBuffer);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = context.Api.BeginCommandBuffer(CommandBuffer, ref beginInfo);
        context.DeviceContext.ObserveNativeResult($"vkBeginCommandBuffer.{owner}", result);
        if (result != Result.Success)
        {
            ReleaseCommandBuffer();
            throw new InvalidOperationException($"Failed to begin synchronous resource command buffer ({result}).");
        }
    }

    internal CommandBuffer CommandBuffer { get; }
    internal VulkanTrackedCommandEncoder Encoder { get; }

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

            Exception? publicationFailure = null;
            try
            {
                _resources.RecordSynchronousGraphicsSubmission(
                    CommandBuffer,
                    fence,
                    _context.DeviceContext.GraphicsQueue);
            }
            catch (Exception failure)
            {
                publicationFailure = failure;
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
            if (publicationFailure is not null)
                ExceptionDispatchInfo.Capture(publicationFailure).Throw();
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

    public void Dispose()
    {
        if (!_completed && !_nativeSubmissionAccepted)
            ReleaseCommandBuffer();
    }

    private void ReleaseCommandBuffer()
    {
        if (_commandBufferReleased)
            return;
        _commandBufferReleased = true;
        CommandBuffer commandBuffer = CommandBuffer;
        if (commandBuffer.Handle != 0)
            lock (_commands.Pools.Gate)
                _context.Api.FreeCommandBuffers(_context.Device, _pool, 1, ref commandBuffer);
        _resources.CompleteSynchronousCommandBuffer(CommandBuffer);
    }
}
