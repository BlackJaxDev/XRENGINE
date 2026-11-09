using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

/// <summary>
/// Process-wide support state of the Advanced mesh surface decal lane. A backend that records
/// the decal buffer phase enables it before the first scene publication. Without support,
/// surface decal materials keep their masked fallback and each one reports one diagnostic.
/// </summary>
public static class AdvancedSurfaceDecalLane
{
    private static readonly ConditionalWeakTable<XRMaterial, object> s_reportedFallbacks = new();
    private static volatile bool s_backendSupported;

    /// <summary>True when the active Advanced backend records the decal buffer phase.</summary>
    public static bool BackendSupported => s_backendSupported;

    /// <summary>
    /// Called by a backend that records the decal buffer phase. <c>XRE_ADVANCED_SURFACE_DECALS=0</c>
    /// keeps the lane off, so surface decals stay masked.
    /// </summary>
    public static void EnableBackendSupport()
        => s_backendSupported = XREnvironment.IsEnabled(XREngineEnvironmentVariables.AdvancedSurfaceDecals, defaultValue: true);

    /// <summary>
    /// Returns true when <paramref name="material"/> uses the decal lane. A surface decal on a
    /// backend without the lane reports one diagnostic and keeps its masked fallback.
    /// </summary>
    public static bool UsesDecalLane(XRMaterial? material)
    {
        if (material is not { AdvancedSurfaceDecal: true })
            return false;
        if (s_backendSupported)
            return true;
        if (s_reportedFallbacks.TryAdd(material, s_reportedFallbacks))
            Debug.RenderingWarning(
                "[AdvancedPipeline] Surface decal material '{0}' is drawn as masked: the active backend has no surface decal lane.",
                material.Name ?? "<unnamed>");
        return false;
    }
}
