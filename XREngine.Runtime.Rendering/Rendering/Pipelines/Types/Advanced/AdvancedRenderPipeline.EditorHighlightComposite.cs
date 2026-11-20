using System;
using System.IO;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.EditorHighlight.Contracts;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using static XREngine.RuntimeEngine.Rendering.State;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private static string GetEditorHighlightCompositeFboName(string sourceTextureName)
        => sourceTextureName switch
        {
            FinalPostProcessOutputTextureName => "EditorHighlightCompositeFinal",
            FxaaOutputTextureName => "EditorHighlightCompositeFxaa",
            SmaaOutputTextureName => "EditorHighlightCompositeSmaa",
            TsrOutputTextureName => "EditorHighlightCompositeTsr",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceTextureName), sourceTextureName,
                "The editor highlight compositor requires a resolved scene color source.")
        };

    private XRFrameBuffer CreateEditorHighlightCompositeFbo(string sourceTextureName)
    {
        if (EditorHighlightModule is null)
            throw new InvalidOperationException("The editor highlight compositor requires an installed module.");

        XRTexture source = RequirePostProcessTexture(sourceTextureName);
        XRTexture mask = RequirePostProcessTexture(EditorHighlightMaskTextureName);
        XRTexture silhouette = RequirePostProcessTexture(EditorHighlightSilhouetteMaskTextureName);
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath,
            Stereo ? "EditorHighlightCompositeStereo.fs" : "EditorHighlightComposite.fs"), EShaderType.Fragment);
        XRMaterial material = new([source, mask, silhouette], shader)
        {
            RenderOptions = new RenderingParameters
            {
                DepthTest = new DepthTest
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions |
                    EUniformRequirements.ClipSpacePolicy,
            }
        };
        XRQuadFrameBuffer fbo = new(material, deriveRenderTargetsFromMaterial: false, useMultiview: Stereo)
        {
            Name = GetEditorHighlightCompositeFboName(sourceTextureName)
        };
        fbo.FullScreenMesh.BindingPublishers.Add(new EditorHighlightCompositePublisher(this, material, sourceTextureName));
        return fbo;
    }

    /// <summary>Captures editor colors and source resources independently of scene post-processing.</summary>
    private sealed class EditorHighlightCompositePublisher(
        AdvancedRenderPipeline owner, XRMaterial material, string sourceTextureName) : IRenderResourceBindingPublisher
    {
        private readonly RenderBindingGenerationTracker<EditorHighlightCompositeKey> _generation = new();

        public ERenderBindingFrequency Frequency => ERenderBindingFrequency.Pass;
        public ulong Generation => _generation.Advance(CaptureKey());
        public ulong ResourceGeneration => Generation;

        private EditorHighlightViewSettings CaptureSettings()
            => (owner.EditorHighlightModule ?? throw new InvalidOperationException(
                "The editor highlight module changed before its resource generation was rebuilt.")).CaptureViewSettings();

        private void ResolveCompositeJitterUv(out Vector2 left, out Vector2 right)
        {
            if (!string.Equals(sourceTextureName, TsrOutputTextureName, StringComparison.Ordinal) ||
                CurrentRenderingPipeline is not { } pipeline ||
                !VPRC_TemporalAccumulationPass.TryGetTsrResolveUniformData(pipeline, out var temporalData))
            {
                left = Vector2.Zero;
                right = Vector2.Zero;
                return;
            }

            float internalWidth = Math.Max(1u, InternalWidth);
            float internalHeight = Math.Max(1u, InternalHeight);
            left = new Vector2(temporalData.CurrentJitter.X / internalWidth, temporalData.CurrentJitter.Y / internalHeight);
            right = new Vector2(temporalData.RightEyeCurrentJitter.X / internalWidth, temporalData.RightEyeCurrentJitter.Y / internalHeight);
        }

        private EditorHighlightCompositeKey CaptureKey()
        {
            ResolveCompositeJitterUv(out Vector2 leftJitter, out Vector2 rightJitter);
            return new(
                owner.RequirePostProcessTexture(sourceTextureName),
                owner.RequirePostProcessTexture(EditorHighlightMaskTextureName),
                owner.RequirePostProcessTexture(EditorHighlightSilhouetteMaskTextureName),
                CurrentRenderingPipeline?.ResourceGeneration ?? 0,
                CaptureSettings(),
                ResolveLensDistortionInputs(ResolveCurrentSettingsCamera()?.GetActivePostProcessState()),
                leftJitter,
                rightJitter);
        }

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            EditorHighlightViewSettings settings = CaptureSettings();
            ResolveCompositeJitterUv(out Vector2 leftJitter, out Vector2 rightJitter);
            materialProgram.Uniform("HoverOutlineColor", settings.HoverColor);
            materialProgram.Uniform("SelectionOutlineColor", settings.SelectionColor);
            materialProgram.Uniform("HoverOutlineEnabled", settings.HoverEnabled);
            materialProgram.Uniform("SelectionOutlineEnabled", settings.SelectionEnabled);
            materialProgram.Uniform("CurrentJitterUv", leftJitter);
            if (owner.Stereo)
                materialProgram.Uniform("CurrentJitterUvRight", rightJitter);
            ApplyLensDistortionUniforms(ResolveCurrentSettingsCamera()?.GetActivePostProcessState(), materialProgram, enabled: true);
        }

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            owner.BindCurrentPostProcessTexture(material, materialProgram, sourceTextureName, 0, "SceneColorTexture");
            owner.BindCurrentPostProcessTexture(material, materialProgram, EditorHighlightMaskTextureName, 1);
            owner.BindCurrentPostProcessTexture(material, materialProgram, EditorHighlightSilhouetteMaskTextureName, 2);
        }
    }

    private readonly record struct EditorHighlightCompositeKey(
        XRTexture Source,
        XRTexture Mask,
        XRTexture SilhouetteMask,
        int ResourceGeneration,
        EditorHighlightViewSettings Settings,
        LensDistortionInputs Lens,
        Vector2 CurrentJitterUv,
        Vector2 CurrentJitterUvRight);
}
