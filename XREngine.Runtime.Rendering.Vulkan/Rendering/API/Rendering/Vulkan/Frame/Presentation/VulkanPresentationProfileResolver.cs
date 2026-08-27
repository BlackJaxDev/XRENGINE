using System.Globalization;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Resolves deliberate desktop presentation policy at swapchain creation.</summary>
internal static class VulkanPresentationProfileResolver
{
    private const float DefaultRefreshHz = 60.0f;
    private const string PresentIdExtensionName = "VK_KHR_present_id";
    private const string PresentWaitExtensionName = "VK_KHR_present_wait";
    private const string DisplayTimingExtensionName = "VK_GOOGLE_display_timing";

    internal static VulkanPresentationProfileResolution Resolve(
        IReadOnlyList<PresentModeKHR> modes,
        VulkanDeviceContext device,
        VulkanOutputRuntime output,
        int frameSlotCount,
        bool validationEnabled)
    {
        if (modes.Count == 0)
            throw new NotSupportedException("The desktop surface exposes no Vulkan present modes.");

        EVulkanPresentationProfile requested = ResolveRequestedProfile();
        float targetRefreshHz = ResolveTargetRefreshHz();
        TimeSpan targetInterval = TimeSpan.FromSeconds(1.0 / targetRefreshHz);
        PresentModeKHR nativeMode;
        EVulkanPresentationProfile resolved = requested;
        bool limiterEnabled = false;
        bool frameGenerationEnabled = false;
        int maximumFramesAhead;

        switch (requested)
        {
            case EVulkanPresentationProfile.Stable:
                nativeMode = RequireMode(modes, PresentModeKHR.FifoKhr, requested);
                maximumFramesAhead = Math.Clamp(
                    RuntimeRenderingHostServices.Settings.VulkanPresentationMaximumFramesAhead,
                    1,
                    Math.Max(frameSlotCount, 1));
                break;
            case EVulkanPresentationProfile.LowLatency:
                if (Contains(modes, PresentModeKHR.MailboxKhr))
                {
                    nativeMode = PresentModeKHR.MailboxKhr;
                    limiterEnabled = true;
                    maximumFramesAhead = 1;
                }
                else
                {
                    nativeMode = RequireMode(modes, PresentModeKHR.FifoKhr, requested);
                    resolved = EVulkanPresentationProfile.Stable;
                    maximumFramesAhead = 1;
                    Debug.VulkanWarning(
                        "[Vulkan][Presentation] LowLatency requested but Mailbox is unavailable; resolved Stable/FIFO.");
                }
                break;
            case EVulkanPresentationProfile.Uncapped:
                nativeMode = RequireMode(modes, PresentModeKHR.ImmediateKhr, requested);
                maximumFramesAhead = Math.Max(frameSlotCount, 1);
                break;
            case EVulkanPresentationProfile.FrameGeneration:
                if (!output._streamlineFrameGenerationProvisioned)
                {
                    throw new NotSupportedException(
                        "FrameGeneration presentation was requested, but the Vulkan device was not provisioned for Streamline/DLSS frame generation.");
                }

                nativeMode = Contains(modes, PresentModeKHR.MailboxKhr)
                    ? PresentModeKHR.MailboxKhr
                    : RequireMode(modes, PresentModeKHR.ImmediateKhr, requested);
                maximumFramesAhead = 1;
                frameGenerationEnabled = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(requested), requested, null);
        }

        bool presentIdAvailable = device.AvailableDeviceExtensions.Contains(PresentIdExtensionName);
        bool presentWaitAvailable = device.AvailableDeviceExtensions.Contains(PresentWaitExtensionName);
        bool displayTimingAvailable = device.AvailableDeviceExtensions.Contains(DisplayTimingExtensionName);
        VulkanPresentationProfileSnapshot snapshot = new(
            requested,
            resolved,
            ToPublicPresentMode(nativeMode),
            targetRefreshHz,
            targetInterval,
            maximumFramesAhead,
            limiterEnabled,
            frameGenerationEnabled,
            SwapchainImageCount: 0,
            FrameSlotCount: Math.Max(frameSlotCount, 1),
            validationEnabled,
            RuntimeRenderingHostServices.Settings.VulkanRenderTargetMode,
            presentIdAvailable,
            device.EnabledDeviceExtensions.Contains(PresentIdExtensionName),
            presentWaitAvailable,
            device.EnabledDeviceExtensions.Contains(PresentWaitExtensionName),
            displayTimingAvailable,
            device.EnabledDeviceExtensions.Contains(DisplayTimingExtensionName));
        return new VulkanPresentationProfileResolution(nativeMode, snapshot);
    }

    private static EVulkanPresentationProfile ResolveRequestedProfile()
    {
        EVulkanPresentationProfile requested =
            RuntimeRenderingHostServices.Settings.VulkanPresentationProfile;
        string? raw = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.VulkanPresentationProfile);
        if (string.IsNullOrWhiteSpace(raw))
            return requested;
        if (Enum.TryParse(raw.Trim(), ignoreCase: true, out EVulkanPresentationProfile parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        Debug.VulkanWarning(
            "[Vulkan][Presentation] Ignoring invalid {0} value '{1}'.",
            XREngineEnvironmentVariables.VulkanPresentationProfile,
            raw.Trim());
        return requested;
    }

    private static float ResolveTargetRefreshHz()
    {
        float refreshHz = RuntimeRenderingHostServices.Settings
            .VulkanPresentationTargetRefreshHz;
        string? raw = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.TargetRefreshHz);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (float.TryParse(
                    raw.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float parsed) &&
                float.IsFinite(parsed) && parsed > 0.0f)
            {
                refreshHz = parsed;
            }
            else
            {
                Debug.VulkanWarning(
                    "[Vulkan][Presentation] Ignoring invalid {0} value '{1}'.",
                    XREngineEnvironmentVariables.TargetRefreshHz,
                    raw.Trim());
            }
        }

        return float.IsFinite(refreshHz) && refreshHz > 0.0f
            ? Math.Clamp(refreshHz, 1.0f, 1000.0f)
            : DefaultRefreshHz;
    }

    private static PresentModeKHR RequireMode(
        IReadOnlyList<PresentModeKHR> modes,
        PresentModeKHR required,
        EVulkanPresentationProfile profile)
    {
        if (Contains(modes, required))
            return required;
        throw new NotSupportedException(
            $"Vulkan presentation profile {profile} requires native present mode {required}, which this surface does not expose.");
    }

    private static bool Contains(
        IReadOnlyList<PresentModeKHR> modes,
        PresentModeKHR expected)
    {
        for (int index = 0; index < modes.Count; index++)
            if (modes[index] == expected)
                return true;
        return false;
    }

    private static EVulkanPresentMode ToPublicPresentMode(PresentModeKHR mode)
        => mode switch
        {
            PresentModeKHR.FifoKhr => EVulkanPresentMode.Fifo,
            PresentModeKHR.MailboxKhr => EVulkanPresentMode.Mailbox,
            PresentModeKHR.ImmediateKhr => EVulkanPresentMode.Immediate,
            PresentModeKHR.FifoRelaxedKhr => EVulkanPresentMode.FifoRelaxed,
            _ => EVulkanPresentMode.Unknown,
        };
}
