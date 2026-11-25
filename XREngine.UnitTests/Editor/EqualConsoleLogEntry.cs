namespace XREngine.UnitTests.Editor;

internal sealed class EqualConsoleLogEntry(string message)
    : LogEntry(message, ELogCategory.General, DateTime.UnixEpoch)
{
    public override bool Equals(object? obj) => obj is EqualConsoleLogEntry;
    public override int GetHashCode() => 1;
}
