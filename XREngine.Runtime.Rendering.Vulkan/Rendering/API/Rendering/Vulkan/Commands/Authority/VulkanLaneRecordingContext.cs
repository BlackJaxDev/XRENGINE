using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-local recording context allocated per logical render lane and frame slot.
/// Provides zero-lock direct bind state tracking, pre-allocated buffers for image-access deltas,
/// and fast bitset/array dependency collection for steady primary and secondary recording.
/// </summary>
internal sealed class VulkanLaneRecordingContext
{
    public EVulkanAcceptedFrameLane Lane { get; }
    public int FrameSlot { get; }

    public CommandBuffer CommandBuffer { get; private set; }
    public ulong CommandBufferHandle => unchecked((ulong)CommandBuffer.Handle);
    public ulong RecordingGeneration { get; private set; }
    public bool IsActive { get; private set; }

    // Direct inlined bind state - zero lock, zero dictionary lookup!
    public CommandBufferBindState BindState;

    // Pre-allocated flat buffer for image-access deltas
    private VulkanImageAccessRangeDelta[] _imageAccessDeltas = new VulkanImageAccessRangeDelta[64];
    private int _imageAccessDeltaCount;

    // Pre-allocated flat buffer for queue ownership transfers
    private VulkanQueueOwnershipTransferRequirement[] _queueOwnershipTransfers = new VulkanQueueOwnershipTransferRequirement[8];
    private int _queueOwnershipTransferCount;

    // Pre-allocated flat buffer / compact set for tracked native resource lifetime keys
    private readonly HashSet<VulkanResourceLifetimeKey> _dependencies = new(128);

    public int DependencyCount => _dependencies.Count;
    public int ImageAccessDeltaCount => _imageAccessDeltaCount;

    public VulkanLaneRecordingContext(EVulkanAcceptedFrameLane lane, int frameSlot)
    {
        Lane = lane;
        FrameSlot = frameSlot;
    }

    public void Begin(CommandBuffer commandBuffer, ulong recordingGeneration)
    {
        CommandBuffer = commandBuffer;
        RecordingGeneration = recordingGeneration;
        BindState = new CommandBufferBindState
        {
            RecordingGeneration = recordingGeneration,
        };
        _imageAccessDeltaCount = 0;
        _queueOwnershipTransferCount = 0;
        _dependencies.Clear();
        IsActive = true;
    }

    public void End()
    {
        IsActive = false;
    }

    public void RecordDependency(VulkanResourceLifetimeKey key)
    {
        if (key.IsValid)
            _dependencies.Add(key);
    }

    public void RecordImageAccess(in VulkanImageAccessRangeDelta delta)
    {
        if (_imageAccessDeltaCount >= _imageAccessDeltas.Length)
            Array.Resize(ref _imageAccessDeltas, _imageAccessDeltas.Length * 2);

        _imageAccessDeltas[_imageAccessDeltaCount++] = delta;
    }

    public void RecordQueueOwnershipTransfer(in VulkanQueueOwnershipTransferRequirement transfer)
    {
        if (_queueOwnershipTransferCount >= _queueOwnershipTransfers.Length)
            Array.Resize(ref _queueOwnershipTransfers, _queueOwnershipTransfers.Length * 2);

        _queueOwnershipTransfers[_queueOwnershipTransferCount++] = transfer;
    }

    public bool ShouldBindPipeline(PipelineBindPoint bindPoint, Pipeline pipeline)
    {
        ulong handle = pipeline.Handle;
        if (bindPoint == PipelineBindPoint.Graphics)
        {
            if (BindState.GraphicsPipeline == handle)
                return false;
            BindState.GraphicsPipeline = handle;
            return true;
        }

        if (BindState.ComputePipeline == handle)
            return false;
        BindState.ComputePipeline = handle;
        return true;
    }

    public bool ShouldBindVertexBuffer(ulong signature)
    {
        if (BindState.VertexBufferSignature == signature)
            return false;
        BindState.VertexBufferSignature = signature;
        return true;
    }

    public bool ShouldBindIndexBuffer(Silk.NET.Vulkan.Buffer buffer, ulong offset, IndexType indexType)
    {
        ulong handle = buffer.Handle;
        if (BindState.IndexBuffer == handle &&
            BindState.IndexOffset == offset &&
            BindState.IndexType == indexType)
        {
            return false;
        }
        BindState.IndexBuffer = handle;
        BindState.IndexOffset = offset;
        BindState.IndexType = indexType;
        return true;
    }

    public bool ShouldSetViewportScissor(ulong signature)
    {
        if (BindState.HasViewportScissorState && BindState.ViewportScissorSignature == signature)
            return false;
        BindState.ViewportScissorSignature = signature;
        BindState.HasViewportScissorState = true;
        return true;
    }

    public bool ShouldBindDescriptorSets(PipelineBindPoint bindPoint, ulong signature)
    {
        if (bindPoint == PipelineBindPoint.Graphics)
        {
            if (BindState.GraphicsDescriptorSignature == signature)
                return false;
            BindState.GraphicsDescriptorSignature = signature;
            return true;
        }

        if (BindState.ComputeDescriptorSignature == signature)
            return false;
        BindState.ComputeDescriptorSignature = signature;
        return true;
    }

    public void InvalidateDescriptorHeapBindingState()
    {
        BindState.DescriptorHeapSignature = 0;
    }

    public void InvalidateDescriptorSetBindingState()
    {
        BindState.GraphicsDescriptorSignature = 0;
        BindState.ComputeDescriptorSignature = 0;
    }

    public void InvalidateStateAfterSecondaryExecution()
    {
        ulong gen = BindState.RecordingGeneration;
        BindState = new CommandBufferBindState
        {
            RecordingGeneration = gen,
        };
    }

    public VulkanSealedRecordingReceipt CreateReceipt(bool isSuccess)
    {
        VulkanResourceLifetimeKey[] deps = new VulkanResourceLifetimeKey[_dependencies.Count];
        _dependencies.CopyTo(deps);

        VulkanImageAccessRangeDelta[] deltas = new VulkanImageAccessRangeDelta[_imageAccessDeltaCount];
        Array.Copy(_imageAccessDeltas, deltas, _imageAccessDeltaCount);

        VulkanQueueOwnershipTransferRequirement[] transfers = new VulkanQueueOwnershipTransferRequirement[_queueOwnershipTransferCount];
        Array.Copy(_queueOwnershipTransfers, transfers, _queueOwnershipTransferCount);

        return new VulkanSealedRecordingReceipt(
            CommandBuffer,
            Lane,
            FrameSlot,
            RecordingGeneration,
            deps,
            deltas,
            transfers,
            isSuccess);
    }
}
