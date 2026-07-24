namespace XREngine.Rendering;

/// <summary>
/// Notifies a backend that the stable shader-pipeline mode changed.
/// </summary>
public interface IShaderPipelineModeBackendCapability
{
    void HandleShaderPipelineModeChanged(bool allowShaderPipelines);
}
