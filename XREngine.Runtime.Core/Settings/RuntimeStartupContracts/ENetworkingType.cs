namespace XREngine;

public enum ENetworkingType
{
    /// <summary>
    /// The application is a server.
    /// Clients will connect to this server.
    /// </summary>
    Server,
    /// <summary>
    /// The application is a client.
    /// The client will connect to a server.
    /// </summary>
    Client,
    /// <summary>
    /// The application is a local client.
    /// No network connection is used.
    /// </summary>
    Local,
}
