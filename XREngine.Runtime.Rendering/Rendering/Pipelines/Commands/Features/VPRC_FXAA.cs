using System;
using System.Numerics;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Applies the engine FXAA shader as a standalone composable fullscreen pass.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_FXAA : ViewportRenderCommand
{
    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        requirements.RequireOperation("fullscreen-quad");
        if (requirements.Backend != RendererBackendId.WebGPU)
            return;
        requirements.RequireRasterProgram("advanced::fxaa");
        requirements.SupportedAntiAliasingModes.Add(EAntiAliasingMode.Fxaa);
        if (Stereo || requirements.OutputProfile.Stereo || requirements.OutputProfile.ViewCount != 1 ||
            requirements.OutputProfile.MsaaSampleCount != 1)
            requirements.Diagnostics.Add("The cooked FXAA operation requires a mono, single-sample output.");
        if (string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName))
            requirements.Diagnostics.Add("The cooked FXAA operation requires an explicit source texture or framebuffer.");
    }

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
    private bool _webResources;
    private string? _cachedPassSource;
    private string? _cachedPassDestination;
    private string? _cachedPassName;
    private string? _cachedProfilingSource;
    private string? _cachedProfilingName;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationFBOName { get; set; }
    public bool Stereo { get; set; }

    public override string GpuProfilingName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName))
                return nameof(VPRC_FXAA);
            string? source = SourceTextureName ?? SourceFBOName;
            if (_cachedProfilingName is not null && _cachedProfilingSource == source)
                return _cachedProfilingName;
            _cachedProfilingSource = source;
            return _cachedProfilingName = $"{nameof(VPRC_FXAA)}:{source}";
        }
    }

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        // Browser branches share a command container; only the selected branch
        // may require and instantiate its cooked program.
        if (!WebPipelineRasterProgram.IsActive)
            EnsureResources(instance);
    }

    private void EnsureResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        string shaderName = Stereo ? "FXAAStereo.fs" : "FXAA.fs";
        _webResources = WebPipelineRasterProgram.IsActive;
        if (_webResources && Stereo)
            throw new NotSupportedException("WebGPU.FXAA.StereoUnsupported: the cooked FXAA pass requires a mono source and destination.");
        XRShader[] shaders = _webResources
            ? WebPipelineRasterProgram.CreateShaders(
                instance.Pipeline ?? throw new InvalidOperationException("WebGPU.FXAA.PipelineMissing: the selected FXAA pass requires an owning pipeline."),
                "advanced::fxaa")
            : [XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment)];
        _material = new(Array.Empty<XRTexture?>(), shaders)
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

        try
        {
            _quad = new XRQuadFrameBuffer(_material, useMultiview: Stereo, deriveRenderTargetsFromMaterial: !_webResources, prepareForInitialRendering: !_webResources);
            _quad.FullScreenMesh.BindingPublishers.Add(new FxaaBindingPublisher(this));
            if (_webResources)
                WebPipelineRasterProgram.OwnMaterial(_quad);
        }
        catch
        {
            _quad?.Destroy(true);
            _quad = null;
            _material.Destroy(true);
            _material = null;
            if (_webResources)
                foreach (XRShader shader in shaders)
                    shader.Destroy(true);
            throw;
        }
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.Destroy();
            _quad = null;
        }

        if (!_webResources)
            _material?.Destroy();
        _material = null;
        _webResources = false;
    }

    protected override void Execute()
    {
        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (WebPipelineRasterProgram.IsActive)
            EnsureResources(instance);
        if (_quad is null)
            return;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out string failure)
            || sourceTexture is null)
        {
            if (_webResources)
                throw new InvalidOperationException($"WebGPU.FXAA.SourceMissing: {failure}");
            return;
        }

        XRFrameBuffer? destination = null;
        if (!string.IsNullOrWhiteSpace(DestinationFBOName))
        {
            destination = instance.GetFBO<XRFrameBuffer>(DestinationFBOName!);
            if (destination is null)
            {
                if (_webResources)
                    throw new InvalidOperationException($"WebGPU.FXAA.TargetMissing: destination '{DestinationFBOName}' was not declared.");
                return;
            }
        }
        else if (_webResources)
            destination = instance.RenderState.CurrentRenderTargetBinding?.FrameBuffer ?? instance.RenderState.OutputFBO;

        if (_material is not null &&
            (_material.Textures.Count != 1 || !ReferenceEquals(_material.Textures[0], sourceTexture)))
        {
            _material.Textures.Clear();
            _material.Textures.Add(sourceTexture);
        }

        if (_webResources && sourceTexture is not (XRTexture2D or XRTexture2DView))
            throw new NotSupportedException("WebGPU.FXAA.SourceUnsupported: the cooked FXAA pass requires a two-dimensional color texture.");

        string destinationName = ResolveDestinationLabel(instance);
        string passName = BuildPassName(destinationName);
        int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
        if (passIndex == int.MinValue && hasRenderGraphMetadata)
        {
            if (_webResources)
                throw new InvalidOperationException($"WebGPU.FXAA.PassMissing: selected pass '{passName}' is absent from the render graph.");
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

        string passName = BuildPassName(destination);
        string? sourceName = !string.IsNullOrWhiteSpace(SourceTextureName) ? SourceTextureName : SourceFBOName;
        if (WebPipelineRasterProgram.IsActive && context.ResourceLayout is not null &&
            (sourceName is not null && !context.HasResource(sourceName) ||
                destination != RenderGraphResourceNames.OutputRenderTarget && !context.HasResource(destination)))
        {
            context.ReserveSyntheticPassIndex(passName);
            return;
        }

        context.GetOrCreateSyntheticPass(passName)
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
    {
        string source = GetSourceDisplayName();
        if (_cachedPassName is not null &&
            string.Equals(_cachedPassSource, source, StringComparison.Ordinal) &&
            string.Equals(_cachedPassDestination, destination, StringComparison.Ordinal))
            return _cachedPassName;

        _cachedPassSource = source;
        _cachedPassDestination = destination;
        return _cachedPassName = $"Fxaa_{source}_to_{destination}";
    }

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
        if (_webResources)
        {
            WebPipelineRasterProgram.PublishRenderArea(program);
            program.Sampler("Texture0", sourceTexture, 0);
        }
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
