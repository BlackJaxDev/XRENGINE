using System.ComponentModel;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Scene.Transforms;
using XREngine.Core.Attributes;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Renders live MotionBricks procedural locomotion debug primitives in 3D:
/// ground-projected future trajectory dots (green when clear, red-orange when collision-clamped),
/// the avatar's current 30-joint SOMA skeleton with foot-contact markers, and future predicted
/// SOMA skeletons sampled from the active trajectory buffer.
/// </summary>
[Category("Animation")]
[DisplayName("MotionBricks Debug Visualizer")]
[Description("Draws MotionBricks future ground trajectory dots, collision clamps, and current/future SOMA skeletons.")]
public sealed class MotionBricksDebugDrawComponent : XRComponent, IRenderable
{
    // Keep debug line colors below the HDR bloom threshold so the avatar and ground dots stay sharp.
    private static readonly ColorF4 ClearDotColor = new(0.10f, 0.68f, 0.22f, 1.0f);
    private static readonly ColorF4 ClampedDotColor = new(0.75f, 0.18f, 0.08f, 1.0f);
    private static readonly ColorF4 TrajectoryPathColor = new(0.14f, 0.62f, 0.28f, 1.0f);
    private static readonly ColorF4 TargetAnchorColor = new(0.12f, 0.62f, 0.72f, 1.0f);
    private static readonly ColorF4 HeadingTickColor = new(0.65f, 0.65f, 0.65f, 1.0f);

    private static readonly ColorF4 CurrentSkeletonColor = new(0.06f, 0.58f, 0.70f, 1.0f);
    private static readonly ColorF4 FutureMidSkeletonColor = new(0.72f, 0.58f, 0.08f, 1.0f);
    private static readonly ColorF4 FutureEndSkeletonColor = new(0.68f, 0.18f, 0.58f, 1.0f);
    private static readonly ColorF4 ContactGroundedColor = new(0.10f, 0.68f, 0.20f, 1.0f);
    private static readonly ColorF4 ContactAirborneColor = new(0.18f, 0.34f, 0.68f, 1.0f);

    private readonly RenderInfo3D _renderInfo;
    private readonly MotionBricksDebugSnapshot _snapshot = new();

    private AnimStateMachineComponent? _animator;
    private TransformBase? _footAnchorTransform;
    private bool _renderEnabled = true;
    private bool _showTrajectoryDots = true;
    private bool _showCurrentSkeleton = true;
    private bool _showFutureSkeletons = true;
    private bool _showFootContacts = true;

    public RenderInfo RenderInfo => _renderInfo;
    public RenderInfo[] RenderedObjects { get; }

    public AnimStateMachineComponent? Animator
    {
        get => _animator;
        set => SetField(ref _animator, value);
    }

    /// <summary>
    /// Optional interpolated foot transform that anchors debug skeletons and trajectory dots to the
    /// rendered avatar position instead of the uninterpolated fixed-step physics position.
    /// </summary>
    public TransformBase? FootAnchorTransform
    {
        get => _footAnchorTransform;
        set => SetField(ref _footAnchorTransform, value);
    }

    /// <summary>
    /// Enables or disables 3D rendering of the locomotion debug overlays without detaching the component.
    /// </summary>
    public bool RenderEnabled
    {
        get => _renderEnabled;
        set => SetField(ref _renderEnabled, value);
    }

    /// <summary>
    /// Toggles 3D rendering of the MotionBricks locomotion debug overlays.
    /// </summary>
    public void ToggleRenderEnabled()
        => RenderEnabled = !RenderEnabled;

    public bool ShowTrajectoryDots
    {
        get => _showTrajectoryDots;
        set => SetField(ref _showTrajectoryDots, value);
    }

    public bool ShowCurrentSkeleton
    {
        get => _showCurrentSkeleton;
        set => SetField(ref _showCurrentSkeleton, value);
    }

    public bool ShowFutureSkeletons
    {
        get => _showFutureSkeletons;
        set => SetField(ref _showFutureSkeletons, value);
    }

    public bool ShowFootContacts
    {
        get => _showFootContacts;
        set => SetField(ref _showFootContacts, value);
    }

    public MotionBricksDebugDrawComponent()
    {
        RenderedObjects = [_renderInfo = RenderInfo3D.New(this, EDefaultRenderPass.OnTopForward, RenderDebug)];
        _renderInfo.Layer = DefaultLayers.GizmosIndex;
        _renderInfo.LocalCullingVolume = null;
    }

    private void RenderDebug()
    {
        if (!_renderEnabled
            || !IsActive
            || !IsActiveInHierarchy
            || RuntimeEngine.Rendering.State.IsShadowPass
            || RuntimeEngine.Rendering.State.IsLightProbePass
            || RuntimeEngine.Rendering.State.IsSceneCapturePass)
            return;

        IMotionBricksLocomotionDriver? driver = ResolveActiveDriver();
        if (driver is null || !driver.TryCopyDebugSnapshot(_snapshot) || !_snapshot.IsValid)
            return;

        RebaseSnapshotToRenderedFootAnchor(_snapshot);

        if (_showTrajectoryDots && _snapshot.TrajectoryPointCount > 0)
            DrawTrajectoryDots(_snapshot);

        if (_showCurrentSkeleton && _snapshot.HasCurrentSkeleton)
        {
            DrawSkeleton(_snapshot.CurrentSkeletonJoints, CurrentSkeletonColor);
            if (_showFootContacts)
                DrawFootContacts(_snapshot.CurrentSkeletonJoints, _snapshot.CurrentFootContacts);
        }

        if (_showFutureSkeletons)
        {
            if (_snapshot.HasFutureMidSkeleton)
                DrawSkeleton(_snapshot.FutureMidSkeletonJoints, FutureMidSkeletonColor);

            if (_snapshot.HasFutureEndSkeleton)
                DrawSkeleton(_snapshot.FutureEndSkeletonJoints, FutureEndSkeletonColor);
        }
    }

    private static void DrawTrajectoryDots(MotionBricksDebugSnapshot snapshot)
    {
        const float liftY = 0.035f;
        Vector3 prevPoint = snapshot.CurrentRootGroundPosition + new Vector3(0.0f, liftY, 0.0f);
        RuntimeEngine.Rendering.Debug.RenderSphere(prevPoint, 0.04f, solid: false, CurrentSkeletonColor);

        for (int i = 0; i < snapshot.TrajectoryPointCount; i++)
        {
            MotionBricksTrajectoryPoint dot = snapshot.TrajectoryPoints[i];
            Vector3 lifted = dot.Position + new Vector3(0.0f, liftY, 0.0f);
            ColorF4 dotColor = dot.IsCollisionClamped ? ClampedDotColor : ClearDotColor;
            ColorF4 lineColor = dot.IsCollisionClamped ? ClampedDotColor : TrajectoryPathColor;

            RuntimeEngine.Rendering.Debug.RenderLine(prevPoint, lifted, lineColor);
            float radius = i == snapshot.TrajectoryPointCount - 1 ? 0.055f : 0.038f;
            RuntimeEngine.Rendering.Debug.RenderSphere(lifted, radius, solid: false, dotColor);

            // Draw planned facing tick and surface normal tick at each ground dot.
            Vector3 facingDir = new(MathF.Sin(dot.FacingYawRadians), 0.0f, MathF.Cos(dot.FacingYawRadians));
            RuntimeEngine.Rendering.Debug.RenderLine(lifted, lifted + facingDir * 0.16f, HeadingTickColor);
            if (dot.HasGroundHit && dot.GroundNormal.LengthSquared() > 1.0e-4f)
                RuntimeEngine.Rendering.Debug.RenderLine(lifted, lifted + dot.GroundNormal * 0.11f, dotColor);

            prevPoint = lifted;
        }

        Vector3 targetPoint = snapshot.TargetRootGroundPosition + new Vector3(0.0f, liftY, 0.0f);
        RuntimeEngine.Rendering.Debug.RenderSphere(targetPoint, 0.07f, solid: false, TargetAnchorColor);
    }

    private static void DrawSkeleton(Vector3[] joints, ColorF4 boneColor)
    {
        int[] parents = MotionBricksDebugSnapshot.SomaJointParentIndices;
        int count = Math.Min(joints.Length, parents.Length);
        for (int joint = 1; joint < count; joint++)
        {
            // Skip tiny eye, jaw, and fingertip twig joints so the stick skeleton reads cleanly.
            if (joint is 7 or 8 or 9 or 14 or 15 or 20 or 21)
                continue;

            int parent = parents[joint];
            if (parent >= 0 && parent < count)
                RuntimeEngine.Rendering.Debug.RenderLine(joints[parent], joints[joint], boneColor);
        }
    }

    private static void DrawFootContacts(Vector3[] joints, Vector4 contacts)
    {
        if (joints.Length < MotionBricksDebugSnapshot.SomaJointCount)
            return;

        DrawFootContactMarker(joints[24], contacts.X); // LeftFoot
        DrawFootContactMarker(joints[25], contacts.Y); // LeftToeBase
        DrawFootContactMarker(joints[28], contacts.Z); // RightFoot
        DrawFootContactMarker(joints[29], contacts.W); // RightToeBase
    }

    private static void DrawFootContactMarker(Vector3 position, float contactWeight)
    {
        ColorF4 color = contactWeight >= 0.5f ? ContactGroundedColor : ContactAirborneColor;
        float radius = contactWeight >= 0.5f ? 0.032f : 0.020f;
        RuntimeEngine.Rendering.Debug.RenderSphere(position, radius, solid: false, color);
    }

    private IMotionBricksLocomotionDriver? ResolveActiveDriver()
    {
        AnimStateMachineComponent? animator = _animator
            ?? (SceneNode?.GetComponentInHierarchy(nameof(AnimStateMachineComponent)) as AnimStateMachineComponent);
        if (animator is null)
            return null;

        AnimStateMachine stateMachine = animator.StateMachine;
        for (int i = 0; i < stateMachine.Layers.Count; i++)
        {
            AnimLayer? layer = stateMachine.Layers[i];
            if (layer?.CurrentState?.Motion is MotionBricksLocomotionMotion motion && motion.ActiveDriver is { IsReady: true } driver)
                return driver;
        }

        return null;
    }

    private void RebaseSnapshotToRenderedFootAnchor(MotionBricksDebugSnapshot snapshot)
    {
        TransformBase? anchorTransform = _footAnchorTransform
            ?? _animator?.SceneNode?.Transform
            ?? SceneNode?.Transform;
        if (anchorTransform is null)
            return;

        // Read the render-thread matrix so debug skeletons follow the interpolated avatar pose
        // instead of stepping at the lower fixed physics rate.
        Vector3 renderedFoot = anchorTransform.RenderMatrix.Translation;
        if (!IsFinite(renderedFoot))
            renderedFoot = anchorTransform.WorldTranslation;
        if (!IsFinite(renderedFoot) || !IsFinite(snapshot.CurrentRootGroundPosition))
            return;

        Vector3 anchorDelta = renderedFoot - snapshot.CurrentRootGroundPosition;
        if (anchorDelta.LengthSquared() <= 1.0e-10f)
            return;

        snapshot.CurrentRootGroundPosition = renderedFoot;
        snapshot.TargetRootGroundPosition += anchorDelta;

        if (snapshot.HasCurrentSkeleton)
            OffsetSkeletonJoints(snapshot.CurrentSkeletonJoints, anchorDelta);
        if (snapshot.HasFutureMidSkeleton)
            OffsetSkeletonJoints(snapshot.FutureMidSkeletonJoints, anchorDelta);
        if (snapshot.HasFutureEndSkeleton)
            OffsetSkeletonJoints(snapshot.FutureEndSkeletonJoints, anchorDelta);

        Vector3 horizontalDelta = new(anchorDelta.X, 0.0f, anchorDelta.Z);
        for (int i = 0; i < snapshot.TrajectoryPointCount; i++)
        {
            MotionBricksTrajectoryPoint dot = snapshot.TrajectoryPoints[i];
            Vector3 shiftedPosition = dot.Position + (dot.HasGroundHit ? horizontalDelta : anchorDelta);
            snapshot.TrajectoryPoints[i] = new MotionBricksTrajectoryPoint(
                shiftedPosition,
                dot.GroundNormal,
                dot.FacingYawRadians,
                dot.TravelYawRadians,
                dot.SpeedMetersPerSecond,
                dot.TimeSeconds,
                dot.IsCollisionClamped,
                dot.HasGroundHit);
        }
    }

    private static void OffsetSkeletonJoints(Vector3[] joints, Vector3 delta)
    {
        for (int i = 0; i < joints.Length; i++)
            joints[i] += delta;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
