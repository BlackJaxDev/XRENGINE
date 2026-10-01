namespace XREngine.Networking;

/// <summary>Wire limits shared by the browser transport and encrypted server gateway.</summary>
public static class RealtimeWebSocketProtocol
{
    public const string Subprotocol = RealtimeWireProtocol.WebSocketProtocol;
    public const string Path = "/realtime";
    public const int MaximumDatagramBytes = 65_507;
    public const int MaximumQueuedDatagrams = 128;
    public const int MaximumQueuedBytes = 2 * 1024 * 1024;

    /// <summary>Credentials travel only in the managed admission protocol, never in an upgrade URL.</summary>
    public static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "wss" || endpoint.AbsolutePath != Path
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Realtime WebSocket requires wss, the /realtime path, and no URL credentials, query, or fragment.", nameof(endpoint));
    }
}
