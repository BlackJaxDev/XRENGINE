using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the one-shot deferred-light UBO probe. The probe is diagnostic-only:
/// it records no work until a desktop graphics submission has been accepted.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private const uint MaximumDeferredLightingUboReadbackBytes = 4096;
    private DeferredLightingUboReadbackRequest _deferredLightingUboReadback;
    private int _hasDeferredLightingUboReadback;

    internal void CaptureDeferredLightingObjectReadback(
        Buffer buffer,
        ulong bufferGeneration,
        ulong offset,
        uint range,
        int frameIndex,
        int drawUniformSlot,
        int rendererIdentity,
        ulong programBindingId,
        ulong expectedHash,
        Vector3 expectedColor,
        float expectedIntensity,
        uint colorOffset,
        uint intensityOffset)
    {
        if (!DeferredLightingDiagnostics.Enabled || buffer.Handle == 0 ||
            bufferGeneration == 0 || range == 0 ||
            range > MaximumDeferredLightingUboReadbackBytes)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _hasDeferredLightingUboReadback, 1, 0) != 0)
            return;

        _deferredLightingUboReadback = new(
            buffer,
            bufferGeneration,
            offset,
            range,
            frameIndex,
            drawUniformSlot,
            rendererIdentity,
            programBindingId,
            expectedHash,
            expectedColor,
            expectedIntensity,
            colorOffset,
            intensityOffset,
            BoundCommandBuffer: default,
            IsBound: false);
    }

    internal void ConfirmDeferredLightingObjectReadbackBinding(
        Buffer buffer,
        ulong bufferGeneration,
        ulong offset,
        uint range,
        int frameIndex,
        int drawUniformSlot,
        int rendererIdentity,
        ulong programBindingId,
        CommandBuffer commandBuffer)
    {
        if (Volatile.Read(ref _hasDeferredLightingUboReadback) == 0)
            return;

        DeferredLightingUboReadbackRequest request = _deferredLightingUboReadback;
        if (request.Buffer.Handle != buffer.Handle ||
            request.BufferGeneration != bufferGeneration ||
            request.Range != range ||
            request.FrameIndex != frameIndex ||
            request.DrawUniformSlot != drawUniformSlot ||
            request.RendererIdentity != rendererIdentity ||
            request.ProgramBindingId != programBindingId ||
            commandBuffer.Handle == 0)
        {
            return;
        }

        // Descriptor-buffer backends can contribute a nonzero base offset.
        // Probe the resolved native range rather than the CPU write's original
        // allocation, so an offset-composition bug becomes observable.
        _deferredLightingUboReadback = request with
        {
            Offset = offset,
            BoundCommandBuffer = commandBuffer,
            IsBound = true,
        };
    }

    /// <summary>
    /// Executes after the source graphics submit has been accepted. The
    /// one-shot graphics-queue submission is therefore ordered after the
    /// deferred-light draw, and its fence is the only host-read authority.
    /// </summary>
    internal void FlushDeferredLightingObjectReadback(CommandBuffer acceptedCommandBuffer)
    {
        if (Interlocked.Exchange(ref _hasDeferredLightingUboReadback, 0) == 0)
            return;

        DeferredLightingUboReadbackRequest request = _deferredLightingUboReadback;
        _deferredLightingUboReadback = default;
        if (!request.IsBound || request.BoundCommandBuffer.Handle != acceptedCommandBuffer.Handle)
        {
            VkMeshRenderer.PublishDeferredLightingObjectGpuReadback(
                "[Vulkan.DeferredLightingObject] GPU UBO probe rejected because the captured write was not confirmed on the accepted primary command buffer.");
            return;
        }

        ulong currentGeneration = ResourceRuntime.Lifetime.Tracker.GetPublishedGeneration(
            new VulkanResourceLifetimeKey(ObjectType.Buffer, request.Buffer.Handle));
        if (currentGeneration != request.BufferGeneration)
        {
            VkMeshRenderer.PublishDeferredLightingObjectGpuReadback(
                $"[Vulkan.DeferredLightingObject] GPU UBO probe rejected because buffer=0x{request.Buffer.Handle:X} generation changed from {request.BufferGeneration} to {currentGeneration}.");
            return;
        }

        Span<byte> bytes = stackalloc byte[checked((int)request.Range)];
        if (!TryReadBufferBytes(request.Buffer, request.Offset, bytes, out string reason))
        {
            VkMeshRenderer.PublishDeferredLightingObjectGpuReadback(
                $"[Vulkan.DeferredLightingObject] GPU UBO probe failed buffer=0x{request.Buffer.Handle:X} generation={request.BufferGeneration} offset={request.Offset} range={request.Range} reason={reason}.");
            return;
        }

        ulong actualHash = ComputeByteHash(bytes);
        bool hasColor = request.ColorOffset <= bytes.Length - 12;
        bool hasIntensity = request.IntensityOffset <= bytes.Length - sizeof(float);
        Vector3 actualColor = hasColor
            ? MemoryMarshal.Read<Vector3>(bytes.Slice(checked((int)request.ColorOffset), 12))
            : default;
        float actualIntensity = hasIntensity
            ? MemoryMarshal.Read<float>(bytes.Slice(checked((int)request.IntensityOffset), sizeof(float)))
            : 0.0f;
        bool valuesMatch = hasColor && hasIntensity &&
            actualColor == request.ExpectedColor &&
            BitConverter.SingleToInt32Bits(actualIntensity) ==
                BitConverter.SingleToInt32Bits(request.ExpectedIntensity);
        VkMeshRenderer.PublishDeferredLightingObjectGpuReadback(
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[Vulkan.DeferredLightingObject] GPU UBO probe frame={0} drawSlot={1} renderer=0x{2:X8} program={3} buffer=0x{4:X} generation={5} offset={6} range={7} expectedHash=0x{8:X16} actualHash=0x{9:X16} expectedLightData=({10:R},{11:R},{12:R};{13:R}) actualLightData=({14:R},{15:R},{16:R};{17:R}) valuesMatch={18}.",
                request.FrameIndex,
                request.DrawUniformSlot,
                request.RendererIdentity,
                request.ProgramBindingId,
                request.Buffer.Handle,
                request.BufferGeneration,
                request.Offset,
                request.Range,
                request.ExpectedHash,
                actualHash,
                request.ExpectedColor.X,
                request.ExpectedColor.Y,
                request.ExpectedColor.Z,
                request.ExpectedIntensity,
                actualColor.X,
                actualColor.Y,
                actualColor.Z,
                actualIntensity,
                valuesMatch));
    }

    internal void DiscardDeferredLightingObjectReadback()
    {
        Volatile.Write(ref _hasDeferredLightingUboReadback, 0);
        _deferredLightingUboReadback = default;
    }

    private static ulong ComputeByteHash(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash ^= bytes[index];
            hash *= prime;
        }
        return hash;
    }

    private readonly record struct DeferredLightingUboReadbackRequest(
        Buffer Buffer,
        ulong BufferGeneration,
        ulong Offset,
        uint Range,
        int FrameIndex,
        int DrawUniformSlot,
        int RendererIdentity,
        ulong ProgramBindingId,
        ulong ExpectedHash,
        Vector3 ExpectedColor,
        float ExpectedIntensity,
        uint ColorOffset,
        uint IntensityOffset,
        CommandBuffer BoundCommandBuffer,
        bool IsBound);
}
