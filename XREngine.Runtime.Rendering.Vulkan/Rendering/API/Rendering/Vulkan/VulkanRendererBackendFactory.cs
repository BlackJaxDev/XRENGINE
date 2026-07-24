using XREngine.Rendering.Vulkan;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering;

/// <summary>
/// Statically linked Vulkan renderer factory. This class moves with the Vulkan leaf module
/// when backend assemblies are split.
/// </summary>
public sealed class VulkanRendererBackendFactory :
    IRendererBackendFactory,
    IOpenXrSmokeDiagnosticsBackendCapability
{
    /// <inheritdoc />
    public void ResetDesktopRejectionEvidence(bool injectionRequested)
        => VulkanRenderer.ResetPhase524bDesktopRejectionEvidence(injectionRequested);

    /// <inheritdoc />
    public OpenXrSmokeDesktopRejectionEvidence CaptureDesktopRejectionEvidence()
        => VulkanRenderer.CapturePhase524bDesktopRejectionEvidence();
    public IRuntimeRendererHost Create(in RendererBackendCreateContext context)
    {
        if (context.Window is not XRWindow window)
        {
            throw new ArgumentException(
                $"The built-in Vulkan backend requires a {nameof(XRWindow)} host, but received " +
                $"'{context.Window?.GetType().FullName ?? "null"}'.",
                nameof(context));
        }

        return new VulkanRenderer(window, context.LinkRendererToWindow);
    }
}
