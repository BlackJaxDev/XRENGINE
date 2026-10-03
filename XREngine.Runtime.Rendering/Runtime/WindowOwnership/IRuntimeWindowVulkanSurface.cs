namespace XREngine.Rendering;

/// <summary>Borrowed Vulkan WSI operations supplied by a desktop window.</summary>
public interface IRuntimeWindowVulkanSurface
{
    IReadOnlyList<string> RequiredInstanceExtensions { get; }
    long OwnerGeneration { get; }
    ulong CreateSurface(nint instance);
}
