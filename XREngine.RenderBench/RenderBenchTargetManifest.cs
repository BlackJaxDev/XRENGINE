namespace XREngine.RenderBench;

/// <summary>Physical execution identity, distinct from synthetic fixture identity.</summary>
public sealed record RenderBenchTargetManifest(
    string PresentationTarget,
    int OutputCount,
    uint GraphicsQueueFamily,
    string PresentPolicy,
    string BackendModuleSha256)
{
    public XREngine.Rendering.Vulkan.QueueFamilyIndices QueueFamilies { get; init; }
}
