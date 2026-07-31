namespace XREngine.Rendering;

/// <summary>
/// Allocation-free scope for a producer-complete indirect argument stream.
/// </summary>
public readonly struct IndirectDrawSecondaryRecordingScope(
    IIndirectDrawSecondaryRecordingBackendCapability? capability,
    IndirectDrawSecondaryRecordingToken token) : IDisposable
{
    public void Dispose()
        => capability?.EndProducerCompleteIndirectStream(token);
}
