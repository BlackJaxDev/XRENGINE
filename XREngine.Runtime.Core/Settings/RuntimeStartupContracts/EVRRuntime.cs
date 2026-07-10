namespace XREngine;

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
