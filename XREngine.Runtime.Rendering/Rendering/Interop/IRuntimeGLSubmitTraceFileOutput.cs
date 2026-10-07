namespace XREngine.Rendering;

/// <summary>Opens an optional GL submit trace log through the host.</summary>
public interface IRuntimeGLSubmitTraceFileOutput
{
    /// <summary>
    /// Creates or truncates the trace log and returns a fresh, exclusive, usable lease.
    /// The host must use UTF-8 without a byte order mark, LF lines, and a writer that
    /// flushes each line through a write-through file stream.
    /// The host must release all partial resources before it throws.
    /// This call runs under the GLSubmitTracer lock. It must not wait for another
    /// thread to enter GLSubmitTracer.
    /// </summary>
    IRuntimeOwnedTextLog OpenGLSubmitTraceLog();
}
