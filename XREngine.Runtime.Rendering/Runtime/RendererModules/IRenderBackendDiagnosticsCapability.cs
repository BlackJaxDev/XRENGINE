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

    object GetLiveResourceOwnerDiagnostics(int top, bool collapseOwnerSuffix, out int groupCount)
    {
        groupCount = 0;
        return new { returned_live = 0, groups = Array.Empty<object>() };
    }

    /// <summary>
    /// Returns the backend memory allocator's statistics document (JSON for VMA), or null when
    /// the active allocator does not provide one. <paramref name="detailedMap"/> adds every block
    /// and allocation, which can be large.
    /// </summary>
    string? GetMemoryAllocatorStatistics(bool detailedMap)
        => null;

    /// <summary>
    /// Describes the backend's retained resource-planner states and the physical image memory each
    /// state's allocator holds, or returns null when the backend has no planner. Call it on the render
    /// thread between frames, because frame recording mutates the planner tables.
    /// </summary>
    object? GetResourcePlannerStateDiagnostics()
        => null;

    object? GetPresentNowTerminalDiagnostics()
        => null;

    object? GetPresentNowFailureDiagnostics()
        => null;

    object? GetDesktopFrameTerminalDiagnostics()
        => null;

    object? GetRetirementDiagnostics()
        => null;

    object? GetValidationDiagnostics()
        => null;

    bool TryCapturePresentNowFailureForFrame(
        long frameAuthorityId,
        out RenderBackendPresentNowFailureSnapshot diagnostic)
    {
        diagnostic = default;
        return false;
    }

    bool TryCaptureMaterialTableDiagnosticsForFrame(
        long frameAuthorityId,
        out RenderBackendMaterialTableDiagnosticsSnapshot diagnostic)
    {
        diagnostic = default;
        return false;
    }

    object GetLastFrameOperationTraceDiagnostics(int limit, string? targetContains, int? pipelineIdentity = null)
        => Array.Empty<object>();

    object GetFinalPresentationLedgerDiagnostics(int limit)
        => Array.Empty<object>();

    object ConfigureFinalPresentationLedgerDiagnostics(
        bool enabled,
        bool frozen,
        bool clear)
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
