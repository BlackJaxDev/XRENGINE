namespace XREngine;

internal sealed class RuntimeGameSettings : IVRGameStartupSettings
{
    public uint MaxMirrorRecursionCount { get; set; } = 4u;
    public RuntimeBuildSettings BuildSettings { get; } = new();
    public OpenVR.NET.Manifest.VrManifest? VRManifest { get; set; }
    public OpenVR.NET.Manifest.IActionManifest? ActionManifest { get; }
    public EVRRuntime VRRuntime { get; set; } = EVRRuntime.Auto;
    public bool EnableOpenXrVulkanParallelRendering { get; set; }
    public string GameName { get; set; } = string.Empty;
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; } = [];
}
