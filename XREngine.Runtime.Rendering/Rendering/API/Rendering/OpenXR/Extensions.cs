using Silk.NET.OpenXR.Extensions.HTC;
using Silk.NET.OpenXR.Extensions.HTCX;
using Silk.NET.OpenXR.Extensions.KHR;
using OxrExtDebugUtils = global::Silk.NET.OpenXR.Extensions.EXT.ExtDebugUtils;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly string[] HTC_Extensions =
    [
        HtcxViveTrackerInteraction.ExtensionName,
        HtcFacialTracking.ExtensionName,
        HtcFoveation.ExtensionName,
        HtcPassthrough.ExtensionName,
        HtcAnchor.ExtensionName,
        HtcBodyTracking.ExtensionName,
    ];
    public enum ERenderer
    {
        OpenGL,
        Vulkan,
    }
    private string[] GetRequiredExtensions(ERenderer renderer, params string[] otherExtensions)
    {
        string[] extensions = [];
        if (EnableValidationLayers)
            extensions = [.. extensions, OxrExtDebugUtils.ExtensionName];
        switch (renderer)
        {
            case ERenderer.Vulkan:
                {
                    extensions = [.. extensions, KhrVulkanEnable.ExtensionName, KhrVulkanEnable2.ExtensionName];
                }
                break;
            case ERenderer.OpenGL:
                {
                    extensions = [.. extensions, KhrOpenglEnable.ExtensionName];
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(renderer), renderer, null);
        }

        if (otherExtensions.Length > 0)
            extensions = [.. extensions, .. otherExtensions];

        return [.. extensions, .. HTC_Extensions];
    }
}