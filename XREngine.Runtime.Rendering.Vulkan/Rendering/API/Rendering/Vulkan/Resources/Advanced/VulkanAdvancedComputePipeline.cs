using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact compute program generation admitted before primary recording.</summary>
internal readonly record struct VulkanAdvancedComputePipeline(
    VkRenderProgram Program, Pipeline Pipeline, ulong LinkGeneration, uint NativeShadingRootAbiVersion = 0)
{
    internal bool UsesAddressRoot => NativeShadingRootAbiVersion != 0;
    internal bool IsCurrent => Program is { IsLinked: true } &&
        Program.LinkGeneration == LinkGeneration && Pipeline.Handle != 0;

    /// <summary>
    /// Compares all fields. The program uses the default comparer. The pipeline handle compares
    /// by value, so the check does not box it.
    /// </summary>
    public bool Equals(VulkanAdvancedComputePipeline other)
        => EqualityComparer<VkRenderProgram>.Default.Equals(Program, other.Program) &&
           Pipeline.Handle == other.Pipeline.Handle &&
           LinkGeneration == other.LinkGeneration &&
           NativeShadingRootAbiVersion == other.NativeShadingRootAbiVersion;

    /// <summary>Combines the program, the pipeline handle value, and the generations without boxing.</summary>
    public override int GetHashCode()
        => HashCode.Combine(Program, Pipeline.Handle, LinkGeneration, NativeShadingRootAbiVersion);
}
