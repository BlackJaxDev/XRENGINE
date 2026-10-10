namespace XREngine.Rendering;

/// <summary>
/// Owns a diagnostic writer and all resources that support it.
/// The lease alone disposes the writer and its supporting resources.
/// Disposal must attempt to close both even if the first close fails.
/// GLSubmitTracer reads Writer, writes text, and calls Dispose under its lock.
/// These calls must not wait for another thread to enter GLSubmitTracer.
/// </summary>
public interface IRuntimeOwnedTextLog : IDisposable
{
    /// <summary>Gets the same usable writer on each call until lease disposal.</summary>
    TextWriter Writer { get; }
}
