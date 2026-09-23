namespace XREngine.LocalAgentBroker;

/// <summary>Raw source material retained only long enough for best-effort rollback.</summary>
internal sealed record AppliedChange(
    string Path,
    string FullPath,
    byte[]? Original,
    byte[] Output,
    bool IsNewFile);
