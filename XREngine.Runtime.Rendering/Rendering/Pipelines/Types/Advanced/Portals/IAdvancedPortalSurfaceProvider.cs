namespace XREngine.Rendering;

/// <summary>
/// Supplies an in-place stencil planar mirror or portal surface to
/// <see cref="Commands.AdvancedGpuScenePublisher"/> and <see cref="AdvancedPortalViewScheduler"/>.
/// </summary>
public interface IAdvancedPortalSurfaceProvider
{
    /// <summary>Stable provider identifier used for deterministic scheduling and cycle rejection.</summary>
    ulong PortalProviderId { get; }

    /// <summary>True when this surface participates in in-place stencil portal rendering.</summary>
    bool IsPortalSurfaceActive { get; }

    /// <summary>
    /// Captures the current and previous world-space geometry, reflection/portal transform,
    /// and glass surface parameters for this frame.
    /// </summary>
    /// <param name="descriptor">Receives the captured surface snapshot when this method returns true.</param>
    /// <returns>True when the surface has a valid finite plane and transform.</returns>
    bool TryCapturePortalSurface(out AdvancedPortalSurfaceDescriptor descriptor);
}
