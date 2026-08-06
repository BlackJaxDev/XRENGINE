using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer :
    IIndirectDrawSecondaryRecordingBackendCapability
{
    private readonly record struct ProducerCompleteIndirectStream(
        XRDataBuffer IndirectBuffer,
        XRDataBuffer? ParameterBuffer,
        ulong IndirectBufferIdentity,
        ulong ParameterBufferIdentity);

    private ProducerCompleteIndirectStream?
        _pendingProducerCompleteIndirectStream;

    bool IIndirectDrawSecondaryRecordingBackendCapability.
        TryBeginProducerCompleteIndirectStream(
            XRDataBuffer indirectBuffer,
            XRDataBuffer? parameterBuffer,
            out IndirectDrawSecondaryRecordingToken token)
    {
        token = _pendingProducerCompleteIndirectStream is { } previous
            ? new(
                previous.IndirectBuffer,
                previous.ParameterBuffer,
                previous.IndirectBufferIdentity,
                previous.ParameterBufferIdentity,
                true)
            : default;

        if (_boundIndirectBuffer is not { } boundIndirect ||
            !ReferenceEquals(boundIndirect.Data, indirectBuffer) ||
            !IsProducerCompleteIndirectBuffer(boundIndirect))
        {
            return false;
        }

        VkDataBuffer? boundParameter = null;
        if (parameterBuffer is not null)
        {
            if (_boundParameterBuffer is not { } parameter ||
                !ReferenceEquals(parameter.Data, parameterBuffer) ||
                !IsProducerCompleteIndirectBuffer(parameter))
            {
                return false;
            }

            boundParameter = parameter;
        }

        _pendingProducerCompleteIndirectStream = new(
            indirectBuffer,
            parameterBuffer,
            CaptureProducerCompleteIndirectBufferIdentity(boundIndirect),
            CaptureProducerCompleteIndirectBufferIdentity(boundParameter));
        return true;
    }

    void IIndirectDrawSecondaryRecordingBackendCapability.
        EndProducerCompleteIndirectStream(
            in IndirectDrawSecondaryRecordingToken token)
        => _pendingProducerCompleteIndirectStream = token.HadPreviousState
            ? new(
                token.PreviousIndirectBuffer!,
                token.PreviousParameterBuffer,
                token.PreviousIndirectBufferIdentity,
                token.PreviousParameterBufferIdentity)
            : null;

    private static bool IsProducerCompleteIndirectBuffer(
        VkDataBuffer buffer)
        => buffer.BufferHandle is { Handle: not 0 } &&
            buffer.IsReadyForRendering &&
            !buffer.HasPendingUpload;

    private ulong CaptureProducerCompleteIndirectBufferIdentity(VkDataBuffer? buffer)
    {
        if (buffer?.BufferHandle is not { } nativeBuffer || nativeBuffer.Handle == 0UL)
            return 0UL;

        FrameOpSignatureHasher hash = new();
        hash.Add(nativeBuffer.Handle);
        hash.Add(GetCurrentVulkanResourceGeneration(ObjectType.Buffer, nativeBuffer.Handle));
        hash.Add(buffer.AllocatedByteSize);
        hash.Add((ulong)buffer.LastUsageFlags);
        return hash.ToHash();
    }

    private VulkanIndirectSecondaryRecordingContract
        CaptureIndirectSecondaryRecordingContract(
            VkDataBuffer indirectBuffer,
            VkDataBuffer? parameterBuffer,
            uint drawCount,
            uint stride,
            nuint byteOffset,
            nuint countByteOffset,
            bool useCount)
    {
        if (_pendingProducerCompleteIndirectStream is not { } pending)
        {
            return new(
                EVulkanIndirectSecondaryEligibility.MutableCurrentFrame,
                0,
                0,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount);
        }

        if (!ReferenceEquals(pending.IndirectBuffer, indirectBuffer.Data) ||
            !ReferenceEquals(
                pending.ParameterBuffer,
                parameterBuffer?.Data))
        {
            return new(
                EVulkanIndirectSecondaryEligibility.BufferIdentityChanged,
                pending.IndirectBufferIdentity,
                pending.ParameterBufferIdentity,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount);
        }

        if (!IsProducerCompleteIndirectBuffer(indirectBuffer) ||
            useCount &&
            (parameterBuffer is null ||
             !IsProducerCompleteIndirectBuffer(parameterBuffer)))
        {
            return new(
                EVulkanIndirectSecondaryEligibility.ProducerIncomplete,
                pending.IndirectBufferIdentity,
                pending.ParameterBufferIdentity,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount);
        }

        ulong indirectIdentity =
            CaptureProducerCompleteIndirectBufferIdentity(indirectBuffer);
        ulong parameterIdentity =
            CaptureProducerCompleteIndirectBufferIdentity(parameterBuffer);
        if (indirectIdentity != pending.IndirectBufferIdentity ||
            parameterIdentity != pending.ParameterBufferIdentity)
        {
            return new(
                EVulkanIndirectSecondaryEligibility.BufferIdentityChanged,
                pending.IndirectBufferIdentity,
                pending.ParameterBufferIdentity,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount);
        }

        if (!IsIndirectSecondaryRangeValid(
                indirectBuffer,
                parameterBuffer,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount))
        {
            return new(
                EVulkanIndirectSecondaryEligibility.InvalidRange,
                indirectIdentity,
                parameterIdentity,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount);
        }

        return new(
            EVulkanIndirectSecondaryEligibility.EligibleProducerComplete,
            indirectIdentity,
            parameterIdentity,
            drawCount,
            stride,
            byteOffset,
            countByteOffset,
            useCount);
    }

    private EVulkanIndirectSecondaryEligibility
        EvaluateIndirectSecondaryRecordingContract(
            IndirectDrawOp operation)
    {
        VulkanIndirectSecondaryRecordingContract contract =
            operation.SecondaryRecordingContract;
        if (!contract.IsEligible)
        {
            return contract.Eligibility ==
                EVulkanIndirectSecondaryEligibility.NotEvaluated
                    ? EVulkanIndirectSecondaryEligibility.MutableCurrentFrame
                    : contract.Eligibility;
        }

        if (!IsProducerCompleteIndirectBuffer(operation.IndirectBuffer) ||
            operation.UseCount &&
            (operation.ParameterBuffer is null ||
             !IsProducerCompleteIndirectBuffer(operation.ParameterBuffer)))
        {
            return EVulkanIndirectSecondaryEligibility.ProducerIncomplete;
        }

        if (CaptureProducerCompleteIndirectBufferIdentity(
                operation.IndirectBuffer) !=
                contract.IndirectBufferIdentity ||
            CaptureProducerCompleteIndirectBufferIdentity(
                operation.ParameterBuffer) !=
                contract.ParameterBufferIdentity)
        {
            return EVulkanIndirectSecondaryEligibility.BufferIdentityChanged;
        }

        return IsIndirectSecondaryRangeValid(
            operation.IndirectBuffer,
            operation.ParameterBuffer,
            operation.DrawCount,
            operation.Stride,
            operation.ByteOffset,
            operation.CountByteOffset,
            operation.UseCount)
                ? EVulkanIndirectSecondaryEligibility.EligibleProducerComplete
                : EVulkanIndirectSecondaryEligibility.InvalidRange;
    }

    private static bool IsIndirectSecondaryRangeValid(
        VkDataBuffer indirectBuffer,
        VkDataBuffer? parameterBuffer,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount)
    {
        const ulong IndexedIndirectCommandSize = 5UL * sizeof(uint);
        if (drawCount == 0 ||
            stride < IndexedIndirectCommandSize ||
            (stride & 3u) != 0)
        {
            return false;
        }

        ulong commandOffset = byteOffset;
        ulong lastCommandDelta = (ulong)(drawCount - 1u) * stride;
        if (lastCommandDelta >
            ulong.MaxValue - IndexedIndirectCommandSize ||
            commandOffset >
            ulong.MaxValue -
            (lastCommandDelta + IndexedIndirectCommandSize))
        {
            return false;
        }

        ulong indirectEnd =
            commandOffset + lastCommandDelta + IndexedIndirectCommandSize;
        if (indirectEnd > indirectBuffer.UploadedByteCount ||
            indirectEnd > indirectBuffer.AllocatedByteSize)
        {
            return false;
        }

        if (!useCount)
            return true;

        if (parameterBuffer is null || (countByteOffset & 3u) != 0)
            return false;

        ulong countOffset = countByteOffset;
        if (countOffset > ulong.MaxValue - sizeof(uint))
            return false;

        ulong countEnd = countOffset + sizeof(uint);
        return countEnd <= parameterBuffer.UploadedByteCount &&
            countEnd <= parameterBuffer.AllocatedByteSize;
    }
}
