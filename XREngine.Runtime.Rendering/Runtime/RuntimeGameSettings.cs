using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeGameSettings : IVRGameStartupSettings
{
    public uint MaxMirrorRecursionCount { get; set; } = 4u;
    public RuntimeBuildSettings BuildSettings { get; } = new();
    public object? VRManifest { get; set; }
    public object? ActionManifest { get; }
    public EVRRuntime VRRuntime { get; set; } = EVRRuntime.Auto;
    public EVrViewRenderMode VrViewRenderMode { get; set; } = RuntimeRenderingHostServiceDefaults.VrViewRenderMode;
    public bool EnableOpenXrVulkanParallelRendering { get; set; }
    public string GameName { get; set; } = string.Empty;
    public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; } = [];
}
