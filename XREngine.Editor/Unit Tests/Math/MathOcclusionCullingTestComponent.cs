using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>
/// Drives and reports one deterministic occlusion-culling qualification rig in the
/// Math Intersections Unit Testing World.
/// </summary>
public sealed class MathOcclusionCullingTestComponent : XRComponent
{
    private EOcclusionCullingMode _requestedMode;
    private EMeshSubmissionStrategy _requestedSubmissionStrategy;
    private Transform? _movingRevealTarget;
    private Vector3 _movingRevealTargetBaseTranslation;
    private bool _configured;
    private bool _animateRevealTarget = true;
    private float _revealCycleSeconds = 6.0f;
    private float _revealDistance = 5.25f;
    private double _animationStartTime;

    [Category("Occlusion Test")]
    [Description("Occlusion mode this qualification rig requests while it is active.")]
    public EOcclusionCullingMode RequestedMode
    {
        get => _requestedMode;
        private set => SetField(ref _requestedMode, value);
    }

    [Category("Occlusion Test")]
    [Description("Mesh submission strategy paired with the requested occlusion mode.")]
    public EMeshSubmissionStrategy RequestedSubmissionStrategy
    {
        get => _requestedSubmissionStrategy;
        private set => SetField(ref _requestedSubmissionStrategy, value);
    }

    [Category("Occlusion Test")]
    [Description("Moves the orange target between the wall's occluded region and a visible side region to exercise disocclusion recovery.")]
    public bool AnimateRevealTarget
    {
        get => _animateRevealTarget;
        set => SetField(ref _animateRevealTarget, value);
    }

    [Category("Occlusion Test")]
    [Description("Seconds for one complete hidden-to-visible-to-hidden reveal cycle.")]
    public float RevealCycleSeconds
    {
        get => _revealCycleSeconds;
        set => SetField(ref _revealCycleSeconds, Math.Clamp(value, 1.0f, 30.0f));
    }

    [Category("Occlusion Test")]
    [Description("Horizontal distance the orange reveal target moves from the center of the occluder.")]
    public float RevealDistance
    {
        get => _revealDistance;
        set => SetField(ref _revealDistance, Math.Clamp(value, 3.75f, 8.0f));
    }

    [Category("Occlusion Test Diagnostics")]
    [Description("Requested occlusion mode and mesh-submission strategy for this rig.")]
    public string RequestedConfiguration => GetRequestedConfiguration();

    [Category("Occlusion Test Diagnostics")]
    [Description("Most recently observed effective occlusion mode and mesh-submission strategy.")]
    public string EffectiveConfiguration => GetEffectiveConfiguration();

    [Category("Occlusion Test Diagnostics")]
    [Description("Live pass, warm-up, failure, or blocked result for this qualification rig.")]
    public string ValidationStatus => GetValidationStatus();

    [Category("Occlusion Test Diagnostics")]
    [Description("Per-frame counters for the active occlusion implementation.")]
    public string FrameTelemetry => GetFrameTelemetry();

    [Category("Occlusion Test Diagnostics")]
    [Description("Strategy-driven GPU BVH readiness and zero-readback submission counters.")]
    public string GpuBvhStatus => GetGpuBvhStatus();

    [Category("Occlusion Test Diagnostics")]
    [Description("Observed Hi-Z phase mode and phase-one/phase-two draw counters.")]
    public string GpuHiZPhaseStatus => GetGpuHiZPhaseStatus();

    internal void Configure(
        EOcclusionCullingMode requestedMode,
        EMeshSubmissionStrategy requestedSubmissionStrategy,
        Transform movingRevealTarget)
    {
        if (_configured)
            throw new InvalidOperationException("An occlusion-culling test component can only be configured once.");

        RequestedMode = requestedMode;
        RequestedSubmissionStrategy = requestedSubmissionStrategy;
        _movingRevealTarget = movingRevealTarget;
        _movingRevealTargetBaseTranslation = movingRevealTarget.Translation;
        _configured = true;
    }

    internal void RegisterControls(CustomUIComponent customUi)
    {
        customUi.Name = $"{GetPathDisplayName()} Properties";
        customUi.AddBoolField(
            "Animate Reveal Target",
            () => AnimateRevealTarget,
            value => AnimateRevealTarget = value,
            "Moves the orange target out from behind the wall and back again. A newly exposed target must appear without stale-visibility popping.");
        customUi.AddFloatField(
            "Reveal Cycle (s)",
            () => RevealCycleSeconds,
            value => RevealCycleSeconds = value,
            1.0f,
            30.0f,
            0.25f,
            "%.2f");
        customUi.AddFloatField(
            "Reveal Distance",
            () => RevealDistance,
            value => RevealDistance = value,
            3.75f,
            8.0f,
            0.1f,
            "%.2f");
        customUi.AddButtonField(
            "Restart Reveal Cycle",
            RestartRevealCycle,
            "Returns the orange target to the center behind the occluder and restarts its deterministic motion.");
        customUi.AddTextField("Requested Configuration", () => RequestedConfiguration);
        customUi.AddTextField("Effective Configuration", () => EffectiveConfiguration);
        customUi.AddTextField("Validation Status", () => ValidationStatus);
        customUi.AddTextField("Frame Telemetry", () => FrameTelemetry);

        if (RequestedMode == EOcclusionCullingMode.GpuHiZ)
        {
            customUi.AddTextField("GPU BVH", () => GpuBvhStatus);
            customUi.AddTextField("Hi-Z Phases", () => GpuHiZPhaseStatus);
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _animationStartTime = Engine.ElapsedTime;
        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateRevealTarget);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateRevealTarget);
        ResetRevealTargetTranslation();
        base.OnComponentDeactivated();
    }

    private void RestartRevealCycle()
    {
        _animationStartTime = Engine.ElapsedTime;
        ResetRevealTargetTranslation();
    }

    private void UpdateRevealTarget()
    {
        if (!_configured || _movingRevealTarget is null)
            return;

        if (!AnimateRevealTarget)
        {
            ResetRevealTargetTranslation();
            return;
        }

        float elapsed = (float)(Engine.ElapsedTime - _animationStartTime);
        float phase = elapsed * XRMath.TwoPIf / RevealCycleSeconds;
        Vector3 translation = _movingRevealTargetBaseTranslation;
        translation.X += MathF.Sin(phase) * RevealDistance;
        _movingRevealTarget.Translation = translation;
    }

    private void ResetRevealTargetTranslation()
    {
        if (_movingRevealTarget is not null &&
            _movingRevealTarget.Translation != _movingRevealTargetBaseTranslation)
            _movingRevealTarget.Translation = _movingRevealTargetBaseTranslation;
    }

    private string GetRequestedConfiguration()
        => $"{RequestedMode} + {RequestedSubmissionStrategy}";

    private static string GetEffectiveConfiguration()
        => $"{GetEffectiveMode()} + {GetEffectiveSubmissionStrategy()}";

    private static EOcclusionCullingMode GetEffectiveMode()
        => VulkanFeatureProfile.ResolveOcclusionCullingMode(
            RuntimeEngine.Rendering.Settings.GpuOcclusionCullingMode);

    private static EMeshSubmissionStrategy GetEffectiveSubmissionStrategy()
        => RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();

    private string GetValidationStatus()
    {
        if (GetEffectiveMode() != RequestedMode ||
            GetEffectiveSubmissionStrategy() != RequestedSubmissionStrategy)
        {
            return $"WARMING / OVERRIDDEN: observed {GetEffectiveConfiguration()}.";
        }

        return RequestedMode switch
        {
            EOcclusionCullingMode.CpuQueryAsync => GetCpuQueryValidationStatus(),
            EOcclusionCullingMode.CpuSoftwareOcclusion => GetCpuRasterValidationStatus(),
            EOcclusionCullingMode.GpuHiZ => GetGpuValidationStatus(),
            _ => "FAIL: this rig was configured with an unsupported occlusion mode.",
        };
    }

    private static string GetCpuQueryValidationStatus()
    {
        if (OcclusionTelemetry.CpuPassesActive == 0 || OcclusionTelemetry.CpuTested == 0)
            return "WARMING: waiting for the CPU async-query path to test render commands.";
        if (OcclusionTelemetry.CpuQueryLatencySamples == 0 &&
            OcclusionTelemetry.CpuDecisionVisibleQuery == 0 &&
            OcclusionTelemetry.CpuDecisionSkip == 0)
            return "WARMING: hardware queries are pending; waiting for asynchronous decisions.";
        if (OcclusionTelemetry.CpuCulled == 0)
            return "WARMING: query results are resolving, but no hidden target has passed hysteresis yet.";

        return $"PASS: CPU queries tested {OcclusionTelemetry.CpuTested:N0} commands and culled {OcclusionTelemetry.CpuCulled:N0}.";
    }

    private static string GetCpuRasterValidationStatus()
    {
        if (OcclusionTelemetry.CpuSocOccludersRasterized == 0)
            return "WARMING: waiting for the CPU occluder mask to rasterize the wall.";
        if (OcclusionTelemetry.CpuSocTested == 0)
            return "WARMING: the CPU mask is ready; waiting for AABB visibility tests.";
        if (OcclusionTelemetry.CpuSocCulled == 0)
            return "WARMING: CPU raster tests are active, but no hidden target was rejected this frame.";

        return $"PASS: CPU rasterization tested {OcclusionTelemetry.CpuSocTested:N0} bounds and culled {OcclusionTelemetry.CpuSocCulled:N0}.";
    }

    private string GetGpuValidationStatus()
    {
        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        if (world is null)
            return "WARMING: no render-world instance is assigned.";

        var gpuScene = world.VisualScene.GPUCommands;
        GpuBvhDiagnostics diagnostics = gpuScene.GpuBvhDiagnostics;
        if (!IsGpuBvhReady(gpuScene))
            return "WARMING: GPU Hi-Z is active; waiting for the strategy-driven GPU BVH.";
        if (diagnostics.ZeroReadbackSubmissionCount == 0)
            return "WARMING: GPU BVH is ready; waiting for a zero-readback submission.";
        if (OcclusionTelemetry.GpuPassesWithReadback > 0)
            return "FAIL: the selected GPU path performed a CPU visibility-count readback.";

        string hiZMode = RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode;
        if (string.IsNullOrWhiteSpace(hiZMode) ||
            hiZMode.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "WARMING: zero-readback GPU-BVH submissions are active; waiting for Hi-Z telemetry.";

        bool twoPhase = hiZMode.StartsWith("two-phase", StringComparison.Ordinal);
        if (!twoPhase)
        {
            return $"FAIL: zero-readback Hi-Z and GPU BVH are active, but the renderer reports '{hiZMode}' instead of persistent phase-1/phase-2 visibility.";
        }

        return "PASS: two-phase GPU Hi-Z, zero-readback submission, and GPU-BVH acceleration are active.";
    }

    private string GetFrameTelemetry()
    {
        return RequestedMode switch
        {
            EOcclusionCullingMode.CpuQueryAsync =>
                $"tested={OcclusionTelemetry.CpuTested:N0}, culled={OcclusionTelemetry.CpuCulled:N0}, pending={OcclusionTelemetry.CpuPendingQueries:N0}, latencySamples={OcclusionTelemetry.CpuQueryLatencySamples:N0}",
            EOcclusionCullingMode.CpuSoftwareOcclusion =>
                $"occluders={OcclusionTelemetry.CpuSocOccludersRasterized:N0}, tested={OcclusionTelemetry.CpuSocTested:N0}, culled={OcclusionTelemetry.CpuSocCulled:N0}, raster={OcclusionTelemetry.CpuSocRasterMilliseconds:F3} ms",
            EOcclusionCullingMode.GpuHiZ => GetGpuFrameTelemetry(),
            _ => "No telemetry is defined for this mode.",
        };
    }

    private string GetGpuFrameTelemetry()
    {
        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        if (world is null)
            return "No render-world instance is assigned.";

        GpuBvhDiagnostics diagnostics = world.VisualScene.GPUCommands.GpuBvhDiagnostics;
        return $"primitives={diagnostics.LogicalPrimitiveCount:N0}, nodes={diagnostics.LogicalNodeCount:N0}, zeroReadbackSubmissions={diagnostics.ZeroReadbackSubmissionCount:N0}, hiZ={RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode}";
    }

    private string GetGpuBvhStatus()
    {
        XRWorldInstance? world = WorldAs<XRWorldInstance>();
        if (world is null)
            return "WARMING: no render-world instance is assigned.";

        var gpuScene = world.VisualScene.GPUCommands;
        GpuBvhDiagnostics diagnostics = gpuScene.GpuBvhDiagnostics;
        bool ready = IsGpuBvhReady(gpuScene);
        return $"requested={gpuScene.UseGpuBvh}, ready={ready}, primitives={diagnostics.LogicalPrimitiveCount:N0}, nodes={diagnostics.LogicalNodeCount:N0}, zeroReadbackSubmissions={diagnostics.ZeroReadbackSubmissionCount:N0}";
    }

    private static string GetGpuHiZPhaseStatus()
        => $"mode={RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode}, onePhase={RuntimeEngine.Rendering.Stats.GpuDriven.HiZOnePhaseFrames:N0}, twoPhase={RuntimeEngine.Rendering.Stats.GpuDriven.HiZTwoPhaseFrames:N0}, phase1Draws={RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseOneDraws:N0}, phase2Draws={RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseTwoDraws:N0}";

    private static bool IsGpuBvhReady(GPUScene gpuScene)
        => gpuScene.UseGpuBvh &&
            gpuScene.UseInternalBvh &&
            gpuScene.BvhProvider?.IsBvhReady == true;

    private string GetPathDisplayName()
    {
        return RequestedMode switch
        {
            EOcclusionCullingMode.CpuQueryAsync => "CPU Async Query Occlusion",
            EOcclusionCullingMode.CpuSoftwareOcclusion => "CPU Rasterized Occlusion",
            EOcclusionCullingMode.GpuHiZ => "GPU Two-Pass Hi-Z Qualification",
            _ => "Occlusion",
        };
    }
}
