using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;

public partial class VulkanRenderer :
    IRenderTexturePreviewBackendCapability,
    IRenderBackendDiagnosticsCapability,
    IOpenXrSmokeDiagnosticsBackendCapability
{
    /// <inheritdoc />
    public bool TryGetTexturePreviewHandle(
        XRTexture texture,
        in RenderTexturePreviewOptions options,
        out nint handle,
        out bool requiresVerticalFlip,
        out string? failureReason)
    {
        IntPtr textureId = RegisterImGuiTexture(texture);
        handle = (nint)textureId;
        requiresVerticalFlip = false;
        failureReason = textureId == IntPtr.Zero
            ? "Texture has not been uploaded to the GPU yet."
            : null;
        return textureId != IntPtr.Zero;
    }

    /// <inheritdoc />
    public IReadOnlyList<RenderBackendDiagnosticError> GetTrackedErrors()
        => Array.Empty<RenderBackendDiagnosticError>();

    /// <inheritdoc />
    object IRenderBackendDiagnosticsCapability.GetLiveImageAllocationDiagnostics(int limit)
        => GetLiveImageAllocationDiagnostics(limit);

    /// <inheritdoc />
    object IRenderBackendDiagnosticsCapability.GetLastFrameOperationTraceDiagnostics(
        int limit,
        string? targetContains)
        => GetLastFrameOpTraceDiagnostics(limit, targetContains);

    /// <inheritdoc />
    bool IRenderBackendDiagnosticsCapability.TryReadDepthPixelDebug(
        XRFrameBuffer frameBuffer,
        int x,
        int y,
        out object? diagnostic)
    {
        bool success = TryReadDepthPixelDebug(frameBuffer, x, y, out VulkanDepthReadbackDebugInfo info);
        diagnostic = info;
        return success;
    }

    /// <inheritdoc />
    string IRenderBackendDiagnosticsCapability.EffectiveRenderTargetMode
        => EffectiveRenderTargetMode.ToString();

    /// <inheritdoc />
    public void ResetDesktopRejectionEvidence(bool injectionRequested)
        => ResetPhase524bDesktopRejectionEvidence(injectionRequested);

    /// <inheritdoc />
    public OpenXrSmokeDesktopRejectionEvidence CaptureDesktopRejectionEvidence()
        => CapturePhase524bDesktopRejectionEvidence();
}
