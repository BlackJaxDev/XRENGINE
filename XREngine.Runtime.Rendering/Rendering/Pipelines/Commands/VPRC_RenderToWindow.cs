using System;
using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Presents a named texture or FBO color attachment to the current renderer's window backbuffer.
/// When a viewport index is supplied, presentation is clipped to that viewport region.
/// </summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_RenderToWindow : ViewportRenderCommand
{
    /// <summary>
    /// Publishes presentation state with exact source-resource ownership so an
    /// unchanged backbuffer copy can reuse its immutable Vulkan artifact.
    /// </summary>
    private sealed class PresentBindingPublisher(
        VPRC_RenderToWindow owner) : IRenderResourceBindingPublisher
    {
        private readonly object _generationSync = new();
        private XRTexture? _lastSourceTexture;
        private AbstractRenderer? _lastRenderer;
        private ulong _lastSourceDescriptorResourceEpoch;
        private bool _lastSourceReady;
        private bool _lastFlipSourceYOnVulkan;
        private int _lastPipelineResourceGeneration = int.MinValue;
        private long _generation = 1;

        public ERenderBindingFrequency Frequency
            => ERenderBindingFrequency.Pass;

        public bool RequiresReadyDescriptorResources => true;

        public ulong Generation
        {
            get
            {
                XRTexture? sourceTexture = owner.ResolvePresentSourceTexture();
                AbstractRenderer? renderer = AbstractRenderer.Current;
                RenderTextureSamplingState samplingState =
                    owner.ResolvePresentSourceSamplingState(
                        sourceTexture,
                        renderer);
                bool flipSourceYOnVulkan = owner.FlipSourceYOnVulkan;
                int pipelineResourceGeneration =
                    RuntimeEngine.Rendering.State.CurrentRenderingPipeline
                        ?.ResourceGeneration ?? 0;

                lock (_generationSync)
                {
                    if (ReferenceEquals(sourceTexture, _lastSourceTexture) &&
                        ReferenceEquals(renderer, _lastRenderer) &&
                        samplingState.DescriptorResourceEpoch ==
                            _lastSourceDescriptorResourceEpoch &&
                        samplingState.IsReady == _lastSourceReady &&
                        flipSourceYOnVulkan == _lastFlipSourceYOnVulkan &&
                        pipelineResourceGeneration == _lastPipelineResourceGeneration)
                    {
                        return unchecked((ulong)_generation);
                    }

                    _lastSourceTexture = sourceTexture;
                    _lastRenderer = renderer;
                    _lastSourceDescriptorResourceEpoch =
                        samplingState.DescriptorResourceEpoch;
                    _lastSourceReady = samplingState.IsReady;
                    _lastFlipSourceYOnVulkan = flipSourceYOnVulkan;
                    _lastPipelineResourceGeneration = pipelineResourceGeneration;
                    if (Interlocked.Increment(ref _generation) == 0)
                        Interlocked.CompareExchange(ref _generation, 1, 0);
                    return unchecked((ulong)_generation);
                }
            }
        }

        public ulong ResourceGeneration => Generation;

        public void PublishUniforms(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
            => materialProgram.Uniform(
                "FlipSourceYOnVulkan",
                owner.FlipSourceYOnVulkan);

        public void PublishResources(
            XRRenderProgram vertexProgram,
            XRRenderProgram materialProgram)
        {
            XRTexture? sourceTexture = owner.ResolvePresentSourceTexture();
            if (sourceTexture is null)
            {
                materialProgram.SuppressFallbackSamplerWarning("SourceTexture");
                return;
            }

            materialProgram.Sampler("SourceTexture", sourceTexture, 0);
        }
    }

    private const string PresentShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;
uniform bool FlipSourceYOnVulkan; // XRENGINE_FREQUENCY(Pass)

vec2 ResolvePresentTextureUv(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5 + 0.5;
#ifdef XRENGINE_VULKAN
    if (FlipSourceYOnVulkan)
        uv.y = 1.0 - uv.y;
#endif
    return uv;
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = ResolvePresentTextureUv(clipXY);
    OutColor = texture(SourceTexture, uv);
}
""";

    private const string StereoPresentShaderCode = """
#version 460
#extension GL_OVR_multiview2 : require

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2DArray SourceTexture;
uniform bool FlipSourceYOnVulkan; // XRENGINE_FREQUENCY(Pass)

vec2 ResolvePresentTextureUv(vec2 clipXY)
{
    vec2 uv = clipXY * 0.5 + 0.5;
#ifdef XRENGINE_VULKAN
    if (FlipSourceYOnVulkan)
        uv.y = 1.0 - uv.y;
#endif
    return uv;
}

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = ResolvePresentTextureUv(clipXY);
    OutColor = texture(SourceTexture, vec3(uv, gl_ViewID_OVR));
}
""";

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;
    private XRMaterial? _stereoMaterial;
    private XRQuadFrameBuffer? _stereoQuad;
    private XRTexture? _resolvedSourceTexture;
    private AbstractRenderer? _resolvedSourceRenderer;
    private RenderTextureSamplingState _resolvedSourceSamplingState;
    private string? _cachedPassSourceTextureName;
    private string? _cachedPassSourceFboName;
    private string? _cachedRenderGraphPassName;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? WindowTitle { get; set; }
    public int? ViewportIndex { get; set; }
    public bool UseTargetViewportRegion { get; set; } = true;
    public bool ClearColor { get; set; }
    public bool ClearDepth { get; set; }
    public bool ClearStencil { get; set; }
    public bool FlipSourceYOnVulkan { get; set; }

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        _material ??= CreatePresentMaterial(PresentShaderCode);

        _quad = new XRQuadFrameBuffer(_material);
        _quad.FullScreenMesh.BindingPublishers.Add(
            new PresentBindingPublisher(this));
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

        if (_stereoQuad is not null)
        {
            _stereoQuad.Destroy();
            _stereoQuad = null;
        }

        _stereoMaterial?.Destroy();
        _stereoMaterial = null;
    }

    protected override void Execute()
    {
        if (_quad is null)
        {
            XRRenderPipelineInstance activeInstance = ActivePipelineInstance;
            Debug.RenderingWarningEvery(
                $"RenderToWindow.NoQuad.{activeInstance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped: present quad not allocated. Pipeline={0} Generation={1}",
                activeInstance.Pipeline?.DebugName ?? activeInstance.Pipeline?.GetType().Name ?? "<null>",
                activeInstance.ResourceGeneration);
            return;
        }

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out XRTexture? sourceTexture, out string resolveFailure)
            || sourceTexture is null)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.SourceMissing.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped: {0}. SourceTex='{1}' SourceFBO='{2}' Pipeline={3} Generation={4}",
                resolveFailure,
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                instance.Pipeline?.DebugName ?? "<null>",
                instance.ResourceGeneration);
            return;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.NoRenderer.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped: no active renderer. Pipeline={0} Generation={1}",
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>",
                instance.ResourceGeneration);
            return;
        }

        XRWindow? targetWindow = ResolveTargetWindow(renderer);
        if (targetWindow is null || !ReferenceEquals(targetWindow, renderer.XRWindow))
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.WindowMismatch.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped: target window mismatch. ResolvedTarget={0} ActiveRendererWindow={1} RequestedTitle='{2}' Pipeline={3}",
                targetWindow?.GetHashCode().ToString() ?? "<null>",
                renderer.XRWindow?.GetHashCode().ToString() ?? "<null>",
                WindowTitle ?? "<null>",
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
            return;
        }

        RenderTextureSamplingState sourceSamplingState =
            renderer.GetTextureShaderSamplingState(sourceTexture);
        if (!sourceSamplingState.IsReady)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.SourceNotReady.{instance.GetHashCode()}.{sourceTexture.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow deferred: required source texture is not ready for shader sampling. Source='{0}' SourceTex='{1}' SourceFBO='{2}' DescriptorEpoch={3} Pipeline={4} Generation={5}",
                sourceTexture.Name ?? sourceTexture.GetDescribingName(),
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                sourceSamplingState.DescriptorResourceEpoch,
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>",
                instance.ResourceGeneration);
            return;
        }

        XRViewport? windowViewport = instance.RenderState.WindowViewport;
        bool isActiveWindowViewport = windowViewport?.Window?.Viewports.Contains(windowViewport) == true;
        bool isExternalSwapchainTarget = renderer.IsRenderingExternalSwapchainTarget;
        bool useBoundOutputFbo = instance.RenderState.OutputFBO is not null;
        if (windowViewport is not null && !isActiveWindowViewport && !isExternalSwapchainTarget && !useBoundOutputFbo)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.SkipOffscreenPresent.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped backbuffer present for non-window pipeline instance. SourceTex='{0}' SourceFBO='{1}' Pipeline={2} WindowViewport={3} HasWindow={4}",
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>",
                windowViewport.GetHashCode().ToString(),
                windowViewport.Window is not null);
            return;
        }

        BoundingRectangle region = isExternalSwapchainTarget &&
                                   renderer.TryGetExternalSwapchainTargetRegion(out BoundingRectangle externalRegion)
            ? externalRegion
            : useBoundOutputFbo
                ? ResolveOutputFboRegion(instance)
                : ResolveTargetRegion(instance, targetWindow);
        if (!isExternalSwapchainTarget && !useBoundOutputFbo)
            region = renderer.MapWindowPresentationRegionToBackbuffer(region);

        if (region.Width <= 0 || region.Height <= 0)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.InvalidRegion.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow skipped: invalid region {0}x{1}. ViewportIndex={2} UseTargetViewportRegion={3} Pipeline={4}",
                region.Width,
                region.Height,
                ViewportIndex?.ToString() ?? "<null>",
                UseTargetViewportRegion,
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
            return;
        }

        bool useStereoPresent = instance.RenderState.StereoPass && IsStereoArrayTexture(sourceTexture);
        XRQuadFrameBuffer quad = useStereoPresent ? GetOrCreateStereoQuad() : _quad;
        if (instance.RenderState.StereoPass && !useStereoPresent)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.StereoMonoSource.{instance.GetHashCode()}.{sourceTexture.Name}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow stereo pass is presenting mono source texture '{0}' type={1}. SourceTex='{2}' SourceFBO='{3}' Pipeline={4}",
                sourceTexture.Name ?? "<unnamed>",
                sourceTexture.GetType().Name,
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
        }

        if (useStereoPresent)
        {
            Debug.RenderingEvery(
                $"RenderToWindow.StereoPresent.{instance.GetHashCode()}.{sourceTexture.Name}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] RenderToWindow stereo array present. Source='{0}' type={1} Region={2}x{3} Pipeline={4}",
                sourceTexture.Name ?? "<unnamed>",
                sourceTexture.GetType().Name,
                region.Width,
                region.Height,
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
        }

        string passName = BuildRenderGraphPassName();
        int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
        if (passIndex == int.MinValue && hasRenderGraphMetadata)
        {
            Debug.RenderingWarningEvery(
                $"RenderToWindow.MissingRenderGraphPass.{passName}",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] RenderToWindow skipped: no matching render-graph pass metadata was generated. Pass='{0}' SourceTex='{1}' SourceFBO='{2}' Pipeline={3}",
                passName,
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
            return;
        }

        using var passScope = passIndex != int.MinValue
            ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
            : default;

        if (!useBoundOutputFbo)
            RuntimeEngine.Rendering.State.UnbindFrameBuffers(EFramebufferTarget.Framebuffer);
        if (!isExternalSwapchainTarget && !useBoundOutputFbo)
            renderer.TrackWindowPresentSource(sourceTexture, ResolveSourceFrameBuffer(instance, sourceTexture));

        using var areaScope = instance.RenderState.PushRenderArea(region);
        if (ClearColor || ClearDepth || ClearStencil)
            RuntimeEngine.Rendering.State.Clear(ClearColor, ClearDepth, ClearStencil);

        try
        {
            _resolvedSourceTexture = sourceTexture;
            _resolvedSourceRenderer = renderer;
            _resolvedSourceSamplingState = sourceSamplingState;
            quad.Render(null);
        }
        finally
        {
            _resolvedSourceTexture = null;
            _resolvedSourceRenderer = null;
            _resolvedSourceSamplingState = default;
        }
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

        context.GetOrCreateSyntheticPass(BuildRenderGraphPassName())
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(RenderGraphResourceNames.OutputRenderTarget, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
    }

    private XRWindow? ResolveTargetWindow(AbstractRenderer renderer)
    {
        if (string.IsNullOrWhiteSpace(WindowTitle))
            return renderer.XRWindow;

        foreach (XRWindow window in RuntimeEngine.Windows)
        {
            string? title = window.Window?.Title;
            if (string.Equals(title, WindowTitle, StringComparison.OrdinalIgnoreCase))
                return window;
        }

        return null;
    }

    private BoundingRectangle ResolveTargetRegion(XRRenderPipelineInstance instance, XRWindow targetWindow)
    {
        if (!UseTargetViewportRegion)
        {
            BoundingRectangle current = instance.RenderState.CurrentRenderRegion;
            if (current.Width > 0 && current.Height > 0)
                return current;
        }

        XRViewport? viewport = null;
        if (ViewportIndex.HasValue)
        {
            int requestedIndex = ViewportIndex.Value;
            for (int i = 0; i < targetWindow.Viewports.Count; ++i)
            {
                XRViewport candidate = targetWindow.Viewports[i];
                if (candidate.Index != requestedIndex)
                    continue;

                viewport = candidate;
                break;
            }

            viewport ??= ViewportIndex.Value >= 0 && ViewportIndex.Value < targetWindow.Viewports.Count
                ? targetWindow.Viewports[ViewportIndex.Value]
                : null;
        }

        viewport ??= instance.RenderState.WindowViewport;
        viewport ??= targetWindow.Viewports.Count > 0 ? targetWindow.Viewports[0] : null;
        if (viewport is not null)
            return viewport.Region;

        var framebufferSize = targetWindow.EffectiveFramebufferSize;
        return new BoundingRectangle(0, 0, framebufferSize.X, framebufferSize.Y);
    }

    private static BoundingRectangle ResolveOutputFboRegion(XRRenderPipelineInstance instance)
    {
        XRFrameBuffer? outputFbo = instance.RenderState.OutputFBO;
        if (VPRC_RenderQuadToFBO.TryResolveDestinationRenderArea(outputFbo, out int width, out int height))
            return new BoundingRectangle(0, 0, width, height);

        BoundingRectangle current = instance.RenderState.CurrentRenderRegion;
        if (current.Width > 0 && current.Height > 0)
            return current;

        return default;
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private string BuildRenderGraphPassName()
    {
        if (_cachedRenderGraphPassName is not null &&
            string.Equals(_cachedPassSourceTextureName, SourceTextureName, StringComparison.Ordinal) &&
            string.Equals(_cachedPassSourceFboName, SourceFBOName, StringComparison.Ordinal))
        {
            return _cachedRenderGraphPassName;
        }

        _cachedPassSourceTextureName = SourceTextureName;
        _cachedPassSourceFboName = SourceFBOName;
        _cachedRenderGraphPassName = string.Concat("RenderToWindow_", GetSourceDisplayName());
        return _cachedRenderGraphPassName;
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

    private XRFrameBuffer? ResolveSourceFrameBuffer(XRRenderPipelineInstance instance, XRTexture sourceTexture)
    {
        if (!string.IsNullOrWhiteSpace(SourceFBOName))
            return instance.GetFBO<XRFrameBuffer>(SourceFBOName!);

        if (FrameBufferContainsColorTexture(instance.RenderState.OutputFBO, sourceTexture))
            return instance.RenderState.OutputFBO;

        XRFrameBuffer[] frameBuffers = instance.Resources.GetFrameBufferInstanceSnapshot();
        for (int i = 0; i < frameBuffers.Length; ++i)
        {
            XRFrameBuffer candidate = frameBuffers[i];
            if (FrameBufferContainsColorTexture(candidate, sourceTexture))
                return candidate;
        }

        return null;
    }

    private static bool FrameBufferContainsColorTexture(XRFrameBuffer? frameBuffer, XRTexture sourceTexture)
    {
        if (frameBuffer?.Targets is null)
            return false;

        var targets = frameBuffer.Targets;
        for (int i = 0; i < targets.Length; ++i)
        {
            var (target, attachment, _, _) = targets[i];
            if (attachment is < EFrameBufferAttachment.ColorAttachment0 or > EFrameBufferAttachment.ColorAttachment31)
                continue;

            if (ReferenceEquals(target, sourceTexture))
                return true;
        }

        return false;
    }

    private static XRMaterial CreatePresentMaterial(string fragmentShaderCode)
        => new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, fragmentShaderCode))
        {
            RenderOptions = new RenderingParameters()
            {
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Disabled,
                    Function = EComparison.Always,
                    UpdateDepth = false,
                }
            }
        };

    private XRQuadFrameBuffer GetOrCreateStereoQuad()
    {
        if (_stereoQuad is not null)
            return _stereoQuad;

        _stereoMaterial ??= CreatePresentMaterial(StereoPresentShaderCode);
        _stereoQuad = new XRQuadFrameBuffer(_stereoMaterial);
        _stereoQuad.FullScreenMesh.BindingPublishers.Add(
            new PresentBindingPublisher(this));
        return _stereoQuad;
    }

    private static bool IsStereoArrayTexture(XRTexture texture)
        => texture is XRTexture2DArray or XRTexture2DArrayView;

    private XRTexture? ResolvePresentSourceTexture()
    {
        XRTexture? sourceTexture = _resolvedSourceTexture;
        if (sourceTexture is not null)
            return sourceTexture;

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        return VPRCSourceTextureHelpers.TryResolveColorTexture(
            instance,
            SourceTextureName,
            SourceFBOName,
            out sourceTexture,
            out _)
            ? sourceTexture
            : null;
    }

    private RenderTextureSamplingState ResolvePresentSourceSamplingState(
        XRTexture? sourceTexture,
        AbstractRenderer? renderer)
    {
        if (sourceTexture is null || renderer is null)
            return default;

        if (ReferenceEquals(sourceTexture, _resolvedSourceTexture) &&
            ReferenceEquals(renderer, _resolvedSourceRenderer))
        {
            return _resolvedSourceSamplingState;
        }

        return renderer.GetTextureShaderSamplingState(sourceTexture);
    }
}
