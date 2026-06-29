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

    private const string FbFoveationExtensionName = "XR_FB_foveation";
    private const string FbFoveationConfigurationExtensionName = "XR_FB_foveation_configuration";
    private const string FbFoveationVulkanExtensionName = "XR_FB_foveation_vulkan";
    private const string MetaFoveationEyeTrackedExtensionName = "XR_META_foveation_eye_tracked";
    private const string VarjoQuadViewsExtensionName = "XR_VARJO_quad_views";

    private readonly string[] Foveation_Extensions =
    [
        FbFoveationExtensionName,
        FbFoveationConfigurationExtensionName,
        FbFoveationVulkanExtensionName,
        MetaFoveationEyeTrackedExtensionName,
        VarjoQuadViewsExtensionName,
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

        extensions = [.. extensions, KhrWin32ConvertPerformanceCounterTime.ExtensionName];

        return [.. extensions, .. HTC_Extensions, .. Foveation_Extensions];
    }
}
