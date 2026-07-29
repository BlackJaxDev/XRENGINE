namespace XREngine.Rendering.Vulkan;

internal sealed record GlslStructDefinition(
    string Name,
    IReadOnlyList<GlslStructField> Fields);
