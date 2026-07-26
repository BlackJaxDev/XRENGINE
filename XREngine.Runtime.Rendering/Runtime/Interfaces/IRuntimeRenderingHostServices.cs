namespace XREngine.Rendering;

/// <summary>
/// Temporary installation facade that composes the focused runtime-rendering host capabilities.
/// New runtime rendering code should depend on the narrow capability exposed by
/// <see cref="RuntimeRenderingHostServices"/> instead of this aggregate contract.
/// </summary>
public interface IRuntimeRenderingHostServices :
    IRuntimeRenderSettingsServices,
    IRuntimeRenderFrameTimingServices,
    IRuntimeRenderSchedulingServices,
    IRuntimeRenderDiagnosticsServices,
    IRuntimeRenderStatisticsServices,
    IRuntimeRenderDebugDrawingServices,
    IRuntimeRenderProfilingServices,
    IRuntimeRenderAssetServices,
    IRuntimeRendererFactoryServices,
    IRuntimeRenderPresentationServices,
    IRuntimeRenderBackendInteropServices
{
}