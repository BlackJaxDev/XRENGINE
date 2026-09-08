using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Transfers one authoritative Advanced resource to the caller-provided offscreen target.
/// This deliberately bypasses presentation, post-processing, and screen-space UI.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_ExportAdvancedOffscreenOutput : ViewportRenderCommand
{
    private ERenderPipelineOffscreenOutput _output;
    private int _passIndex = int.MinValue;

    /// <summary>
    /// Gets or sets the resource class exported to the bound output framebuffer.
    /// </summary>
    public ERenderPipelineOffscreenOutput Output
    {
        get => _output;
        set => SetField(ref _output, value);
    }

    public override string GpuProfilingName
        => $"{base.GpuProfilingName}[{Output}]";

    protected override void Execute()
    {
        if (RuntimeEngine.Rendering.State.HasRequiredOffscreenAuthoringFailure)
            return;

        try
        {
            if (_passIndex == int.MinValue)
                throw new InvalidOperationException("Advanced offscreen export has no declared transfer pass.");
            // Deferred backends order by the declared graph pass. Inheriting the
            // ambient pre-render pass would copy before shading/background writers.
            using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(_passIndex);
            ExecuteRequiredExport();
        }
        catch (Exception ex)
        {
            RuntimeEngine.Rendering.State.RejectRequiredOffscreenAuthoring(
                $"Advanced offscreen {Output} export failed: {ex.Message}");
        }
    }

    private void ExecuteRequiredExport()
    {
        RenderOutputRequest completionRequest = ActivePipelineInstance.RenderState.OutputCompletionRequest;
        if (completionRequest.IsDefined)
        {
            ERenderOutputWriteAspect requiredAspect = Output == ERenderPipelineOffscreenOutput.Depth
                ? ERenderOutputWriteAspect.Depth
                : ERenderOutputWriteAspect.Color;
            if (completionRequest.ExpectedWriteAspect != requiredAspect)
            {
                RuntimeEngine.Rendering.State.RejectRequiredOffscreenAuthoring(
                    $"Advanced offscreen {Output} export requires completion aspect {requiredAspect}, " +
                    $"but the frozen output request expects {completionRequest.ExpectedWriteAspect}.");
                return;
            }
        }

        XRFrameBuffer output = ActivePipelineInstance.RenderState.OutputFBO
            ?? throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires a caller-provided output framebuffer.");
        XRFrameBuffer source = ResolveSourceFrameBuffer();
        AbstractRenderer renderer = AbstractRenderer.Current
            ?? throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires an active renderer.");

        ValidateOutputContract(source, output);
        FrameBufferBlitSubmission submission = Output == ERenderPipelineOffscreenOutput.Depth
            ? renderer.TryBlitFBOToFBO(
                source,
                output,
                EReadBufferMode.None,
                colorBit: false,
                depthBit: true,
                stencilBit: false,
                linearFilter: false)
            : renderer.TryBlitFBOToFBOSingleAttachment(
                source,
                output,
                EReadBufferMode.ColorAttachment0,
                EReadBufferMode.ColorAttachment0,
                linearFilter: false);
        if (!submission.Accepted)
        {
            RuntimeEngine.Rendering.State.RejectRequiredOffscreenAuthoring(
                $"Advanced offscreen {Output} export was rejected by {renderer.GetType().Name}: {submission.Reason}");
        }
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string source = Output switch
        {
            ERenderPipelineOffscreenOutput.HdrColor => MakeFboColorResource(AdvancedRenderPipeline.ForwardPassFBOName),
            ERenderPipelineOffscreenOutput.Depth => MakeFboDepthResource(AdvancedVisibilityResourceNames.FrameBuffer),
            ERenderPipelineOffscreenOutput.Visibility => MakeFboColorResource(AdvancedVisibilityResourceNames.FrameBuffer),
            _ => throw new ArgumentOutOfRangeException(nameof(Output), Output, "Unknown advanced offscreen output."),
        };

        RenderPassBuilder exportPass = context.GetOrCreateSyntheticPass($"AdvancedOffscreenExport.{Output}")
            .WithStage(ERenderGraphPassStage.Transfer)
            .UseTransferSource(source);
        _passIndex = exportPass.PassIndex;
        if (Output == ERenderPipelineOffscreenOutput.Depth)
            exportPass.UseDepthTransferDestination(RenderGraphResourceNames.OutputRenderTarget);
        else
            exportPass.UseTransferDestination(RenderGraphResourceNames.OutputRenderTarget);
    }

    private XRFrameBuffer ResolveSourceFrameBuffer()
    {
        string sourceName = Output switch
        {
            ERenderPipelineOffscreenOutput.HdrColor => AdvancedRenderPipeline.ForwardPassFBOName,
            ERenderPipelineOffscreenOutput.Depth or ERenderPipelineOffscreenOutput.Visibility => AdvancedVisibilityResourceNames.FrameBuffer,
            _ => throw new ArgumentOutOfRangeException(nameof(Output), Output, "Unknown advanced offscreen output."),
        };

        return ActivePipelineInstance.GetFBO<XRFrameBuffer>(sourceName)
            ?? throw new InvalidOperationException(
                $"Advanced offscreen {Output} export source '{sourceName}' was not realized.");
    }

    private void ValidateOutputContract(XRFrameBuffer source, XRFrameBuffer output)
    {
        EFrameBufferAttachment sourceAttachment = Output == ERenderPipelineOffscreenOutput.Depth
            ? EFrameBufferAttachment.DepthStencilAttachment
            : EFrameBufferAttachment.ColorAttachment0;
        EFrameBufferAttachment outputAttachment = sourceAttachment;
        if (!TryGetAttachment(source, sourceAttachment, out var sourceTarget, out int sourceLayer) ||
            !TryGetAttachment(output, outputAttachment, out var outputTarget, out int outputLayer))
        {
            throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires {sourceAttachment} on both source and output framebuffers.");
        }

        ESizedInternalFormat expectedFormat = Output switch
        {
            ERenderPipelineOffscreenOutput.HdrColor => ESizedInternalFormat.Rgba16f,
            ERenderPipelineOffscreenOutput.Visibility => ESizedInternalFormat.Rg32ui,
            ERenderPipelineOffscreenOutput.Depth => ESizedInternalFormat.Depth32fStencil8,
            _ => throw new ArgumentOutOfRangeException(nameof(Output), Output, "Unknown advanced offscreen output."),
        };
        if (!TryGetSizedFormat(sourceTarget, out ESizedInternalFormat sourceFormat) ||
            !TryGetSizedFormat(outputTarget, out ESizedInternalFormat outputFormat) ||
            sourceFormat != expectedFormat || outputFormat != expectedFormat)
        {
            throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires matching {expectedFormat} attachments; " +
                $"source={DescribeFormat(sourceTarget)}, output={DescribeFormat(outputTarget)}.");
        }

        uint sourceLayers = ResolveLayerCount(sourceTarget, sourceLayer);
        uint outputLayers = ResolveLayerCount(outputTarget, outputLayer);
        if (source.Width != output.Width || source.Height != output.Height || sourceLayers != outputLayers)
        {
            throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires matching extent and layer count; " +
                $"source={source.Width}x{source.Height}x{sourceLayers}, output={output.Width}x{output.Height}x{outputLayers}.");
        }
        if (source.EffectiveSampleCount != 1u || output.EffectiveSampleCount != 1u)
        {
            throw new InvalidOperationException(
                $"Advanced offscreen {Output} export requires single-sample source and output attachments; " +
                $"source={source.EffectiveSampleCount}, output={output.EffectiveSampleCount}.");
        }
    }

    private static bool TryGetAttachment(
        XRFrameBuffer frameBuffer,
        EFrameBufferAttachment requested,
        out IFrameBufferAttachement target,
        out int layerIndex)
    {
        if (frameBuffer.Targets is { } targets)
        {
            foreach (var (candidate, attachment, _, candidateLayer) in targets)
            {
                if (attachment == requested ||
                    requested == EFrameBufferAttachment.DepthStencilAttachment && attachment == EFrameBufferAttachment.DepthAttachment)
                {
                    target = candidate;
                    layerIndex = candidateLayer;
                    return true;
                }
            }
        }

        target = null!;
        layerIndex = 0;
        return false;
    }

    private static bool TryGetSizedFormat(IFrameBufferAttachement attachment, out ESizedInternalFormat format)
    {
        switch (attachment)
        {
            case XRTextureCube cube:
                format = cube.SizedInternalFormat;
                return true;
            case XRTexture2D texture:
                format = texture.SizedInternalFormat;
                return true;
            case XRTexture2DArray textureArray:
                format = textureArray.SizedInternalFormat;
                return true;
            case XRTextureViewBase textureView:
                format = textureView.InternalFormat;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static uint ResolveLayerCount(IFrameBufferAttachement attachment, int layerIndex)
    {
        if (layerIndex >= 0)
            return 1u;

        return attachment switch
        {
            XRTexture2DArray textureArray => textureArray.Depth,
            XRTexture2DArrayView textureArrayView => textureArrayView.NumLayers,
            _ => 1u,
        };
    }

    private static string DescribeFormat(IFrameBufferAttachement attachment)
        => TryGetSizedFormat(attachment, out ESizedInternalFormat format) ? format.ToString() : attachment.GetType().Name;
}
