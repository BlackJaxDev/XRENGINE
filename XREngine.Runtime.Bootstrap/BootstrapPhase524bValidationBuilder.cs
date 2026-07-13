using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Occlusion;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.Bootstrap.Builders;

/// <summary>
/// Builds the deterministic, query-eligible scene used by the Vulkan/OpenXR
/// Phase 5.2.4b acceptance validator. The workload is never present unless the
/// dedicated validation environment switch is enabled.
/// </summary>
public static class BootstrapPhase524bValidationBuilder
{
    public const string RootNodeName = "Phase524bValidationRoot";
    public const string OccluderNodeName = "Phase524bOccluder";
    public const string HiddenTargetNodeName = "Phase524bHiddenTarget";
    public const string StableSentinelNodeName = "Phase524bStableVisibleSentinel";
    public const string DesktopMovingSentinelNodeName = "Phase524bDesktopMovingVisibleSentinel";
    public const string DesktopTopEdgeSentinelNodeName = "Phase524bDesktopTopEdgeVisibleSentinel";
    public const string SpsMovingSentinelNodeName = "Phase524bSpsMovingVisibleSentinel";
    public const string SpsTopEdgeSentinelNodeName = "Phase524bSpsTopEdgeVisibleSentinel";

    private const uint MotionPeriodTicks = 120u;
    private static readonly Vector3 MovingSentinelCenter = new(-2.35f, 0.80f, -5.50f);
    private static readonly Vector3 MovingSentinelAmplitude = new(0.35f, 0.20f, 0.0f);
    private static readonly Vector3 HeadsetRelativeMovingSentinelCenter = new(-0.45f, -0.30f, -1.25f);
    private static readonly Vector3 HeadsetRelativeMovingSentinelAmplitude = new(0.12f, 0.06f, 0.0f);
    private static readonly Vector3 HeadsetRelativeTopEdgeSentinelTranslation = new(0.0f, 2.45f, -3.20f);
    private static readonly Vector3 WorldRelativeTopEdgeSentinelTranslation = new(0.0f, 0.88f, -3.20f);
    private static readonly Vector3 OccluderCenter = new(0.0f, 0.80f, -4.00f);
    private const float DisocclusionRevealOffsetX = 3.25f;
    private const float HeadRotationDegreesPerFrame = 1.5f;
    private const float HeadTranslationMetersPerFrame = 0.025f;
    private const float MotionStopMetersPerFrame = 0.035f;

    public static bool IsEnabled
        => IsEnabledValue(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPhase524bValidation));

    public static void AddValidationWorkload(SceneNode rootNode)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        if (!IsEnabled)
            return;

        SceneNode validationRoot = rootNode.NewChild(RootNodeName);

        SceneNode occluder = AddBox(
            validationRoot,
            OccluderNodeName,
            OccluderCenter,
            new Vector3(2.60f, 3.80f, 0.45f),
            new ColorF4(0.08f, 0.11f, 0.16f, 1.0f));

        AddBox(
            validationRoot,
            HiddenTargetNodeName,
            new Vector3(0.0f, 0.80f, -5.75f),
            new Vector3(0.90f),
            new ColorF4(4.0f, 0.10f, 0.10f, 1.0f));

        AddBox(
            validationRoot,
            StableSentinelNodeName,
            new Vector3(2.35f, 0.80f, -5.50f),
            new Vector3(0.75f),
            new ColorF4(0.15f, 4.0f, 4.0f, 1.0f));

        // Desktop and strict-SPS outputs use different cameras and culling
        // scopes. Give each output an explicit oracle instead of asking one
        // scene node to remain visible in both unrelated projections.
        SceneNode? headsetNode = rootNode.FindDescendantByName("VRHeadsetNode");
        AddBox(
            validationRoot,
            DesktopTopEdgeSentinelNodeName,
            WorldRelativeTopEdgeSentinelTranslation,
            new Vector3(1.20f, 0.14f, 0.14f),
            new ColorF4(4.0f, 0.20f, 3.20f, 1.0f));

        SceneNode desktopMovingSentinel = AddBox(
            validationRoot,
            DesktopMovingSentinelNodeName,
            CalculateMovingSentinelTranslation(0u),
            new Vector3(0.70f),
            new ColorF4(4.0f, 1.40f, 0.10f, 1.0f));

        SceneNode? spsMovingSentinel = null;
        if (headsetNode is not null)
        {
            AddBox(
                headsetNode,
                SpsTopEdgeSentinelNodeName,
                HeadsetRelativeTopEdgeSentinelTranslation,
                new Vector3(1.20f, 0.14f, 0.14f),
                new ColorF4(4.0f, 0.20f, 3.20f, 1.0f));
            spsMovingSentinel = AddBox(
                headsetNode,
                SpsMovingSentinelNodeName,
                CalculateHeadsetRelativeMovingSentinelTranslation(0u),
                new Vector3(0.28f),
                new ColorF4(4.0f, 1.40f, 0.10f, 1.0f));
        }

        Phase524bTemporalScenarioDiagnostics.Reset();
        Phase524bScenarioComponent scenario = validationRoot.AddComponent<Phase524bScenarioComponent>()!;
        scenario.Configure(
            occluder.Transform as Transform,
            desktopMovingSentinel.Transform as Transform,
            spsMovingSentinel?.Transform as Transform,
            headsetNode?.Parent?.Transform as Transform);

        Debug.Rendering(
            "[Phase524bValidation] Added deterministic occlusion/temporal workload: " +
            "occluder='{0}', hiddenTarget='{1}', stableSentinel='{2}', desktopMoving='{3}', desktopTop='{4}', " +
            "spsMoving='{5}', spsTop='{6}', temporalSequenceFrames={7}.",
            OccluderNodeName,
            HiddenTargetNodeName,
            StableSentinelNodeName,
            DesktopMovingSentinelNodeName,
            DesktopTopEdgeSentinelNodeName,
            SpsMovingSentinelNodeName,
            SpsTopEdgeSentinelNodeName,
            Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame);
    }

    /// <summary>
    /// Returns the repeatable update-tick trajectory used by the moving HDR
    /// sentinel. The period is deliberately frame-count based rather than
    /// wall-clock based so captured cohorts have the same sequence.
    /// </summary>
    public static Vector3 CalculateMovingSentinelTranslation(uint tick)
        => CalculateMovingSentinelTranslation(tick, MovingSentinelCenter, MovingSentinelAmplitude);

    /// <summary>
    /// Returns the repeatable trajectory used when an OpenXR headset node is
    /// available. Keeping the sentinel in headset space guarantees motion-vector
    /// coverage even when the runtime supplies a moving HMD pose.
    /// </summary>
    public static Vector3 CalculateHeadsetRelativeMovingSentinelTranslation(uint tick)
        => CalculateMovingSentinelTranslation(
            tick,
            HeadsetRelativeMovingSentinelCenter,
            HeadsetRelativeMovingSentinelAmplitude);

    public static EPhase524bTemporalScenario ResolveTemporalScenario(int sequenceFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceFrame);
        return sequenceFrame switch
        {
            < 12 => EPhase524bTemporalScenario.ObjectMotion,
            < 20 => EPhase524bTemporalScenario.StaticPose,
            < 28 => EPhase524bTemporalScenario.HeadRotation,
            < 36 => EPhase524bTemporalScenario.HeadTranslation,
            < 52 => EPhase524bTemporalScenario.Disocclusion,
            < Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame => EPhase524bTemporalScenario.MotionStop,
            _ => EPhase524bTemporalScenario.ObjectMotion,
        };
    }

    public static float CalculateTemporalHeadYawDegrees(int sequenceFrame)
        => sequenceFrame is >= 20 and < 28
            ? (sequenceFrame - 19) * HeadRotationDegreesPerFrame
            : 0.0f;

    public static Vector3 CalculateTemporalHeadTranslation(int sequenceFrame)
        => sequenceFrame is >= 28 and < 36
            ? new Vector3((sequenceFrame - 27) * HeadTranslationMetersPerFrame, 0.0f, 0.0f)
            : Vector3.Zero;

    public static Vector3 CalculateTemporalOccluderTranslation(int sequenceFrame)
    {
        if (sequenceFrame < 41 || sequenceFrame >= 52)
            return OccluderCenter;
        if (sequenceFrame >= 48)
            return OccluderCenter + new Vector3(DisocclusionRevealOffsetX, 0.0f, 0.0f);

        float alpha = (sequenceFrame - 40) / 8.0f;
        return OccluderCenter + new Vector3(DisocclusionRevealOffsetX * alpha, 0.0f, 0.0f);
    }

    public static Vector3 CalculateTemporalMovingSentinelTranslation(int sequenceFrame, bool headsetRelative)
    {
        Vector3 center = headsetRelative ? HeadsetRelativeMovingSentinelCenter : MovingSentinelCenter;
        if (sequenceFrame < 12)
        {
            return headsetRelative
                ? CalculateHeadsetRelativeMovingSentinelTranslation((uint)sequenceFrame)
                : CalculateMovingSentinelTranslation((uint)sequenceFrame);
        }

        if (sequenceFrame is >= 52 and < Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame)
        {
            int movingFrame = Math.Min(sequenceFrame - 52, 8);
            return center + new Vector3(movingFrame * MotionStopMetersPerFrame, 0.0f, 0.0f);
        }

        if (sequenceFrame >= Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame)
        {
            uint repeatTick = (uint)(sequenceFrame - Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame);
            return headsetRelative
                ? CalculateHeadsetRelativeMovingSentinelTranslation(repeatTick)
                : CalculateMovingSentinelTranslation(repeatTick);
        }

        return center;
    }

    private static Vector3 CalculateMovingSentinelTranslation(
        uint tick,
        in Vector3 center,
        in Vector3 amplitude)
    {
        float phase = (tick % MotionPeriodTicks) * (XRMath.TwoPIf / MotionPeriodTicks);
        return center + new Vector3(
            MathF.Sin(phase) * amplitude.X,
            MathF.Sin(phase * 2.0f) * amplitude.Y,
            0.0f);
    }

    internal static bool IsEnabledValue(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static SceneNode AddBox(
        SceneNode parent,
        string name,
        in Vector3 translation,
        in Vector3 size,
        in ColorF4 color)
    {
        SceneNode node = parent.NewChild(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = translation;

        Vector3 halfSize = size * 0.5f;
        XRMaterial material = XRMaterial.CreateUnlitColorMaterialForward(color);
        material.Name = name switch
        {
            OccluderNodeName => CpuOcclusionValidationEvidence.OccluderMaterialName,
            HiddenTargetNodeName => CpuOcclusionValidationEvidence.HiddenTargetMaterialName,
            StableSentinelNodeName => CpuOcclusionValidationEvidence.StableSentinelMaterialName,
            DesktopMovingSentinelNodeName => CpuOcclusionValidationEvidence.DesktopMovingSentinelMaterialName,
            DesktopTopEdgeSentinelNodeName => CpuOcclusionValidationEvidence.DesktopTopEdgeSentinelMaterialName,
            SpsMovingSentinelNodeName => CpuOcclusionValidationEvidence.SpsMovingSentinelMaterialName,
            SpsTopEdgeSentinelNodeName => CpuOcclusionValidationEvidence.SpsTopEdgeSentinelMaterialName,
            _ => $"{name}Material",
        };
        material.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.ExcludeFromCpuOcclusion = false;

        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Name = $"{name}Model";
        model.Model = new Model(
        [
            new SubMesh(XRMesh.Shapes.SolidBox(-halfSize, halfSize), material)
            {
                CullingBounds = new AABB(-halfSize, halfSize),
            }
        ]);

        return node;
    }

    private sealed class Phase524bScenarioComponent : XRComponent
    {
        private Transform? _occluder;
        private Transform? _desktopMover;
        private Transform? _spsMover;
        private Transform? _headMotionRoot;
        private Vector3 _headMotionRootBaseTranslation;
        private Quaternion _headMotionRootBaseRotation = Quaternion.Identity;

        public void Configure(
            Transform? occluder,
            Transform? desktopMover,
            Transform? spsMover,
            Transform? headMotionRoot)
        {
            _occluder = occluder;
            _desktopMover = desktopMover;
            _spsMover = spsMover;
            _headMotionRoot = headMotionRoot;
            if (_headMotionRoot is not null)
            {
                _headMotionRootBaseTranslation = _headMotionRoot.Translation;
                _headMotionRootBaseRotation = _headMotionRoot.Rotation;
            }
            ApplyScenario();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            ApplyScenario();
            RegisterTick(ETickGroup.Normal, ETickOrder.Animation, ApplyScenario);
        }

        protected override void OnComponentDeactivated()
        {
            UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, ApplyScenario);
            if (_headMotionRoot is not null)
            {
                _headMotionRoot.Translation = _headMotionRootBaseTranslation;
                _headMotionRoot.Rotation = _headMotionRootBaseRotation;
            }
            base.OnComponentDeactivated();
        }

        private void ApplyScenario()
        {
            int sequenceFrame = Phase524bTemporalScenarioDiagnostics.SequenceFrame;
            if (_occluder is not null)
                _occluder.Translation = CalculateTemporalOccluderTranslation(sequenceFrame);
            if (_desktopMover is not null)
                _desktopMover.Translation = CalculateTemporalMovingSentinelTranslation(sequenceFrame, headsetRelative: false);
            if (_spsMover is not null)
                _spsMover.Translation = CalculateTemporalMovingSentinelTranslation(sequenceFrame, headsetRelative: true);
            if (_headMotionRoot is null)
                return;

            _headMotionRoot.Translation = _headMotionRootBaseTranslation +
                CalculateTemporalHeadTranslation(sequenceFrame);
            float yawRadians = CalculateTemporalHeadYawDegrees(sequenceFrame) * (MathF.PI / 180.0f);
            _headMotionRoot.Rotation = Quaternion.Normalize(
                _headMotionRootBaseRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians));
        }
    }
}
