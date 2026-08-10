using System.Collections.Frozen;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Required device-extension facts contributed by the output target and
/// optional runtime integrations. The device core consumes values only.
/// </summary>
internal sealed class VulkanDeviceExtensionRequirements
{
    public VulkanDeviceExtensionRequirements(
        IEnumerable<string>? targetExtensions,
        IEnumerable<string>? streamlineExtensions,
        IEnumerable<string>? openXrExtensions)
    {
        TargetExtensions = Freeze(targetExtensions);
        StreamlineExtensions = Freeze(streamlineExtensions);
        OpenXrExtensions = Freeze(openXrExtensions);
        RequiredExtensions = TargetExtensions
            .Concat(StreamlineExtensions)
            .Concat(OpenXrExtensions)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    public FrozenSet<string> TargetExtensions { get; }
    public FrozenSet<string> StreamlineExtensions { get; }
    public FrozenSet<string> OpenXrExtensions { get; }
    public FrozenSet<string> RequiredExtensions { get; }

    private static FrozenSet<string> Freeze(IEnumerable<string>? extensions)
        => (extensions ?? [])
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .ToFrozenSet(StringComparer.Ordinal);
}
