namespace XREngine.Editor.Mcp;

/// <summary>
/// Controls how a sequence capture responds when its bounded readback queue is full.
/// </summary>
internal enum ViewportSequenceCaptureOverflowPolicy
{
    /// <summary>
    /// Fail the session so a caller asking for consecutive frames is never given a silently incomplete sequence.
    /// </summary>
    Fail,

    /// <summary>
    /// Skip the sample and record the dropped render frame in the capture manifest.
    /// </summary>
    Drop,
}
