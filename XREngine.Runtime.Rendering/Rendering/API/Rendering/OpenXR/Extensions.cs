using Silk.NET.OpenXR.Extensions.HTC;
using Silk.NET.OpenXR.Extensions.HTCX;
using Silk.NET.OpenXR.Extensions.KHR;
using XREngine.Rendering.Vulkan;
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
    private const string KhrVisibilityMaskExtensionName = "XR_KHR_visibility_mask";
    private const string ExtHandTrackingExtensionName = "XR_EXT_hand_tracking";
    private const string MsftControllerModelExtensionName = "XR_MSFT_controller_model";

    private readonly string[] Foveation_Extensions =
    [
        FbFoveationExtensionName,
        FbFoveationConfigurationExtensionName,
        FbFoveationVulkanExtensionName,
        MetaFoveationEyeTrackedExtensionName,
        VarjoQuadViewsExtensionName,
    ];

    public bool IsRvcOpenXrVisibilityMaskExtensionEnabled
        => IsInstanceExtensionEnabled(KhrVisibilityMaskExtensionName);

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
                    extensions = [.. extensions, .. GetVulkanGraphicsBindingExtensions()];
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

        return [.. extensions, KhrVisibilityMaskExtensionName, ExtHandTrackingExtensionName, MsftControllerModelExtensionName, .. HTC_Extensions, .. Foveation_Extensions];
    }

    private string[] GetVulkanGraphicsBindingExtensions()
    {
        if (Window?.Renderer is VulkanRenderer vulkanRenderer &&
            vulkanRenderer.UsesOpenXrVulkanEnable2Creation)
        {
            return [KhrVulkanEnable2.ExtensionName];
        }

        string? runtimePath = ResolveCurrentOpenXrRuntimePath();
        if (!string.IsNullOrWhiteSpace(runtimePath) && IsSteamVrRuntimePath(runtimePath))
            return [KhrVulkanEnable.ExtensionName];

        return [KhrVulkanEnable.ExtensionName, KhrVulkanEnable2.ExtensionName];
    }
}
