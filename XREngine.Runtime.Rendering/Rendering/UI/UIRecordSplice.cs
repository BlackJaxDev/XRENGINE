namespace XREngine.Rendering.UI;

/// <summary>
/// Replaces <see cref="OldCount"/> records at <see cref="OldStart"/> in an old ordered store with
/// <see cref="NewCount"/> records at <see cref="NewStart"/> in a caller-supplied insert span.
/// </summary>
internal readonly record struct UIRecordSplice(int OldStart, int OldCount, int NewStart, int NewCount)
{
    /// <summary>Gets the record count change of this edit.</summary>
    internal int Delta => NewCount - OldCount;
}
