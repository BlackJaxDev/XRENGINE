using System;
using System.Numerics;
using System.Threading;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Applies the engine FXAA shader as a standalone composable fullscreen pass.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_FXAA : ViewportRenderCommand
{
    private sealed class FxaaBindingPublisher(
        VPRC_FXAA owner) : IRenderBindingPublisher
    {
        private readonly object _generationSync = new();
        private XRTexture? _lastSource;
        private Vector2 _lastTexelStep = new(float.NaN);
        private long _generation = 1;

        public ERenderBindingFrequency Frequency
            => ERenderBindingFrequency.Pass;

        public ulong Generation
        {
            get
            {
                XRTexture? source = owner._material?.Textures.Count > 0
                    ? owner._material.Textures[0]
                    : null;
                XRRenderPipelineInstance? instance =
                    RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
                Vector2 texelStep = source is not null && instance is not null
                    ? owner.ResolveTexelStep(instance, source)
                    : Vector2.Zero;

                lock (_generationSync)
                {
                    if (ReferenceEquals(source, _lastSource) &&
                        texelStep == _lastTexelStep)
                    {
                        return unchecked((ulong)_generation);
                    }

                    _lastSource = source;
                    _lastTexelStep = texelStep;
                    if (Interlocked.Increment(ref _generation) == 0)
                        Interlocked.CompareExchange(ref _generation, 1, 0);
                    return unchecked((ulong)_generation);
                }
            }
        }

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => owner.Fxaa_SettingUniforms(materialProgram);
    }

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationFBOName { get; set; }
    public bool Stereo { get; set; }

    public override string GpuProfilingName
        => string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName)
            ? nameof(VPRC_FXAA)
            : $"{nameof(VPRC_FXAA)}:{SourceTextureName ?? SourceFBOName}";

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        string shaderName = Stereo ? "FXAAStereo.fs" : "FXAA.fs";
        _material = new(Array.Empty<XRTexture?>(), XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.ViewportDimensions | EUniformRequirements.ClipSpacePolicy
            }
        };

        _quad = new XRQuadFrameBuffer(_material);
        _quad.FullScreenMesh.BindingPublishers.Add(
            new FxaaBindingPublisher(this));
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.Destroy();
            _quad = null;
        }

        _material?.Destroy();
        _material = null;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (_quad is null ||
            !VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
            return;

        XRFrameBuffer? destination = null;
        if (!string.IsNullOrWhiteSpace(DestinationFBOName))
        {
            destination = instance.GetFBO<XRFrameBuffer>(DestinationFBOName!);
            if (destination is null)
                return;
        }

        if (_material is not null &&
            (_material.Textures.Count != 1 || !ReferenceEquals(_material.Textures[0], sourceTexture)))
        {
            _material.Textures.Clear();
            _material.Textures.Add(sourceTexture);
        }

        string destinationName = ResolveDestinationLabel(instance);
        string passName = BuildPassName(destinationName);
        int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
        if (passIndex == int.MinValue && hasRenderGraphMetadata)
        {
            Debug.RenderingWarningEvery(
                $"Fxaa.MissingRenderGraphPass.{passName}",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] Skipping FXAA pass '{0}': no matching render-graph pass metadata was generated.",
                passName);
            return;
        }

        using var passScope = passIndex != int.MinValue
            ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
            : default;
        using var renderAreaScope = destination is { Width: > 0, Height: > 0 }
            ? instance.RenderState.PushRenderArea((int)destination.Width, (int)destination.Height)
            : default;

        if (destination is not null)
            VPRCFullscreenPassContract.ValidateAndLog(instance, nameof(VPRC_FXAA), destination, sourceTexture, Stereo);

        _quad.Render(destination);
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);

        string? source = !string.IsNullOrWhiteSpace(SourceTextureName)
            ? MakeTextureResource(SourceTextureName!)
            : !string.IsNullOrWhiteSpace(SourceFBOName)
                ? MakeFboColorResource(SourceFBOName!)
                : null;

        if (source is null)
            return;

        string destination = DestinationFBOName
            ?? context.CurrentRenderTarget?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

        context.GetOrCreateSyntheticPass(BuildPassName(destination))
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
    }

    private string ResolveDestinationLabel(XRRenderPipelineInstance instance)
        => DestinationFBOName
            ?? instance.RenderState.CurrentRenderTargetBinding?.Name
            ?? instance.RenderState.OutputFBO?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

    private string BuildPassName(string destination)
        => $"Fxaa_{GetSourceDisplayName()}_to_{destination}";

    private int ResolvePassIndex(string passName, out bool hasRenderGraphMetadata)
    {
        RenderPipeline? pipeline = ParentPipeline;
        if (pipeline?.PassMetadata is not { Count: > 0 })
        {
            hasRenderGraphMetadata = false;
            return int.MinValue;
        }

        hasRenderGraphMetadata = true;
        return pipeline.TryGetRenderPassIndex(passName, out int passIndex)
            ? passIndex
            : int.MinValue;
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private void Fxaa_SettingUniforms(XRRenderProgram program)
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
            || sourceTexture is null)
            return;

        Vector2 texelStep = ResolveTexelStep(instance, sourceTexture);
        program.Uniform("FxaaTexelStep", texelStep);
    }

    private Vector2 ResolveTexelStep(XRRenderPipelineInstance instance, XRTexture sourceTexture)
    {
        Vector3 sourceSize = sourceTexture.WidthHeightDepth;
        float width = sourceSize.X;
        float height = sourceSize.Y;

        if ((width <= 0.0f || height <= 0.0f) &&
            instance.RenderState.CurrentRenderRegion is { Width: > 0, Height: > 0 } region)
        {
            width = region.Width;
            height = region.Height;
        }
        else if ((width <= 0.0f || height <= 0.0f) &&
            instance.RenderState.OutputFBO is XRFrameBuffer output)
        {
            width = output.Width;
            height = output.Height;
        }
        else if ((width <= 0.0f || height <= 0.0f) &&
            (instance.RenderState.WindowViewport ?? instance.LastWindowViewport) is XRViewport viewport)
        {
            width = viewport.Width;
            height = viewport.Height;
        }

        width = Math.Max(1.0f, width);
        height = Math.Max(1.0f, height);
        return new Vector2(1.0f / width, 1.0f / height);
    }
}
