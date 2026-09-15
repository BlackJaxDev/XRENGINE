using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanXrGraphicsBinding : IOpenXrEyeFrameOpEmitter
{
    /// <summary>Captures published package authority before synchronous eye preparation begins.</summary>
    private bool TryCreateDirectEyeRenderRequest(
        Image image,
        Format format,
        Extent2D extent,
        uint viewIndex,
        uint imageIndex,
        out OpenXrEyeSwapchainRenderRequest request)
    {
        XRViewport? viewport = GetOpenXrEyeViewport(viewIndex);
        if (viewport is null ||
            !viewport.TryCaptureRenderingBackendReadyFramePackageAuthority(
                out BackendReadyFramePackageConsumptionAuthority packageAuthority))
        {
            request = default;
            return LogVulkanEyeRenderNotReady(
                viewIndex,
                imageIndex,
                "no published OpenXR backend-ready frame-package authority");
        }

        request = new(
            image,
            format,
            extent,
            ResourcePlannerStateIndex: checked((int)viewIndex),
            PackageAuthority: packageAuthority,
            OpenXrViewIndex: viewIndex,
            OpenXrImageIndex: imageIndex,
            Foveation: CreateOpenXrEyeFoveationContext(viewIndex),
            FrameOpEmitter: this,
            SubmissionMetadata: new(Context.PendingFrameId, Context.PendingPredictedDisplayTime));
        return true;
    }

    // The emitter runs only during owner-thread preparation. Immutable parallel
    // command inputs contain the resulting operation stream, never this binding.
    void IOpenXrEyeFrameOpEmitter.Emit(in OpenXrEyeFrameOpEmission emission)
    {
        XRViewport viewport = GetOpenXrEyeViewport(emission.ViewIndex)
            ?? throw new InvalidOperationException("OpenXR direct eye preparation requires a viewport.");
        XRCamera camera = GetOpenXrEyeCamera(emission.ViewIndex)
            ?? throw new InvalidOperationException("OpenXR direct eye preparation requires a camera.");
        ApplyOpenXrEyePoseForRenderThread(emission.ViewIndex);
        if (!viewport.TryRenderOpenXrFramePackage(
                _openXrFrameWorld,
                camera,
                emission.PackageAuthority))
        {
            throw new InvalidOperationException(
                "OpenXR direct eye preparation rejected its captured backend-ready frame-package authority.");
        }
    }
}
