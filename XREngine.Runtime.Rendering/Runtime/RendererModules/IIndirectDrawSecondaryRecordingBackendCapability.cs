namespace XREngine.Rendering;

/// <summary>
/// Allows a backend to capture an explicitly producer-complete indirect
/// argument stream for reusable secondary command recording.
/// </summary>
public interface IIndirectDrawSecondaryRecordingBackendCapability
{
    bool TryBeginProducerCompleteIndirectStream(
        XRDataBuffer indirectBuffer,
        XRDataBuffer? parameterBuffer,
        out IndirectDrawSecondaryRecordingToken token);

    void EndProducerCompleteIndirectStream(
        in IndirectDrawSecondaryRecordingToken token);
}
