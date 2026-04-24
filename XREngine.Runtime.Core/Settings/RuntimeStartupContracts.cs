using OpenVR.NET.Manifest;

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

public enum EVRRuntime
{
    /// <summary>
    /// Uses OpenXR when available, otherwise falls back to OpenVR.
    /// </summary>
    Auto,
    /// <summary>
    /// Forces OpenXR initialization.
    /// </summary>
    OpenXR,
    /// <summary>
    /// Forces OpenVR (SteamVR/OpenVR.NET) initialization.
    /// </summary>
    OpenVR,
}

public interface IVRGameStartupSettings
{
    VrManifest? VRManifest { get; set; }
    IActionManifest? ActionManifest { get; }
    EVRRuntime VRRuntime { get; set; }
    bool EnableOpenXrVulkanParallelRendering { get; set; }
    string GameName { get; set; }
    (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
}
