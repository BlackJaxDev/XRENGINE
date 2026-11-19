using System;
using System.IO;
using XREngine.Data.Rendering;
using XREngine.Rendering.EditorHighlight.Contracts;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.Models.Materials;
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
        XRShader shader = XRShader.EngineShader(Path.Combine(SceneShaderPath,
            Stereo ? "EditorHighlightCompositeStereo.fs" : "EditorHighlightComposite.fs"), EShaderType.Fragment);
        XRMaterial material = new([source, mask], shader)
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

        private EditorHighlightCompositeKey CaptureKey()
            => new(
                owner.RequirePostProcessTexture(sourceTextureName),
                owner.RequirePostProcessTexture(EditorHighlightMaskTextureName),
                CurrentRenderingPipeline?.ResourceGeneration ?? 0,
                CaptureSettings(),
                ResolveLensDistortionInputs(ResolveCurrentSettingsCamera()?.GetActivePostProcessState()));

        public void PublishUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            EditorHighlightViewSettings settings = CaptureSettings();
            materialProgram.Uniform("HoverOutlineColor", settings.HoverColor);
            materialProgram.Uniform("SelectionOutlineColor", settings.SelectionColor);
            materialProgram.Uniform("HoverOutlineEnabled", settings.HoverEnabled);
            materialProgram.Uniform("SelectionOutlineEnabled", settings.SelectionEnabled);
            ApplyLensDistortionUniforms(ResolveCurrentSettingsCamera()?.GetActivePostProcessState(), materialProgram, enabled: true);
        }

        public void PublishResources(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
        {
            owner.BindCurrentPostProcessTexture(material, materialProgram, sourceTextureName, 0, "SceneColorTexture");
            owner.BindCurrentPostProcessTexture(material, materialProgram, EditorHighlightMaskTextureName, 1);
        }
    }

    private readonly record struct EditorHighlightCompositeKey(
        XRTexture Source,
        XRTexture Mask,
        int ResourceGeneration,
        EditorHighlightViewSettings Settings,
        LensDistortionInputs Lens);
}
