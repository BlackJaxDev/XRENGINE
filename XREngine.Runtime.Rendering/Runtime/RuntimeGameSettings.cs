using XREngine.Rendering;

using XREngine.Input;

namespace XREngine;

internal sealed class RuntimeGameSettings : IVRGameStartupSettings
{
    public uint MaxMirrorRecursionCount { get; set; } = 4u;
    public RuntimeBuildSettings BuildSettings { get; } = new();
    public RuntimeOpenVrApplicationManifest? VRManifest { get; set; }
    public IRuntimeOpenVrActionManifest? ActionManifest { get; }
    public EVRRuntime VRRuntime { get; set; } = EVRRuntime.Auto;
    public bool StartVrOnLaunch { get; set; } = true;
    public EVrViewRenderMode VrViewRenderMode { get; set; } = RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
    public bool EnableOpenXrVulkanParallelRendering { get; set; }
    public string GameName { get; set; } = string.Empty;
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; } = [];
}
