using System;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    internal bool TryValidateLatePassProfile(
        int renderPass,
        out string? rejectionReason)
    {
        if (!AdvancedLatePassEligibilityValidator.IsAdvancedLateRenderPass(
                renderPass))
        {
            rejectionReason =
                $"Render pass {renderPass} is not an Advanced late-pass lane.";
            return false;
        }

        if (!AllowsLateTransparency)
        {
            rejectionReason =
                "The active offscreen profile disables late transparency.";
            return false;
        }

        if (renderPass ==
                (int)EDefaultRenderPass.WeightedBlendedOitForward &&
            !EnableWeightedBlendedOitPasses)
        {
            rejectionReason =
                "Weighted blended OIT is disabled by the active Advanced pipeline settings.";
            return false;
        }

        bool exact = renderPass is
            (int)EDefaultRenderPass.PerPixelLinkedListForward or
            (int)EDefaultRenderPass.DepthPeelingForward;
        if (!exact)
        {
            rejectionReason = null;
            return true;
        }

        if (Stereo)
        {
            rejectionReason =
                "Exact transparency is unsupported by the active stereo resource profile.";
            return false;
        }
        if (UseOpenXrVulkanDesktopStartupSafePath)
        {
            rejectionReason =
                "Exact transparency is disabled by the OpenXR Vulkan desktop startup-safe profile.";
            return false;
        }
        if (!RuntimeEngine.EditorPreferences.Debug.EnableExactTransparencyTechniques)
        {
            rejectionReason =
                "Exact transparency techniques are disabled by the active debug/profile setting.";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    private bool ShouldRunAdvancedLatePass(int renderPass)
    {
        if (!HasRenderPassCommands(renderPass))
            return false;
        if (TryValidateLatePassProfile(renderPass, out string? rejectionReason))
            return true;

        if (!Debug.ShouldLogEvery("AdvancedLatePass.ProfileRejected", TimeSpan.FromSeconds(2)))
            return false;
        Debug.RenderingWarning(
            "[AdvancedLatePass] Pass {0} has visible consumers but the active profile rejected it. Reason={1}",
            (EDefaultRenderPass)renderPass,
            rejectionReason ?? "Unknown profile eligibility failure.");
        return false;
    }
}
