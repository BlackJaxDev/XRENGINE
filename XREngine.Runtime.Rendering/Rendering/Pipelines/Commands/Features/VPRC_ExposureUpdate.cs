using System;
using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_ExposureUpdate : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            if (requirements.Backend != RendererBackendId.WebGPU)
            {
                base.DescribeRequirements(requirements);
                return;
            }
            if (requirements.PostProcessState.GetStage<ColorGradingSettings>()?.TryGetBacking(out ColorGradingSettings? settings) == true &&
                settings is { RequiresAutoExposure: true })
            {
                requirements.RequireOperation("storage-images");
                requirements.RequireComputeProgram(GpuProgramBinding ?? "auto-exposure");
            }
        }

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

        /// <summary>The exact package compute binding selected by the owning output pipeline.</summary>
        public string? GpuProgramBinding { get; set; }

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

        internal static bool TryUseAutoExposureSource(
            XRTexture sourceTexture,
            out EVrAutoExposurePolicy policy,
            out string reason)
        {
            policy = EVrAutoExposurePolicy.HeadsetShared;

            if (!IsVrOrStereoExposureContext())
            {
                reason = "mono rendering uses the normal shared exposure target.";
                return true;
            }

            if (sourceTexture is XRTexture2DArray stereoArray && stereoArray.Depth >= 2u)
            {
                reason = "HeadsetShared auto exposure averages the stereo array layers into one 1x1 exposure texture.";
                return true;
            }

            reason = "VR HeadsetShared auto exposure requires a stereo array source; external per-eye swapchain targets are skipped to avoid last-eye-wins exposure state.";
            return false;
        }

        // Being in VR does not make every viewport an eye. A desktop viewport keeps its own mono exposure.
        private static bool IsVrOrStereoExposureContext()
            => RuntimeEngine.Rendering.State.IsStereoPass
            || AbstractRenderer.Current?.IsRenderingExternalSwapchainTarget == true;

        private static void ReportVrAutoExposurePolicy(
            EVrAutoExposurePolicy policy,
            XRTexture sourceTexture,
            string reason)
        {
            if (!IsVrOrStereoExposureContext())
                return;

            Debug.RenderingEvery(
                $"ExposureUpdate.VrPolicy.{policy}.{sourceTexture.GetType().Name}",
                TimeSpan.FromSeconds(5),
                "[ExposureUpdate] VR auto exposure policy={0} source={1} reason={2}",
                policy,
                sourceTexture.GetType().Name,
                reason);
        }

        private static void ReportSkippedVrAutoExposure(
            EVrAutoExposurePolicy policy,
            XRTexture sourceTexture,
            string reason)
            => Debug.RenderingWarningEvery(
                $"ExposureUpdate.VrPolicySkipped.{policy}.{sourceTexture.GetType().Name}",
                TimeSpan.FromSeconds(1),
                "[ExposureUpdate] Skipping VR auto exposure policy={0} source={1}: {2}",
                policy,
                sourceTexture.GetType().Name,
                reason);

        protected override void Execute()
        {
            var camera = ActivePipelineInstance.RenderState.SceneCamera ?? ActivePipelineInstance.LastSceneCamera;
            var stage = camera?.GetPostProcessStageState<ColorGradingSettings>();
            if (stage?.TryGetBacking(out ColorGradingSettings? grading) != true || grading is null)
                return;
            if (!grading.RequiresAutoExposure)
            {
                grading.MarkGpuAutoExposureReady(false);
                return;
            }

            int passIndex = ResolvePassIndex(nameof(VPRC_ExposureUpdate));
            if (passIndex == int.MinValue)
            {
                Debug.RenderingWarningEvery(
                    "ExposureUpdate.MissingRenderGraphPass",
                    TimeSpan.FromSeconds(1),
                    "[ExposureUpdate] Skipping GPU auto exposure because no render-graph pass metadata was generated for '{0}'.",
                    nameof(VPRC_ExposureUpdate));
                return;
            }

            using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex);

            if (RuntimeEngine.StartupPresentationEnabled)
                return;

            XRViewport? viewport = ActivePipelineInstance.RenderState.WindowViewport ?? ActivePipelineInstance.LastWindowViewport;
            if (viewport?.Suppress3DSceneRendering == true)
            {
                Debug.RenderingEvery(
                    "ExposureUpdate.Skip.Suppress3DSceneRendering",
                    TimeSpan.FromSeconds(2),
                    "[ExposureUpdate] Holding auto exposure during suppressed 3D viewport frame.");
                return;
            }

            if (viewport?.SuppressAutoExposureUpdates == true)
            {
                Debug.RenderingEvery(
                    "ExposureUpdate.Skip.SuppressAutoExposureUpdates",
                    TimeSpan.FromSeconds(2),
                    "[ExposureUpdate] Holding auto exposure while the viewport requested exposure stability.");
                return;
            }

            if (GlobalIlluminationDiagnosticPresentation.IsCurrentFrame(ActivePipelineInstance))
                return;

            grading.MarkGpuAutoExposureReady(false);

            if (IsAutoExposureRestrictedPass())
            {
                Debug.RenderingWarningEvery(
                    $"ExposureUpdate.Skip.{DescribeRestrictedPass()}",
                    TimeSpan.FromSeconds(1),
                    "[ExposureUpdate] Skipping auto exposure during {0} pass.",
                    DescribeRestrictedPass());
                return;
            }

            if (DefaultRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath)
            {
                Debug.RenderingEvery(
                    "ExposureUpdate.Skip.OpenXrVulkanDesktopStartup",
                    TimeSpan.FromSeconds(2),
                    "[ExposureUpdate] Holding auto exposure on OpenXR Vulkan desktop startup/mirror frame.");
                return;
            }

            var sourceTexture = ActivePipelineInstance.GetTexture<XRTexture>(HDRSceneTextureName);
            if (sourceTexture is null)
            {
                Debug.Rendering($"[ExposureUpdate] Source texture '{HDRSceneTextureName}' not found");
                return;
            }

            if (!TryUseAutoExposureSource(sourceTexture, out EVrAutoExposurePolicy exposurePolicy, out string exposurePolicyReason))
            {
                ReportSkippedVrAutoExposure(exposurePolicy, sourceTexture, exposurePolicyReason);
                return;
            }

            ReportVrAutoExposurePolicy(exposurePolicy, sourceTexture, exposurePolicyReason);

            var renderer = AbstractRenderer.Current;
            if (Shaders.Compilation.WebPipelineRasterProgram.IsActive)
            {
                XRTexture2D exposure = ActivePipelineInstance.GetTexture<XRTexture2D>(AutoExposureTextureName)
                    ?? throw new InvalidOperationException("WebGPU.AutoExposure.OutputMissing: automatic exposure requires its declared GPU history target.");
                if (renderer?.SupportsGpuAutoExposure != true)
                    throw new NotSupportedException("WebGPU.AutoExposure.Unavailable: the selected renderer has no GPU exposure producer.");
                grading.UpdateExposureGpu(sourceTexture, exposure, false, GpuProgramBinding ?? "auto-exposure");
                // A warming producer marks the entire renderer frame pending.
                // Let that frame retry through its normal GPU lifecycle.
                return;
            }
            if (renderer?.SupportsGpuAutoExposure == true)
            {
                var exposureTexture = ActivePipelineInstance.GetTexture<XRTexture2D>(AutoExposureTextureName);
                if (exposureTexture is not null)
                {
                    if (TryConsumeSharedAutoExposure(renderer, grading, exposureTexture))
                        return;

                    grading.UpdateExposureGpu(sourceTexture, exposureTexture, GenerateMipmapsHere);
                    if (grading.UseGpuAutoExposureThisFrame)
                    {
                        PublishSharedAutoExposure(renderer, exposureTexture);
                        return;
                    }

                    Debug.Rendering("[ExposureUpdate] GPU exposure update not ready; using CPU exposure for this frame");
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

        /// <summary>
        /// Copies this pipeline instance's new exposure into its shared channel, so that
        /// instances that render later in the frame (the quad-view inset pair) use the same
        /// exposure. Does nothing when the instance publishes to no channel.
        /// </summary>
        private void PublishSharedAutoExposure(AbstractRenderer renderer, XRTexture2D exposureTexture)
        {
            if (ActivePipelineInstance.PublishesAutoExposureTo is not { } channel)
                return;

            if (renderer.PublishAutoExposureGpu(exposureTexture, channel.Buffer))
            {
                channel.MarkPublished(RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId);
                return;
            }

            Debug.RenderingWarningEvery(
                $"ExposureUpdate.SharedPublishFailed.{ActivePipelineInstance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[ExposureUpdate] Could not publish the shared auto exposure of pipeline instance {0}. Consumers meter their own scene this frame.",
                ActivePipelineInstance.InstanceId);
        }

        /// <summary>
        /// For an instance that consumes a shared channel, copies the exposure that the
        /// publisher wrote this frame into this instance's exposure texture and skips metering.
        /// Returns false when the instance consumes no channel, or when no value was published
        /// this frame; the caller then meters this instance's own scene image.
        /// </summary>
        private bool TryConsumeSharedAutoExposure(AbstractRenderer renderer, ColorGradingSettings grading, XRTexture2D exposureTexture)
        {
            if (ActivePipelineInstance.ConsumesAutoExposureFrom is not { } channel)
                return false;

            ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
            if (channel.PublishedRenderFrameId == renderFrameId)
            {
                grading.GetResolvedExposureBounds(out float minExposure, out float maxExposure);
                float fallbackExposure = grading.ExposureMode == ColorGradingSettings.ExposureControlMode.Physical
                    ? grading.ComputePhysicalExposureMultiplier()
                    : grading.Exposure;
                bool consumed = renderer.ConsumeAutoExposureGpu(
                    channel.Buffer,
                    exposureTexture,
                    minExposure,
                    maxExposure,
                    Math.Clamp(fallbackExposure, minExposure, maxExposure));
                grading.MarkGpuAutoExposureReady(consumed);
                if (consumed)
                    return true;
            }

            Debug.RenderingWarningEvery(
                $"ExposureUpdate.SharedConsumeUnavailable.{ActivePipelineInstance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[ExposureUpdate] Pipeline instance {0} has no shared auto exposure for render frame {1} (published frame {2}). It meters its own scene this frame.",
                ActivePipelineInstance.InstanceId,
                renderFrameId,
                channel.PublishedRenderFrameId);
            return false;
        }

        public void SetOptions(string hdrSceneTextureName, bool generateMipmapsHere)
        {
            HDRSceneTextureName = hdrSceneTextureName;
            GenerateMipmapsHere = generateMipmapsHere;
        }

        private int ResolvePassIndex(string passName)
        {
            if (ParentPipeline?.TryGetRenderPassIndex(passName, out int passIndex) == true)
                return passIndex;

            return RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline
                ?.TryGetRenderPassIndex(passName, out passIndex) == true
                ? passIndex
                : int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (Shaders.Compilation.WebPipelineRasterProgram.IsActive && context.ResourceLayout is not null &&
                !context.HasResource(AutoExposureTextureName))
            {
                context.ReserveSyntheticPassIndex(nameof(VPRC_ExposureUpdate));
                return;
            }

            var pass = context.GetOrCreateSyntheticPass(nameof(VPRC_ExposureUpdate), ERenderGraphPassStage.Compute);
            pass.SampleTexture(MakeTextureResource(HDRSceneTextureName));
            pass.ReadWriteTexture(MakeTextureResource(AutoExposureTextureName));
        }
    }
}
