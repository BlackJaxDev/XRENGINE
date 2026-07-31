using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// OpenXR target policy. Runtime-provided Vulkan extension and device ownership
/// requirements remain merged by the common OpenXR bootstrap path.
/// </summary>
internal sealed class VulkanOpenXrTargetDriver : IVulkanRendererTargetDriver
{
    private readonly RenderTargetOutputProperties _output;

    public VulkanOpenXrTargetDriver(RendererHostContext hostContext)
    {
        _output = hostContext.OutputProperties
            ?? throw new InvalidOperationException("An OpenXR Vulkan target requires fixed output properties.");
        _output.Validate();
    }

    public RenderExecutionMode ExecutionMode => RenderExecutionMode.OpenXr;
    public bool RequiresPresentQueue => false;
    public bool RequiresSwapchainOutput => false;
    public bool SupportsStreamlinePresentation => false;
    public IReadOnlyList<string> RequiredDeviceExtensions => [];

    public string[] GetRequiredInstanceExtensions() => [];
    public void CreateInstanceResources(VulkanRenderer renderer) { }
    public void InitializeFinalOutput(VulkanRenderer renderer) { }
    public void DestroyFinalOutput(VulkanRenderer renderer) { }
    public void DestroyInstanceResources(VulkanRenderer renderer) { }

    /// <summary>
    /// Maps an image already acquired and waited by the OpenXR runtime binding.
    /// The lease deliberately carries no Vulkan acquire/present semaphore:
    /// runtime release remains outside the renderer and occurs after submission.
    /// </summary>
    public static VulkanFrameTargetLease MapRuntimeOwnedImage(
        in VulkanRenderFrameTarget target,
        Format colorFormat,
        Format depthFormat,
        SampleCountFlags samples,
        uint imageIndex,
        uint viewIndex,
        bool supportsHiddenAreaMask)
    {
        VulkanRenderFrameTarget mappedTarget = target with
        {
            TargetGeneration = 1,
            FrameSlotIndex = imageIndex,
        };
        return new(
            mappedTarget,
            colorFormat,
            depthFormat,
            samples,
            imageIndex,
            Result.Success,
            default,
            0,
            default,
            default,
            VulkanFrameTargetCompletionKind.OpenXrRuntimeRelease,
            ImagesExternallyOwned: true,
            viewIndex,
            supportsHiddenAreaMask);
    }
}
