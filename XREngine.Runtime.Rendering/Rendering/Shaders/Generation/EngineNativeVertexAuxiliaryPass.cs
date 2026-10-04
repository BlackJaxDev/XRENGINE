namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Exact raster companions which replay the material's isolated local vertex function.</summary>
public enum EngineNativeVertexAuxiliaryPass
{
    DepthNormal,
    DirectionalShadow,
    PointShadow,
    SpotShadow,
    DirectionalReceiver,
    LocalReceiver,
}
