using System;
using System.Linq;
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
    private const string PresentShaderCode = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D SourceTexture;

void main()
{
    vec2 clipXY = FragPos.xy;
    if (clipXY.x < -1.0 || clipXY.x > 1.0 || clipXY.y < -1.0 || clipXY.y > 1.0)
        discard;

    vec2 uv = clipXY * 0.5 + 0.5;
    OutColor = texture(SourceTexture, uv);
}
""";

    private XRMaterial? _material;
    private XRQuadFrameBuffer? _quad;
    private XRTexture? _resolvedSourceTexture;

    public string? SourceTextureName { get; set; }
    public string? SourceFBOName { get; set; }
    public string? WindowTitle { get; set; }
    public int? ViewportIndex { get; set; }
    public bool UseTargetViewportRegion { get; set; } = true;
    public bool ClearColor { get; set; }
    public bool ClearDepth { get; set; }
    public bool ClearStencil { get; set; }

    internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
            return;

        _material = new(Array.Empty<XRTexture?>(), new XRShader(EShaderType.Fragment, PresentShaderCode))
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

        _quad = new XRQuadFrameBuffer(_material);
        _quad.SettingUniforms += Present_SettingUniforms;
    }

    internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
    {
        if (_quad is not null)
        {
            _quad.SettingUniforms -= Present_SettingUniforms;
            _quad.Destroy();
            _quad = null;
        }

        _material?.Destroy();
        _material = null;
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

        XRViewport? windowViewport = instance.RenderState.WindowViewport;
        bool isActiveWindowViewport = windowViewport?.Window?.Viewports.Contains(windowViewport) == true;
        if (windowViewport is not null && !isActiveWindowViewport)
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

        BoundingRectangle region = ResolveTargetRegion(instance, targetWindow);
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

        Engine.Rendering.State.UnbindFrameBuffers(EFramebufferTarget.Framebuffer);
        using var areaScope = instance.RenderState.PushRenderArea(region);
        if (ClearColor || ClearDepth || ClearStencil)
            Engine.Rendering.State.Clear(ClearColor, ClearDepth, ClearStencil);

        try
        {
            _resolvedSourceTexture = sourceTexture;
            Debug.RenderingEvery(
                $"RenderToWindow.Present.{instance.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[RenderDiag] RenderToWindow presenting. SourceTex='{0}' SourceFBO='{1}' Region={2}x{3} Pipeline={4}",
                SourceTextureName ?? "<null>",
                SourceFBOName ?? "<null>",
                region.Width,
                region.Height,
                instance.Pipeline?.DebugName ?? instance.Pipeline?.GetType().Name ?? "<null>");
            _quad.Render(null);
        }
        finally
        {
            _resolvedSourceTexture = null;
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

        context.GetOrCreateSyntheticPass($"RenderToWindow_{GetSourceDisplayName()}")
            .WithStage(ERenderGraphPassStage.Graphics)
            .SampleTexture(source)
            .UseColorAttachment(RenderGraphResourceNames.OutputRenderTarget, ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
    }

    private XRWindow? ResolveTargetWindow(AbstractRenderer renderer)
    {
        if (string.IsNullOrWhiteSpace(WindowTitle))
            return renderer.XRWindow;

        foreach (XRWindow window in Engine.Windows)
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
            viewport = targetWindow.Viewports.FirstOrDefault(x => x.Index == ViewportIndex.Value);
            viewport ??= ViewportIndex.Value >= 0 && ViewportIndex.Value < targetWindow.Viewports.Count
                ? targetWindow.Viewports[ViewportIndex.Value]
                : null;
        }

        viewport ??= instance.RenderState.WindowViewport;
        viewport ??= targetWindow.Viewports.FirstOrDefault();
        if (viewport is not null)
            return viewport.Region;

        return new BoundingRectangle(0, 0, targetWindow.Window.FramebufferSize.X, targetWindow.Window.FramebufferSize.Y);
    }

    private string GetSourceDisplayName()
        => SourceTextureName ?? SourceFBOName ?? "Output";

    private void Present_SettingUniforms(XRRenderProgram program)
    {
        XRTexture? sourceTexture = _resolvedSourceTexture;
        if (sourceTexture is null)
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            if (!VPRCSourceTextureHelpers.TryResolveColorTexture(instance, SourceTextureName, SourceFBOName, out sourceTexture, out _)
                || sourceTexture is null)
            {
                program.SuppressFallbackSamplerWarning("SourceTexture");
                return;
            }
        }

        program.Sampler("SourceTexture", sourceTexture, 0);
    }
}
