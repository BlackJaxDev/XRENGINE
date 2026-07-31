namespace XREngine.Rendering.Vulkan;

/// <summary>Selects the immutable Vulkan target policy before native bootstrap begins.</summary>
internal static class VulkanRendererTargetDriverFactory
{
    public static IVulkanRendererTargetDriver Create(RendererHostContext hostContext)
        => hostContext.ExecutionMode switch
        {
            RenderExecutionMode.DesktopWsi => new VulkanDesktopWsiTargetDriver(hostContext),
            RenderExecutionMode.Presentationless or RenderExecutionMode.Component =>
                new VulkanPresentationlessTargetDriver(hostContext),
            RenderExecutionMode.HeadlessWsi => new VulkanHeadlessWsiTargetDriver(hostContext),
            RenderExecutionMode.OpenXr => new VulkanOpenXrTargetDriver(hostContext),
            _ => throw new ArgumentOutOfRangeException(
                nameof(hostContext),
                hostContext.ExecutionMode,
                "Unsupported Vulkan renderer execution mode."),
        };
}
