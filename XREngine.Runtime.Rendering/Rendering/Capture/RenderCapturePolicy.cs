namespace XREngine.Rendering;

/// <summary>
/// Explicit policy for an offscreen scene capture. Minimal policies use the
/// caller-owned FBO directly and never enter the viewport post-process chain.
/// </summary>
public readonly record struct RenderCapturePolicy
{
    public ERenderCaptureKind Kind { get; init; }
    public bool UseDirectFboTargetCommands { get; init; }
    public bool OutputHDR { get; init; }
    public bool RenderShadows { get; init; }
    public bool EnablePreRenderHooks { get; init; }
    public bool RenderBackground { get; init; }
    public bool RenderOpaqueDeferred { get; init; }
    public bool RenderOpaqueForward { get; init; }
    public bool RenderMasked { get; init; }
    public bool RenderTransparent { get; init; }
    public bool RenderOnTop { get; init; }
    public bool EnableComputeLighting { get; init; }
    public bool RenderDebugOverlays { get; init; }
    public bool EnablePostRenderHooks { get; init; }
    public bool RenderScreenSpaceUI { get; init; }
    public bool AllowTemporalHistory { get; init; }
    public bool AllowAutoExposure { get; init; }
    public bool AllowBloom { get; init; }
    public bool AllowTemporalAntiAliasing { get; init; }
    public bool AllowVendorUpscale { get; init; }
    public bool AllowViewportFinalOutput { get; init; }
    public ERenderCaptureTextureOrientation TextureOrientation { get; init; }

    public bool IsCapture => Kind != ERenderCaptureKind.None;

    /// <summary>
    /// True when the pipeline can skip its managed viewport resources entirely.
    /// </summary>
    public bool UsesMinimalDirectFboPath
        => IsCapture &&
           UseDirectFboTargetCommands &&
           !AllowTemporalHistory &&
           !AllowAutoExposure &&
           !AllowBloom &&
           !AllowTemporalAntiAliasing &&
           !AllowVendorUpscale &&
           !AllowViewportFinalOutput;

    public static RenderCapturePolicy None => default;

    public static RenderCapturePolicy GenericSceneCapture { get; } = CreateMinimal(
        ERenderCaptureKind.SceneCapture,
        outputHDR: false,
        renderShadows: true,
        renderTransparent: true);

    public static RenderCapturePolicy LightProbe { get; } = CreateMinimal(
        ERenderCaptureKind.LightProbe,
        outputHDR: true,
        renderShadows: true,
        renderTransparent: true);

    public static RenderCapturePolicy ReflectionProbe { get; } = CreateMinimal(
        ERenderCaptureKind.ReflectionProbe,
        outputHDR: true,
        renderShadows: true,
        renderTransparent: true);

    public static RenderCapturePolicy GiProbe { get; } = CreateMinimal(
        ERenderCaptureKind.GiProbe,
        outputHDR: true,
        renderShadows: true,
        renderTransparent: false);

    public static RenderCapturePolicy ThumbnailOrUiPreview { get; } = CreateMinimal(
        ERenderCaptureKind.ThumbnailOrUiPreview,
        outputHDR: false,
        renderShadows: true,
        renderTransparent: true) with
    {
        RenderOnTop = true,
        RenderScreenSpaceUI = true,
    };

    public static RenderCapturePolicy DiagnosticFbo { get; } = CreateMinimal(
        ERenderCaptureKind.DiagnosticFbo,
        outputHDR: false,
        renderShadows: false,
        renderTransparent: true) with
    {
        RenderOnTop = true,
        RenderDebugOverlays = true,
    };

    public bool Allows(ERenderCapturePass pass)
        => pass switch
        {
            ERenderCapturePass.PreRender => EnablePreRenderHooks,
            ERenderCapturePass.Background => RenderBackground,
            ERenderCapturePass.OpaqueDeferred => RenderOpaqueDeferred,
            ERenderCapturePass.OpaqueForward => RenderOpaqueForward,
            ERenderCapturePass.Masked => RenderMasked,
            ERenderCapturePass.Transparent => RenderTransparent,
            ERenderCapturePass.OnTop => RenderOnTop,
            ERenderCapturePass.ComputeLighting => EnableComputeLighting,
            ERenderCapturePass.DebugOverlays => RenderDebugOverlays,
            ERenderCapturePass.PostRender => EnablePostRenderHooks,
            ERenderCapturePass.ScreenSpaceUi => RenderScreenSpaceUI,
            _ => false,
        };

    public void ApplyCameraOverrides(XRCamera camera)
    {
        if (!IsCapture)
            return;

        camera.OutputHDROverride = OutputHDR;
        camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
        camera.MsaaSampleCountOverride = 1u;
        camera.TsrRenderScaleOverride = 1.0f;
    }

    public string DescribeEffective(RuntimeGraphicsApiKind backend)
        => $"kind={Kind} directFbo={UseDirectFboTargetCommands} hdr={OutputHDR} " +
           $"passes=[pre:{EnablePreRenderHooks},sky:{RenderBackground},deferred:{RenderOpaqueDeferred},forward:{RenderOpaqueForward},masked:{RenderMasked},transparent:{RenderTransparent},shadows:{RenderShadows}] " +
           $"post=[temporal:{AllowTemporalHistory},exposure:{AllowAutoExposure},bloom:{AllowBloom},taa:{AllowTemporalAntiAliasing},vendorUpscale:{AllowVendorUpscale},finalOutput:{AllowViewportFinalOutput}] " +
           $"orientation={TextureOrientation} backend={backend} clipY={RuntimeEngine.Rendering.Settings.ClipSpaceYDirection} depth={RuntimeEngine.Rendering.EffectiveClipDepthRange} textureY={RenderClipSpacePolicy.FramebufferTextureYDirection(backend)}";

    private static RenderCapturePolicy CreateMinimal(
        ERenderCaptureKind kind,
        bool outputHDR,
        bool renderShadows,
        bool renderTransparent)
        => new()
        {
            Kind = kind,
            UseDirectFboTargetCommands = true,
            OutputHDR = outputHDR,
            RenderShadows = renderShadows,
            EnablePreRenderHooks = true,
            RenderBackground = true,
            RenderOpaqueDeferred = true,
            RenderOpaqueForward = true,
            RenderMasked = true,
            RenderTransparent = renderTransparent,
            EnablePostRenderHooks = true,
            TextureOrientation = ERenderCaptureTextureOrientation.BackendFramebufferNative,
        };
}
