namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Development-only validation diagnostic that fails one upcoming desktop frame at a chosen
/// phase boundary of the current Vulkan renderer. The frame takes its existing failure path,
/// so submission markers owned by its unsubmitted plan fail with
/// <see cref="EGpuFenceFailureSite.PlanUnsubmitted"/>. It is inert unless armed through the
/// editor MCP tool; an armed fault costs one atomic read per phase boundary.
/// </summary>
public static class VulkanDesktopFrameFaultInjection
{
    /// <summary>Phase boundaries that accept an injected failure, in frame order.</summary>
    public static readonly string[] PointNames =
    [
        nameof(EVulkanDesktopFrameFaultPoint.Acquire),
        nameof(EVulkanDesktopFrameFaultPoint.ImagePreparation),
        nameof(EVulkanDesktopFrameFaultPoint.SceneRecording),
        nameof(EVulkanDesktopFrameFaultPoint.OverlayRecording),
        nameof(EVulkanDesktopFrameFaultPoint.Submission),
        nameof(EVulkanDesktopFrameFaultPoint.PostSubmitAuxiliary),
        nameof(EVulkanDesktopFrameFaultPoint.Presentation),
        nameof(EVulkanDesktopFrameFaultPoint.PostPresentAuxiliary),
    ];

    /// <summary>
    /// Arms the current Vulkan renderer to fail the <paramref name="occurrence"/>th upcoming
    /// observation of <paramref name="pointName"/>. A new request replaces a pending one.
    /// </summary>
    public static bool TryArm(string pointName, int occurrence, out string? failure)
    {
        if (!Enum.TryParse(pointName, ignoreCase: true, out EVulkanDesktopFrameFaultPoint point)
            || point == EVulkanDesktopFrameFaultPoint.None
            || !Enum.IsDefined(point))
        {
            failure = $"Unknown fault point '{pointName}'. Use one of: {string.Join(", ", PointNames)}.";
            return false;
        }
        if (occurrence <= 0)
        {
            failure = "occurrence must be at least 1.";
            return false;
        }
        if (!TryGetDesktopRenderer(out VulkanRenderer? renderer, out failure))
            return false;

        renderer!.ArmDesktopFrameFaultInjection(point, occurrence);
        return true;
    }

    /// <summary>Clears any pending injected failure on the desktop Vulkan renderer.</summary>
    public static bool TryClear(out string? failure)
    {
        if (!TryGetDesktopRenderer(out VulkanRenderer? renderer, out failure))
            return false;

        renderer!.ClearDesktopFrameFaultInjection();
        return true;
    }

    // Callers run on tool threads, where AbstractRenderer.Current can name another
    // renderer between frames. The first window's renderer is the desktop renderer.
    private static bool TryGetDesktopRenderer(out VulkanRenderer? renderer, out string? failure)
    {
        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            if (window.Renderer is VulkanRenderer vulkan)
            {
                renderer = vulkan;
                failure = null;
                return true;
            }
        }

        renderer = null;
        failure = "No window uses a Vulkan renderer.";
        return false;
    }
}
