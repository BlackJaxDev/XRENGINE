using XREngine.Data.Rendering;
using XREngine.Core.Files;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private static void AdaptEditorHighlightFragment(List<XRShader> shaders, uint maskLocation)
    {
        int fragmentIndex = -1;
        for (int i = 0; i < shaders.Count; i++)
        {
            if (shaders[i].Type != EShaderType.Fragment)
                continue;
            if (fragmentIndex >= 0)
                throw new NotSupportedException("The editor highlight output requires exactly one authored fragment shader.");
            fragmentIndex = i;
        }
        if (fragmentIndex < 0)
            throw new NotSupportedException("The editor highlight output requires an authored fragment shader.");

        XRShader source = shaders[fragmentIndex];
        if (source.SourceLanguage != ShaderSourceLanguage.Glsl)
            throw new NotSupportedException("The editor highlight output requires a GLSL fragment source.");
        string failure = "The fragment source could not be resolved.";
        if (!source.TryGetResolvedSource(out string resolved) ||
            !EditorHighlightFragmentSourceAdapter.TryAdaptWithPicking(
                resolved, maskLocation, 2, out string adapted, out failure))
            throw new NotSupportedException(
                $"The editor highlight fragment output is unsupported: {failure}");

        shaders[fragmentIndex] = new XRShader(EShaderType.Fragment, TextFile.FromText(adapted))
        {
            Name = source.Name,
            GenerateAsync = source.GenerateAsync,
            GeneratedUberVariantHash = source.GeneratedUberVariantHash,
        };
    }

    private static EditorHighlightRasterOutput CaptureEditorHighlightOutput(
        XRRenderPipelineInstance? pipeline,
        XRFrameBuffer? target,
        int passIndex)
    {
        if (pipeline?.Pipeline?.ResolveAdvancedStageFamily()?.EditorHighlightModule is null)
            return default;

        if (!pipeline.TryGetTexture(AdvancedRenderPipeline.EditorHighlightMaskTextureName, out XRTexture? mask) ||
            mask is null)
            throw new InvalidOperationException("The enabled editor highlight module has no mask in the active resource generation.");

        bool hasSceneColor = false;
        bool hasMask = false;
        bool hasPicking = false;
        XRTexture? picking = pipeline.GetTexture<XRTexture>(AdvancedRenderPipeline.EditorPickingIdentityTextureName);
        XRTexture? sceneColor = pipeline.GetTexture<XRTexture>(AdvancedRenderPipeline.HDRSceneTextureName);
        if (target?.Targets is { } targets)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly var attachment = ref targets[i];
                if (attachment.Attachment == EFrameBufferAttachment.ColorAttachment0 &&
                    ReferenceEquals(attachment.Target, sceneColor))
                    hasSceneColor = true;
                if (attachment.Attachment == EFrameBufferAttachment.ColorAttachment1 &&
                    ReferenceEquals(attachment.Target, mask))
                    hasMask = true;
                if (attachment.Attachment == EFrameBufferAttachment.ColorAttachment2 &&
                    picking is not null && ReferenceEquals(attachment.Target, picking))
                    hasPicking = true;
            }
        }

        bool sceneColorPass = passIndex is
            (int)EDefaultRenderPass.Background or
            (int)EDefaultRenderPass.OpaqueForward or
            (int)EDefaultRenderPass.MaskedForward or
            (int)EDefaultRenderPass.TransparentForward or
            (int)EDefaultRenderPass.OnTopForward or
            (int)EDefaultRenderPass.PostBloomForward or
            (int)EDefaultRenderPass.PostMotionBlurForward or
            (int)EDefaultRenderPass.PostDepthOfFieldForward;
        if (passIndex is
            (int)EDefaultRenderPass.WeightedBlendedOitForward or
            (int)EDefaultRenderPass.PerPixelLinkedListForward or
            (int)EDefaultRenderPass.DepthPeelingForward)
            throw new NotSupportedException(
                "The editor highlight module requires a mask output for this transparency pass.");
        if (sceneColorPass && hasSceneColor && !hasMask)
            throw new NotSupportedException(
                "The editor highlight module requires its auxiliary mask on each authored scene-color target.");
        if (!hasMask)
            return default;
        if (!hasPicking)
            throw new NotSupportedException("The editor module requires its picking identity beside the highlight mask.");

        return new(
            hasSceneColor && sceneColorPass
                ? EEditorHighlightRasterWriteRole.SceneColor
                : EEditorHighlightRasterWriteRole.Preserve,
            1,
            checked((byte)EditorHighlightFragmentSourceAdapter.AbiVersion));
    }
}
