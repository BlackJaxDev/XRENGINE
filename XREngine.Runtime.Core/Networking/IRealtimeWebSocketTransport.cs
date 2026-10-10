namespace XREngine.Networking;

/// <summary>A single ordered realtime connection whose datagrams retain the production admission envelope.</summary>
public interface IRealtimeWebSocketTransport : IDatagramTransport
{
    /// <summary>Credential-free terminal diagnostic; reconnection requires a fresh admission handoff.</summary>
    string? Failure { get; }
}
