namespace XREngine.Rendering.Vulkan;

/// <summary>Owns OBS Vulkan output-capture policy and discovery state.</summary>
internal sealed class VulkanObsHookOutputState
{
    internal EVulkanObsHookPolicy Policy = EVulkanObsHookPolicy.Auto;
    internal bool LayerAvailable;
    internal bool DisabledForProcess;
    internal bool DisabledByLoader;
    internal string? LayerManifestPath;
}
