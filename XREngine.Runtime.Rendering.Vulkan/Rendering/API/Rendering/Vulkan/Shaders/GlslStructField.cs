namespace XREngine.Rendering.Vulkan;

internal readonly record struct GlslStructField(
    string GlslType,
    string Name,
    bool IsArray,
    uint ArrayLength);
