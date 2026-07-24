namespace XREngine.Rendering.Vulkan;

internal sealed record RenderGraphPlanResourceLifetime(
    string ResourceKey,
    int FirstPassOrder,
    int LastPassOrder,
    int FirstSubmissionIndex,
    int LastSubmissionIndex);
