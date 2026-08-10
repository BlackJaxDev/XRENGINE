namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer : IIndirectDrawSecondaryRecordingBackendCapability
{
    bool IIndirectDrawSecondaryRecordingBackendCapability.TryBeginProducerCompleteIndirectStream(
        XRDataBuffer indirectBuffer,
        XRDataBuffer? parameterBuffer,
        out IndirectDrawSecondaryRecordingToken token)
        => _commandRuntime.TryBeginProducerCompleteIndirectStream(
            _commandRuntime.CommandBuffers.BoundIndirectBuffer,
            _commandRuntime.CommandBuffers.BoundParameterBuffer,
            indirectBuffer,
            parameterBuffer,
            out token);

    void IIndirectDrawSecondaryRecordingBackendCapability.EndProducerCompleteIndirectStream(
        in IndirectDrawSecondaryRecordingToken token)
        => _commandRuntime.EndProducerCompleteIndirectStream(token);
}
