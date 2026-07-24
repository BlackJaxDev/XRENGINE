namespace XREngine.Rendering.Compute;

/// <summary>
/// Marker identity used by the GPU soft-body dispatcher without depending on
/// the concrete scene component that owns authoring and CPU lifecycle.
/// </summary>
public interface IGpuSoftbodyComputeSource;
