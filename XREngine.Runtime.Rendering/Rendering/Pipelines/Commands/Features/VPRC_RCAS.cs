using System;
using System.IO;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Applies post-resolve Robust Contrast Adaptive Sharpening (RCAS) as a standalone
/// fullscreen pass outside the temporal history feedback loop.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_RCAS : ViewportRenderCommand
{
    public override void DescribeRequirements(RenderPipelineRequirements requirements)
    {
        requirements.RequireOperation("fullscreen-quad");
    }

    private sealed class RcasBindingPublisher(VPRC_RCAS owner) : IRenderBindingPublisher
    {
        private readonly object _generationSync = new();
        private XRTexture? _lastSource;
        private float _lastSharpness = float.NaN;
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
                float sharpness = RenderPipeline.ResolveEffectiveAntiAliasingSharpnessForFrame();

                lock (_generationSync)
                {
                    if (ReferenceEquals(source, _lastSource) &&
                        BitConverter.SingleToInt32Bits(sharpness) == BitConverter.SingleToInt32Bits(_lastSharpness))
                    {
                        return unchecked((ulong)_generation);
                    }

                    _lastSource = source;
                    _lastSharpness = sharpness;
                    if (Interlocked.Increment(ref _generation) == 0)
                        Interlocked.CompareExchange(ref _generation, 1, 0);
                    return unchecked((ulong)_generation);
                }
            }
        }

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => owner.Rcas_SettingUniforms(materialProgram);
    }

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;
    private string? _cachedPassSource;
    private string? _cachedPassDestination;
    private string? _cachedPassName;
    private string? _cachedProfilingSource;
    private string? _cachedProfilingName;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? DestinationFBOName { get; set; }
    public string? WaitForPassName { get; set; }
    public bool Stereo { get; set; }

    public static string BuildPassName(string source, string destination)
        => $"Rcas_{source}_to_{destination}";

    public override string GpuProfilingName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourceTextureName) && string.IsNullOrWhiteSpace(SourceFBOName))
                return nameof(VPRC_RCAS);
            string? source = SourceTextureName ?? SourceFBOName;
            if (_cachedProfilingName is not null && _cachedProfilingSource == source)
                return _cachedProfilingName;
            _cachedProfilingSource = source;
            return _cachedProfilingName = $"{nameof(VPRC_RCAS)}:{source}";
        }
    }

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (!WebPipelineRasterProgram.IsActive)
            EnsureResources();
    }

    private void EnsureResources()
    {
        if (_quad is not null)
            return;

        string shaderName = Stereo ? "RCASStereo.fs" : "RCAS.fs";
        XRShader[] shaders = [XRShader.EngineShader(Path.Combine(SceneShaderPath, shaderName), EShaderType.Fragment)];
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
            _quad = new XRQuadFrameBuffer(_material, useMultiview: Stereo);
            _quad.FullScreenMesh.BindingPublishers.Add(new RcasBindingPublisher(this));
        }
        catch
        {
            _quad?.Destroy(true);
            _quad = null;
            _material.Destroy(true);
            _material = null;
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

        _material?.Destroy();
        _material = null;
    }

    protected override void Execute()
    {
        if (WebPipelineRasterProgram.IsActive)
            return;

        EnsureResources();
        if (_quad is null)
            return;

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out _)
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
        string passName = ResolveCachedPassName(destinationName);
        int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
        if (passIndex == int.MinValue && hasRenderGraphMetadata)
        {
            Debug.RenderingWarningEvery(
                $"Rcas.MissingRenderGraphPass.{passName}",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] Skipping RCAS pass '{0}': no matching render-graph pass metadata was generated.",
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
            VPRCFullscreenPassContract.ValidateAndLog(instance, nameof(VPRC_RCAS), destination, sourceTexture, Stereo);

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

        string passName = ResolveCachedPassName(destination);
        if (WebPipelineRasterProgram.IsActive)
        {
            context.ReserveSyntheticPassIndex(passName);
            return;
        }

        RenderPassBuilder pass = context.GetOrCreateSyntheticPass(passName)
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
        if (!string.IsNullOrWhiteSpace(WaitForPassName))
            pass.DependsOn(context.GetOrCreateSyntheticPass(WaitForPassName!).PassIndex);
    }

    private string ResolveDestinationLabel(XRRenderPipelineInstance instance)
        => DestinationFBOName
            ?? instance.RenderState.CurrentRenderTargetBinding?.Name
            ?? instance.RenderState.OutputFBO?.Name
            ?? RenderGraphResourceNames.OutputRenderTarget;

    private string ResolveCachedPassName(string destination)
    {
        string source = GetSourceDisplayName();
        if (_cachedPassName is not null &&
            string.Equals(_cachedPassSource, source, StringComparison.Ordinal) &&
            string.Equals(_cachedPassDestination, destination, StringComparison.Ordinal))
            return _cachedPassName;

        _cachedPassSource = source;
        _cachedPassDestination = destination;
        return _cachedPassName = BuildPassName(source, destination);
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

    private void Rcas_SettingUniforms(XRRenderProgram program)
        => program.Uniform("RcasSharpness", RenderPipeline.ResolveEffectiveAntiAliasingSharpnessForFrame());
}
