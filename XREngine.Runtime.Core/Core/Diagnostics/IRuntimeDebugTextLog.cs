namespace XREngine;

/// <summary>Writes lines to one native log file.</summary>
/// <remarks>
/// Debug writes and disposes text logs while it holds its writer lock.
/// Implementations must not call Debug or wait for a thread that needs that lock.
/// </remarks>
public interface IRuntimeDebugTextLog : IDisposable
{
    void WriteLine(string? value);
}
