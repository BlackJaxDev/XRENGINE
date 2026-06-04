using System;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
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

        private static bool IsAutoExposureRestrictedPass()
            => RuntimeEngine.Rendering.State.IsLightProbePass
            || RuntimeEngine.Rendering.State.IsShadowPass
            || RuntimeEngine.Rendering.State.IsSceneCapturePass;

        private static string DescribeRestrictedPass()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass)
                return "light-probe";

            if (RuntimeEngine.Rendering.State.IsShadowPass)
                return "shadow";

            if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return "scene-capture";

            return "restricted";
        }

        protected override void Execute()
        {
            int passIndex = ResolvePassIndex(nameof(VPRC_ExposureUpdate));
            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            var stage = ActivePipelineInstance.RenderState.SceneCamera?.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) != true || grading is null)
            {
                Debug.Rendering("[ExposureUpdate] No ColorGradingSettings stage found on camera");
                return;
            }

            grading.MarkGpuAutoExposureReady(false);

            if (RuntimeEngine.StartupPresentationEnabled)
                return;

            if (IsAutoExposureRestrictedPass())
            {
                Debug.RenderingWarningEvery(
                    $"ExposureUpdate.Skip.{DescribeRestrictedPass()}",
                    TimeSpan.FromSeconds(1),
                    "[ExposureUpdate] Skipping auto exposure during {0} pass.",
                    DescribeRestrictedPass());
                return;
            }

            var sourceTexture = ActivePipelineInstance.GetTexture<XRTexture>(HDRSceneTextureName);
            if (sourceTexture is null)
            {
                Debug.Rendering($"[ExposureUpdate] Source texture '{HDRSceneTextureName}' not found");
                return;
            }

            var renderer = AbstractRenderer.Current;
            if (renderer?.SupportsGpuAutoExposure == true)
            {
                var exposureTexture = ActivePipelineInstance.GetTexture<XRTexture2D>(AutoExposureTextureName);
                if (exposureTexture is not null)
                {
                    grading.UpdateExposureGpu(sourceTexture, exposureTexture, GenerateMipmapsHere);
                    if (grading.UseGpuAutoExposureThisFrame)
                        return;

                    Debug.Rendering("[ExposureUpdate] GPU exposure update failed, falling back to CPU");
                }
                else
                {
                    Debug.Rendering($"[ExposureUpdate] Exposure texture '{AutoExposureTextureName}' not found, falling back to CPU");
                }
            }
            else
            {
                //Debug.Out($"[ExposureUpdate] GPU auto exposure not supported, using CPU path");
            }

            grading.UpdateExposure(sourceTexture, GenerateMipmapsHere);
        }

        public void SetOptions(string hdrSceneTextureName, bool generateMipmapsHere)
        {
            HDRSceneTextureName = hdrSceneTextureName;
            GenerateMipmapsHere = generateMipmapsHere;
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            foreach (var pass in metadata)
            {
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return pass.PassIndex;
            }

            return int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var pass = context.GetOrCreateSyntheticPass(nameof(VPRC_ExposureUpdate), ERenderGraphPassStage.Compute);
            pass.SampleTexture(MakeTextureResource(HDRSceneTextureName));
            pass.ReadWriteTexture(MakeTextureResource(AutoExposureTextureName));
        }
    }
}
