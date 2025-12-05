
using XREngine.Rendering;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_ExposureUpdate : ViewportRenderCommand
    {
        /// <summary>
        /// This is the texture that exposure will be calculated from.
        /// </summary>
        public string HDRSceneTextureName { get; set; } = "HDRSceneTexture";

        /// <summary>
        /// If true, the command will generate mipmaps for the HDR texture.
        /// Set to false if you've already generated mipmaps before this command.
        /// </summary>
        public bool GenerateMipmapsHere { get; set; } = true;

        protected override void Execute()
        {
            var stage = ActivePipelineInstance.RenderState.SceneCamera?.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) != true)
                return;

            //var hdrTexture = ActivePipelineInstance.GetTexture<XRTexture>(HDRSceneTextureName);
            //if (hdrTexture is null)
            //    return;

            var vp = ActivePipelineInstance.RenderState.WindowViewport;
            if (vp != null)
                grading?.UpdateExposure(vp.Region);
        }

        public void SetOptions(string hdrSceneTextureName, bool generateMipmapsHere)
        {
            HDRSceneTextureName = hdrSceneTextureName;
            GenerateMipmapsHere = generateMipmapsHere;
        }
    }
}
