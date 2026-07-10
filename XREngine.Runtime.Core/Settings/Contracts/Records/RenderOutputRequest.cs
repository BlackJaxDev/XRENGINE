namespace XREngine;

public readonly record struct RenderOutputRequest(
    ulong OutputId,
    ulong ViewFamilyId,
    EFrameOutputKind OutputKind,
    EVrOutputViewKind ViewKind,
    ERenderOutputClass OutputClass,
    RenderOutputTargetDescriptor Target,
    RenderOutputSchedulePolicy Schedule,
    ERenderOutputQualityRequirement QualityRequirements,
    ERenderOutputFallbackPolicy FallbackPolicy,
    ERenderOutputCompletionRequirement CompletionRequirement,
    ulong ProducerDependencySetId,
    ulong ConsumerDependencySetId,
    ulong FrameId)
{
    public bool IsDefined => OutputId != 0UL;

    public RenderOutputRequest WithTarget(in RenderOutputTargetDescriptor target)
        => this with { Target = target };

    public bool Allows(ERenderOutputWorkDisposition disposition)
        => disposition switch
        {
            ERenderOutputWorkDisposition.FreshRender or ERenderOutputWorkDisposition.ReusedCurrent => true,
            ERenderOutputWorkDisposition.ReusedStale => (FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0,
            ERenderOutputWorkDisposition.Deferred =>
                (FallbackPolicy & (ERenderOutputFallbackPolicy.AllowCadenceReduction | ERenderOutputFallbackPolicy.AllowBudgetDeferral)) != 0,
            ERenderOutputWorkDisposition.Skipped => FallbackPolicy != ERenderOutputFallbackPolicy.None,
            ERenderOutputWorkDisposition.QualityReduced =>
                (FallbackPolicy & ERenderOutputFallbackPolicy.AllowResolutionReduction) != 0,
            _ => false,
        };

    public static RenderOutputRequest CreateDefault(
        EVrOutputViewKind viewKind,
        EFrameOutputKind outputKind,
        ulong frameId = 0UL,
        float desiredRateHz = 0.0f,
        float sourceRateHz = 0.0f)
    {
        ERenderOutputClass outputClass = ResolveClass(outputKind);
        ERenderOutputPriority priority = ResolvePriority(outputKind, outputClass);
        ERenderOutputFallbackPolicy fallback = ResolveFallback(outputKind, outputClass);
        bool hardDeadline = outputClass == ERenderOutputClass.XrCritical;
        double deadlineMs = hardDeadline && sourceRateHz > 0.0f ? 1000.0 / sourceRateHz : 0.0;
        uint maxContentAgeFrames = outputClass switch
        {
            ERenderOutputClass.XrCritical or ERenderOutputClass.RequiredDependency or ERenderOutputClass.Presentation => 0u,
            ERenderOutputClass.InteractiveScene or ERenderOutputClass.Overlay => 1u,
            ERenderOutputClass.VisibleMirror => 2u,
            _ => uint.MaxValue,
        };

        ulong outputId = PackIdentity(1u, outputKind, viewKind);
        ulong viewFamilyId = ResolveViewFamilyId(outputKind, viewKind);
        RenderOutputTargetDescriptor target = new(
            ResolveTargetClass(outputKind),
            StableTargetId: outputId,
            TargetGeneration: 0UL,
            DisplayWidth: 0u,
            DisplayHeight: 0u,
            InternalWidth: 0u,
            InternalHeight: 0u,
            FormatCompatibilityKey: 0UL,
            SampleCount: 1u,
            ViewMask: ResolveDefaultViewMask(viewKind),
            ExternalImageSlot: -1);
        return new(
            outputId,
            viewFamilyId,
            outputKind,
            viewKind,
            outputClass,
            target,
            new(
                priority,
                desiredRateHz,
                deadlineMs,
                MaxCpuBudgetMs: 0.0,
                MaxGpuBudgetMs: 0.0,
                maxContentAgeFrames,
                hardDeadline),
            ERenderOutputQualityRequirement.GpuAccelerated,
            fallback,
            ResolveCompletionRequirement(outputKind),
            ProducerDependencySetId: 0UL,
            ConsumerDependencySetId: 0UL,
            frameId);
    }

    private static ERenderOutputClass ResolveClass(EFrameOutputKind kind)
        => kind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit => ERenderOutputClass.XrCritical,
            EFrameOutputKind.Present or EFrameOutputKind.DesktopMirror => ERenderOutputClass.Presentation,
            EFrameOutputKind.DesktopScene or EFrameOutputKind.EditorScenePanel => ERenderOutputClass.InteractiveScene,
            EFrameOutputKind.VrPickupMirror or EFrameOutputKind.InWorldMirror => ERenderOutputClass.VisibleMirror,
            EFrameOutputKind.Shadow => ERenderOutputClass.RequiredDependency,
            EFrameOutputKind.SceneCapture or EFrameOutputKind.LightProbeCapture or
                EFrameOutputKind.ReflectionProbeCapture or EFrameOutputKind.ImageBasedLighting or
                EFrameOutputKind.Thumbnail => ERenderOutputClass.BackgroundCapture,
            EFrameOutputKind.ImGuiOverlay or EFrameOutputKind.DynamicTextOverlay or
                EFrameOutputKind.UiPreview => ERenderOutputClass.Overlay,
            _ => ERenderOutputClass.Diagnostic,
        };

    private static ERenderOutputPriority ResolvePriority(
        EFrameOutputKind outputKind,
        ERenderOutputClass outputClass)
        => outputKind == EFrameOutputKind.Present
            ? ERenderOutputPriority.Critical
            : outputClass switch
        {
            ERenderOutputClass.XrCritical => ERenderOutputPriority.Critical,
            ERenderOutputClass.RequiredDependency => ERenderOutputPriority.RequiredDependency,
            ERenderOutputClass.Presentation or ERenderOutputClass.InteractiveScene or
                ERenderOutputClass.Overlay => ERenderOutputPriority.Interactive,
            ERenderOutputClass.VisibleMirror => ERenderOutputPriority.VisibleAuxiliary,
            ERenderOutputClass.BackgroundCapture => ERenderOutputPriority.Background,
            _ => ERenderOutputPriority.Diagnostic,
        };

    private static ERenderOutputFallbackPolicy ResolveFallback(
        EFrameOutputKind kind,
        ERenderOutputClass outputClass)
    {
        if (outputClass == ERenderOutputClass.XrCritical || outputClass == ERenderOutputClass.RequiredDependency)
            return ERenderOutputFallbackPolicy.None;

        ERenderOutputFallbackPolicy fallback =
            ERenderOutputFallbackPolicy.AllowCadenceReduction |
            ERenderOutputFallbackPolicy.AllowBudgetDeferral;
        if (outputClass is ERenderOutputClass.VisibleMirror or ERenderOutputClass.BackgroundCapture)
            fallback |= ERenderOutputFallbackPolicy.AllowStaleReuse;
        if (kind == EFrameOutputKind.DesktopMirror)
            fallback |= ERenderOutputFallbackPolicy.AllowCompositionReuse | ERenderOutputFallbackPolicy.AllowStaleReuse;
        return fallback;
    }

    private static ERenderOutputTargetClass ResolveTargetClass(EFrameOutputKind kind)
        => kind switch
        {
            EFrameOutputKind.DesktopScene or EFrameOutputKind.Present => ERenderOutputTargetClass.DesktopSwapchain,
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit => ERenderOutputTargetClass.RuntimeExternalImage,
            EFrameOutputKind.DesktopMirror => ERenderOutputTargetClass.CompositionTarget,
            EFrameOutputKind.ImGuiOverlay or EFrameOutputKind.DynamicTextOverlay => ERenderOutputTargetClass.Overlay,
            EFrameOutputKind.SceneCapture or EFrameOutputKind.LightProbeCapture or
                EFrameOutputKind.ReflectionProbeCapture or EFrameOutputKind.ImageBasedLighting or
                EFrameOutputKind.Thumbnail or EFrameOutputKind.Diagnostic => ERenderOutputTargetClass.CaptureTexture,
            _ => ERenderOutputTargetClass.OffscreenFramebuffer,
        };

    private static ERenderOutputCompletionRequirement ResolveCompletionRequirement(EFrameOutputKind kind)
        => kind switch
        {
            EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit =>
                ERenderOutputCompletionRequirement.GpuCompleteBeforeRuntimeRelease,
            EFrameOutputKind.Present or EFrameOutputKind.DesktopMirror =>
                ERenderOutputCompletionRequirement.BeforePresent,
            EFrameOutputKind.Shadow or EFrameOutputKind.SceneCapture or EFrameOutputKind.LightProbeCapture or
                EFrameOutputKind.ReflectionProbeCapture or EFrameOutputKind.ImageBasedLighting =>
                ERenderOutputCompletionRequirement.BeforeConsumer,
            _ => ERenderOutputCompletionRequirement.None,
        };

    private static ulong ResolveViewFamilyId(EFrameOutputKind outputKind, EVrOutputViewKind viewKind)
    {
        bool xrFamily = outputKind is EFrameOutputKind.OpenXREyeSubmit or EFrameOutputKind.OpenVRSubmit ||
            viewKind is EVrOutputViewKind.LeftEye or EVrOutputViewKind.RightEye or
                EVrOutputViewKind.LeftWide or EVrOutputViewKind.RightWide or
                EVrOutputViewKind.LeftInset or EVrOutputViewKind.RightInset;
        return xrFamily
            ? PackIdentity(2u, outputKind, EVrOutputViewKind.LeftEye)
            : PackIdentity(2u, outputKind, viewKind);
    }

    private static uint ResolveDefaultViewMask(EVrOutputViewKind viewKind)
        => viewKind switch
        {
            EVrOutputViewKind.LeftEye or EVrOutputViewKind.LeftWide or EVrOutputViewKind.LeftInset => 1u,
            EVrOutputViewKind.RightEye or EVrOutputViewKind.RightWide or EVrOutputViewKind.RightInset => 2u,
            _ => 0u,
        };

    private static ulong PackIdentity(uint domain, EFrameOutputKind outputKind, EVrOutputViewKind viewKind)
        => ((ulong)domain << 56) |
           ((ulong)(uint)outputKind + 1UL) << 24 |
           ((ulong)(uint)viewKind + 1UL);
}
