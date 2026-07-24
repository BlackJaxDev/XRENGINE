using XREngine.Data.Geometry;

namespace XREngine.Rendering;

/// <summary>
/// Exposes optional backend diagnostics to editor and automation tooling.
/// </summary>
public interface IRenderBackendDiagnosticsCapability
{
    IReadOnlyList<RenderBackendDiagnosticError> GetTrackedErrors()
        => Array.Empty<RenderBackendDiagnosticError>();

    void ClearTrackedErrors()
    {
    }

    IReadOnlyList<string> AvailableDeviceExtensions
        => Array.Empty<string>();

    IReadOnlyList<string> EnabledDeviceExtensions
        => Array.Empty<string>();

    object GetLiveImageAllocationDiagnostics(int limit)
        => Array.Empty<object>();

    object GetLastFrameOperationTraceDiagnostics(int limit, string? targetContains)
        => Array.Empty<object>();

    bool TryReadDepthPixelDebug(
        XRFrameBuffer frameBuffer,
        int x,
        int y,
        out object? diagnostic)
    {
        diagnostic = null;
        return false;
    }

    string? EffectiveRenderTargetMode
        => null;
}
