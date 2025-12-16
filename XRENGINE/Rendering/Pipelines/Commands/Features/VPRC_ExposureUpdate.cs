
using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_ExposureUpdate : ViewportRenderCommand
    {
        /// <summary>
        /// This is the texture that exposure will be calculated from.
        /// </summary>
        public string HDRSceneTextureName { get; set; } = DefaultRenderPipeline.HDRSceneTextureName;

        /// <summary>
        /// The 1x1 texture that stores the current exposure value when using GPU auto exposure.
        /// </summary>
        public string AutoExposureTextureName { get; set; } = DefaultRenderPipeline.AutoExposureTextureName;

        /// <summary>
        /// If true, the command will generate mipmaps for the HDR texture.
        /// Set to false if you've already generated mipmaps before this command.
        /// </summary>
        public bool GenerateMipmapsHere { get; set; } = true;

        protected override void Execute()
        {
            var stage = ActivePipelineInstance.RenderState.SceneCamera?.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) != true)
            {
                Debug.Out("[ExposureUpdate] No ColorGradingSettings stage found on camera");
                return;
            }

            var sourceTexture = ActivePipelineInstance.GetTexture<XRTexture>(HDRSceneTextureName);
            if (sourceTexture is null)
            {
                Debug.Out($"[ExposureUpdate] Source texture '{HDRSceneTextureName}' not found");
                return;
            }

            var renderer = AbstractRenderer.Current;
            if (renderer?.SupportsGpuAutoExposure == true)
            {
                var exposureTexture = ActivePipelineInstance.GetTexture<XRTexture2D>(AutoExposureTextureName);
                if (exposureTexture is not null)
                {
                    grading.UpdateExposureGpu(sourceTexture, exposureTexture, GenerateMipmapsHere);
                    return;
                }
                else
                {
                    Debug.Out($"[ExposureUpdate] Exposure texture '{AutoExposureTextureName}' not found, falling back to CPU");
                }
            }
            else
            {
                Debug.Out($"[ExposureUpdate] GPU auto exposure not supported, using CPU path");
            }

            grading.UpdateExposure(sourceTexture, GenerateMipmapsHere);
        }

        public void SetOptions(string hdrSceneTextureName, bool generateMipmapsHere)
        {
            HDRSceneTextureName = hdrSceneTextureName;
            GenerateMipmapsHere = generateMipmapsHere;
        }
    }
}
