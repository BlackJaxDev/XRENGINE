using XREngine.Rendering.PostProcessing;

namespace XREngine.Rendering;

/// <summary>
/// Defines the visual-feature surface shared by desktop, capture, and OpenXR scene pipelines.
/// The output topology may differ while the scene, GI, temporal, froxel, and post-process
/// feature contracts remain compatible.
/// </summary>
public interface ISceneRenderPipelineFeatureProvider :
    IForwardDepthNormalPrePassSettings,
    IGlobalIlluminationPipelineProvider,
    IPbrLightingResourceProvider,
    IRenderPipelinePassMaterialProvider
{
    bool Stereo { get; }
    RenderPipelinePostProcessSchema PostProcessSchema { get; }
}
