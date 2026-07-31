using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Side-effect-free instance-extension probe for the optional headless WSI lane.</summary>
internal static unsafe class VulkanHeadlessWsiSupport
{
    public const string ExtensionName = "VK_EXT_headless_surface";

    public static VulkanHeadlessWsiProbeResult Probe()
    {
        using Vk api = Vk.GetApi();
        uint count = 0;
        Result result = api.EnumerateInstanceExtensionProperties((byte*)null, ref count, null);
        if (result != Result.Success)
            return new(false, $"vkEnumerateInstanceExtensionProperties failed: {result}.");

        ExtensionProperties[] properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* propertiesPtr = properties)
        {
            result = api.EnumerateInstanceExtensionProperties((byte*)null, ref count, propertiesPtr);
            if (result != Result.Success)
                return new(false, $"vkEnumerateInstanceExtensionProperties failed: {result}.");
        }

        for (int i = 0; i < properties.Length; i++)
        {
            string name;
            fixed (byte* namePtr = properties[i].ExtensionName)
                name = SilkMarshal.PtrToString((nint)namePtr) ?? string.Empty;
            if (string.Equals(name, ExtensionName, StringComparison.Ordinal))
                return new(true, "VK_EXT_headless_surface is available; presentation is a headless WSI no-op, not desktop compositor presentation.");
        }

        return new(false, "VK_EXT_headless_surface is unavailable. Use the presentationless Vulkan lane instead.");
    }
}
