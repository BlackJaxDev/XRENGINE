namespace XREngine.Rendering;

/// <summary>
/// Backend resource limits relevant to an uber material variant.
/// </summary>
public sealed record UberMaterialBindingLimits
{
    public required string BackendName { get; init; }
    public int MaxFragmentSamplers { get; init; } = 16;
    public int MaxSampledImages { get; init; } = 16;
    public int MaxUniformBytes { get; init; } = 16 * 1024;
    public int MaxPushConstantBytes { get; init; }

    public static UberMaterialBindingLimits OpenGl46Minimum { get; } = new()
    {
        BackendName = "OpenGL 4.6 minimum",
        MaxFragmentSamplers = 16,
        MaxSampledImages = 16,
        MaxUniformBytes = 16 * 1024,
    };

    public static UberMaterialBindingLimits Vulkan10Minimum { get; } = new()
    {
        BackendName = "Vulkan 1.0 minimum",
        MaxFragmentSamplers = 16,
        MaxSampledImages = 16,
        MaxUniformBytes = 16 * 1024,
        MaxPushConstantBytes = 128,
    };
}
