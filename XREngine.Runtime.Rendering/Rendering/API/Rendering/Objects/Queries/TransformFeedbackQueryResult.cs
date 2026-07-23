namespace XREngine.Rendering;

/// <summary>
/// Typed transform-feedback result. Overflow is present when more primitives
/// were needed than could be written.
/// </summary>
public readonly record struct TransformFeedbackQueryResult(
    ulong PrimitivesWritten,
    ulong PrimitivesNeeded)
{
    public bool Overflowed => PrimitivesNeeded > PrimitivesWritten;
}
