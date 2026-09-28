namespace XREngine.Rendering;

/// <summary>CPU-direct focused forward pipeline configuration and synchronous binary frame import.</summary>
public interface IBrowserFocusedPipelineCapability
{
    void ConfigurePipeline(BrowserPipelineQualitySettings settings);
    void ConfigureMaterial(BrowserResourceHandle material, BrowserMaterialData data);
    void SubmitPipelinePacket(BrowserPipelineFramePacket packet);
}
