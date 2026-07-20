namespace XREngine;

/// <summary>
/// Represents the startup settings required for initializing a VR game.
/// </summary>
public interface IVRGameStartupSettings
{
    /// <summary>
    /// Gets or sets the host-owned VR manifest used for initializing the VR runtime.
    /// </summary>
    object? VRManifest { get; set; }
    /// <summary>
    /// Gets the host-owned action manifest used for defining VR input actions.
    /// </summary>
    object? ActionManifest { get; }
    /// <summary>
    /// Gets or sets the VR runtime to be used (e.g., OpenXR or OpenVR).
    /// </summary>
    EVRRuntime VRRuntime { get; set; }
    /// <summary>
    /// Gets or sets the view render mode for the VR application.
    /// </summary>
    EVrViewRenderMode VrViewRenderMode { get; set; }
    /// <summary>
    /// Gets or sets a value indicating whether OpenXR Vulkan parallel rendering is enabled.
    /// </summary>
    bool EnableOpenXrVulkanParallelRendering { get; set; }
    /// <summary>
    /// Gets or sets the name of the game.
    /// </summary>
    string GameName { get; set; }
    /// <summary>
    /// Gets or sets the search paths for the VR game, specified as an array of tuples containing the base folder and relative path.
    /// </summary>
    (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
}
