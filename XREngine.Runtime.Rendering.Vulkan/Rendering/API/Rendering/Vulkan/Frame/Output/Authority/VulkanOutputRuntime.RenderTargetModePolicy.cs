namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanOutputRuntime
{
    private const string VulkanRenderTargetModeEnvironmentVariable =
        XREngineEnvironmentVariables.VkRenderTargetMode;

    internal static EVulkanRenderTargetMode ResolveRequestedRenderTargetMode()
    {
        string? value = Environment.GetEnvironmentVariable(
            VulkanRenderTargetModeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return RuntimeEngine.EffectiveSettings.VulkanRenderTargetMode;

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => EVulkanRenderTargetMode.Auto,
            "dynamic" or "dynamicrendering" or "dynamic-rendering" =>
                EVulkanRenderTargetMode.DynamicRendering,
            "legacy" or "renderpass" or "render-pass" or
            "legacyrenderpass" or "legacy-render-pass" =>
                EVulkanRenderTargetMode.LegacyRenderPass,
            _ => throw new InvalidOperationException(
                $"Unsupported {VulkanRenderTargetModeEnvironmentVariable} value '{value}'. Expected Auto, DynamicRendering, or LegacyRenderPass."),
        };
    }
}
