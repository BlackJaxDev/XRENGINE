using System.Collections.Frozen;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable input to native Vulkan instance creation. Output, OpenXR,
/// Streamline, and validation requirements are flattened into facts so the
/// device authority never retains a renderer, output runtime, or callback.
/// </summary>
internal sealed class VulkanDeviceBootstrapRequest
{
    public VulkanDeviceBootstrapRequest(
        IEnumerable<string> targetInstanceExtensions,
        bool requireSwapchainOutput,
        IEnumerable<string> openXrInstanceExtensions,
        ulong openXrMinimumApiVersion,
        ulong openXrMaximumApiVersion,
        IEnumerable<string> streamlineInstanceExtensions,
        uint streamlineMinimumApiVersion,
        VulkanDeviceValidationRequest validation)
    {
        ArgumentNullException.ThrowIfNull(targetInstanceExtensions);
        ArgumentNullException.ThrowIfNull(openXrInstanceExtensions);
        ArgumentNullException.ThrowIfNull(streamlineInstanceExtensions);

        TargetInstanceExtensions = FreezeExtensions(targetInstanceExtensions);
        RequireSwapchainOutput = requireSwapchainOutput;
        OpenXrInstanceExtensions = FreezeExtensions(openXrInstanceExtensions);
        OpenXrMinimumApiVersion = openXrMinimumApiVersion;
        OpenXrMaximumApiVersion = openXrMaximumApiVersion;
        StreamlineInstanceExtensions = FreezeExtensions(streamlineInstanceExtensions);
        StreamlineMinimumApiVersion = streamlineMinimumApiVersion;
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
    }

    public FrozenSet<string> TargetInstanceExtensions { get; }
    public bool RequireSwapchainOutput { get; }
    public FrozenSet<string> OpenXrInstanceExtensions { get; }
    public ulong OpenXrMinimumApiVersion { get; }
    public ulong OpenXrMaximumApiVersion { get; }
    public FrozenSet<string> StreamlineInstanceExtensions { get; }
    public uint StreamlineMinimumApiVersion { get; }
    public VulkanDeviceValidationRequest Validation { get; }

    private static FrozenSet<string> FreezeExtensions(IEnumerable<string> extensions)
        => extensions
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .ToFrozenSet(StringComparer.Ordinal);
}
