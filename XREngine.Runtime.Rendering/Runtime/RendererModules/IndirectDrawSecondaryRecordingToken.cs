namespace XREngine.Rendering;

/// <summary>
/// Allocation-free restoration token for a nested producer-complete indirect
/// stream declaration.
/// </summary>
public readonly record struct IndirectDrawSecondaryRecordingToken(
    XRDataBuffer? PreviousIndirectBuffer,
    XRDataBuffer? PreviousParameterBuffer,
    ulong PreviousIndirectBufferIdentity,
    ulong PreviousParameterBufferIdentity,
    bool HadPreviousState);
