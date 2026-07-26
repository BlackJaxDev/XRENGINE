namespace XREngine.Rendering;

/// <summary>
/// Publishes scene-database buffer addresses through the active backend without
/// exposing native buffer wrappers to stable rendering code.
/// </summary>
public interface ISceneDatabaseDeviceAddressBackendCapability
{
    bool TryBindSceneDatabaseDeviceAddressUniforms(
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer);
}
