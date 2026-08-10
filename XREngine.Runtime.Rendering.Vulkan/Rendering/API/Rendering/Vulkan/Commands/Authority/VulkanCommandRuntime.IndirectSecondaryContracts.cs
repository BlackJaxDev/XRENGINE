namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    internal bool TryBeginProducerCompleteIndirectStream(
        VkDataBuffer? boundIndirect,
        VkDataBuffer? boundParameter,
        XRDataBuffer indirectBuffer,
        XRDataBuffer? parameterBuffer,
        out IndirectDrawSecondaryRecordingToken token)
    {
        token = PendingProducerCompleteIndirectStream is { } previous
            ? new(
                previous.IndirectBuffer,
                previous.ParameterBuffer,
                previous.IndirectBufferIdentity,
                previous.ParameterBufferIdentity,
                true)
            : default;

        if (boundIndirect is null ||
            !ReferenceEquals(boundIndirect.Data, indirectBuffer) ||
            !IsProducerCompleteIndirectBuffer(boundIndirect))
        {
            return false;
        }

        VkDataBuffer? preparedParameter = null;
        if (parameterBuffer is not null)
        {
            if (boundParameter is null ||
                !ReferenceEquals(boundParameter.Data, parameterBuffer) ||
                !IsProducerCompleteIndirectBuffer(boundParameter))
            {
                return false;
            }

            preparedParameter = boundParameter;
        }

        PendingProducerCompleteIndirectStream = new(
            indirectBuffer,
            parameterBuffer,
            CaptureProducerCompleteIndirectBufferIdentity(boundIndirect),
            CaptureProducerCompleteIndirectBufferIdentity(preparedParameter));
        return true;
    }

    internal void EndProducerCompleteIndirectStream(
        in IndirectDrawSecondaryRecordingToken token)
        => PendingProducerCompleteIndirectStream = token.HadPreviousState
            ? new(
                token.PreviousIndirectBuffer!,
                token.PreviousParameterBuffer,
                token.PreviousIndirectBufferIdentity,
                token.PreviousParameterBufferIdentity)
            : null;

    internal VulkanIndirectSecondaryRecordingContract CaptureIndirectSecondaryRecordingContract(
        VkDataBuffer indirectBuffer,
        VkDataBuffer? parameterBuffer,
        uint drawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        bool useCount)
    {
        if (PendingProducerCompleteIndirectStream is not { } pending)
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
            !ReferenceEquals(pending.ParameterBuffer, parameterBuffer?.Data))
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
            useCount && (parameterBuffer is null || !IsProducerCompleteIndirectBuffer(parameterBuffer)))
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

        ulong indirectIdentity = CaptureProducerCompleteIndirectBufferIdentity(indirectBuffer);
        ulong parameterIdentity = CaptureProducerCompleteIndirectBufferIdentity(parameterBuffer);
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

        EVulkanIndirectSecondaryEligibility eligibility =
            IsIndirectSecondaryRangeValid(
                indirectBuffer,
                parameterBuffer,
                drawCount,
                stride,
                byteOffset,
                countByteOffset,
                useCount)
                ? EVulkanIndirectSecondaryEligibility.EligibleProducerComplete
                : EVulkanIndirectSecondaryEligibility.InvalidRange;
        return new(
            eligibility,
            indirectIdentity,
            parameterIdentity,
            drawCount,
            stride,
            byteOffset,
            countByteOffset,
            useCount);
    }
}
