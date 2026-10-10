namespace XREngine;

/// <summary>Identifies one native log run when its directory is created.</summary>
public sealed record RuntimeDebugLogSessionOptions(string? SessionId, string ApplicationIdentifier, int ProcessId)
{
    public string CreateSessionId()
        => SessionId ?? Debug.BuildLogSessionId(ApplicationIdentifier, DateTime.Now, ProcessId);
}
