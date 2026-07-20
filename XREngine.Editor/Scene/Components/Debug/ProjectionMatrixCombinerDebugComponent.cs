using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed class ProjectionMatrixCombinerDebugComponent : XRComponent
{
    private float _eyeSeparationDistance = 0.064f;
    private float _rotationAngleDegrees = 20.0f;
    private float _verticalFieldOfView = 65.0f;
    private float _cyclopsVerticalFieldOfView = 65.0f;
    private float _aspectRatio = 16.0f / 9.0f;
    private float _nearPlane = 0.3f;
    private float _farPlane = 18.0f;
    private float _yawAmplitudeScale = 1.0f;
    private float _pitchAmplitudeScale = 1.0f;
    private float _orbitYawNormalized = 1.0f;
    private float _orbitPitchNormalized = 0.0f;
    private Vector3 _rigOffset = Vector3.Zero;
    private bool _showLeftFrustum = true;
    private bool _showRightFrustum = true;
    private bool _showCyclopsFrustum = true;
    private bool _showCombinedFrustum = true;
    private bool _showEyeMarkers = true;
    private bool _preferStereoFarDistance = true;
    private bool _solveCombinedViewOrientation;
    private bool _refineCombinedViewOrientation = true;
    private bool _highSpeedMode;
    private bool _showCombinedViewBasis = true;
    private float _combinedViewBasisLength = 2.0f;
    private ColorF4 _leftFrustumColor = ColorF4.LightBlue;
    private ColorF4 _rightFrustumColor = ColorF4.Orange;
    private ColorF4 _cyclopsFrustumColor = ColorF4.White;
    private ColorF4 _combinedFrustumColor = ColorF4.LightGreen;
    private string _lastCombinedViewCandidateLabel = "Identity";
    private float _lastCombinedViewCost;
    private bool _hasCachedPointCloud;
    private ProjectionMatrixCombiner.FrustumPointCloud _cachedPointCloud;
    private Matrix4x4[]? _cachedPointCloudProjections;
    private Matrix4x4[]? _cachedPointCloudViews;
    private int _cachedPointCloudFarSourceCount = -1;

    [Browsable(false)]
    public CameraComponent? LeftEyeCamera { get; private set; }

    [Browsable(false)]
    public CameraComponent? RightEyeCamera { get; private set; }

    [Browsable(false)]
    public CameraComponent? CyclopsEyeCamera { get; private set; }

    [Browsable(false)]
    public DebugDrawComponent? DebugDraw { get; private set; }

    [Browsable(false)]
    public Transform? CyclopsOrbitTransform { get; private set; }

    public float EyeSeparationDistance
    {
        get => _eyeSeparationDistance;
        set => SetField(ref _eyeSeparationDistance, MathF.Max(0.0f, value));
    }

    public float RotationAngleDegrees
    {
        get => _rotationAngleDegrees;
        set => SetField(ref _rotationAngleDegrees, MathF.Max(0.0f, value));
    }

    public float VerticalFieldOfView
    {
        get => _verticalFieldOfView;
        set => SetField(ref _verticalFieldOfView, Math.Clamp(value, 1.0f, 170.0f));
    }

    public float CyclopsVerticalFieldOfView
    {
        get => _cyclopsVerticalFieldOfView;
        set => SetField(ref _cyclopsVerticalFieldOfView, Math.Clamp(value, 1.0f, 170.0f));
    }

    public float AspectRatio
    {
        get => _aspectRatio;
        set => SetField(ref _aspectRatio, MathF.Max(0.01f, value));
    }

    public float NearPlane
    {
        get => _nearPlane;
        set => SetField(ref _nearPlane, MathF.Max(0.01f, value));
    }

    public float FarPlane
    {
        get => _farPlane;
        set => SetField(ref _farPlane, MathF.Max(NearPlane + 0.01f, value));
    }

    public float YawAmplitudeScale
    {
        get => _yawAmplitudeScale;
        set => SetField(ref _yawAmplitudeScale, MathF.Max(0.0f, value));
    }

    public float PitchAmplitudeScale
    {
        get => _pitchAmplitudeScale;
        set => SetField(ref _pitchAmplitudeScale, MathF.Max(0.0f, value));
    }

    public Vector3 RigOffset
    {
        get => _rigOffset;
        set => SetField(ref _rigOffset, value);
    }

    public bool ShowLeftFrustum
    {
        get => _showLeftFrustum;
        set => SetField(ref _showLeftFrustum, value);
    }

    public bool ShowRightFrustum
    {
        get => _showRightFrustum;
        set => SetField(ref _showRightFrustum, value);
    }

    public bool ShowCyclopsFrustum
    {
        get => _showCyclopsFrustum;
        set => SetField(ref _showCyclopsFrustum, value);
    }

    public bool ShowCombinedFrustum
    {
        get => _showCombinedFrustum;
        set => SetField(ref _showCombinedFrustum, value);
    }

    public bool ShowEyeMarkers
    {
        get => _showEyeMarkers;
        set => SetField(ref _showEyeMarkers, value);
    }

    public bool PreferStereoFarDistance
    {
        get => _preferStereoFarDistance;
        set => SetField(ref _preferStereoFarDistance, value);
    }

    public bool SolveCombinedViewOrientation
    {
        get => _solveCombinedViewOrientation;
        set => SetField(ref _solveCombinedViewOrientation, value);
    }

    public bool RefineCombinedViewOrientation
    {
        get => _refineCombinedViewOrientation;
        set => SetField(ref _refineCombinedViewOrientation, value);
    }

    public bool HighSpeedMode
    {
        get => _highSpeedMode;
        set => SetField(ref _highSpeedMode, value);
    }

    public bool ShowCombinedViewBasis
    {
        get => _showCombinedViewBasis;
        set => SetField(ref _showCombinedViewBasis, value);
    }

    public float CombinedViewBasisLength
    {
        get => _combinedViewBasisLength;
        set => SetField(ref _combinedViewBasisLength, MathF.Max(0.1f, value));
    }

    [Browsable(false)]
    public string LastCombinedViewCandidateLabel
    {
        get => _lastCombinedViewCandidateLabel;
        private set => SetField(ref _lastCombinedViewCandidateLabel, value);
    }

    [Browsable(false)]
    public float LastCombinedViewCost
    {
        get => _lastCombinedViewCost;
        private set => SetField(ref _lastCombinedViewCost, value);
    }

    public ColorF4 LeftFrustumColor
    {
        get => _leftFrustumColor;
        set => SetField(ref _leftFrustumColor, value);
    }

    public ColorF4 RightFrustumColor
    {
        get => _rightFrustumColor;
        set => SetField(ref _rightFrustumColor, value);
    }

    public ColorF4 CyclopsFrustumColor
    {
        get => _cyclopsFrustumColor;
        set => SetField(ref _cyclopsFrustumColor, value);
    }

    public ColorF4 CombinedFrustumColor
    {
        get => _combinedFrustumColor;
        set => SetField(ref _combinedFrustumColor, value);
    }

    [Browsable(false)]
    public float OrbitYawNormalized
    {
        get => _orbitYawNormalized;
        set => SetField(ref _orbitYawNormalized, Math.Clamp(value, -1.0f, 1.0f));
    }

    [Browsable(false)]
    public float OrbitPitchNormalized
    {
        get => _orbitPitchNormalized;
        set => SetField(ref _orbitPitchNormalized, Math.Clamp(value, -1.0f, 1.0f));
    }

    public void Configure(
        CameraComponent leftEyeCamera,
        CameraComponent rightEyeCamera,
        CameraComponent cyclopsEyeCamera,
        DebugDrawComponent debugDraw,
        Transform cyclopsOrbitTransform)
    {
        LeftEyeCamera = leftEyeCamera;
        RightEyeCamera = rightEyeCamera;
        CyclopsEyeCamera = cyclopsEyeCamera;
        DebugDraw = debugDraw;
        CyclopsOrbitTransform = cyclopsOrbitTransform;
        ApplyCurrentState();
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateRig);
        ApplyCurrentState();
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateRig);
        base.OnComponentDeactivated();
    }

    private void UpdateRig()
        => ApplyCurrentState();

    private void ApplyCurrentState()
    {
        ApplyCyclopsRotation();
        ApplyEyeOffsets();
        ApplyCameraParameters();
        UpdateDebugFrusta();
    }

    private void ApplyCyclopsRotation()
    {
        if (CyclopsOrbitTransform is not Transform transform)
            return;

        float yawRadians = XRMath.DegToRad(RotationAngleDegrees * YawAmplitudeScale * OrbitYawNormalized);
        float pitchRadians = XRMath.DegToRad(RotationAngleDegrees * PitchAmplitudeScale * OrbitPitchNormalized);
        transform.Translation = RigOffset;
        transform.Rotation =
            Quaternion.CreateFromAxisAngle(Globals.Up, yawRadians) *
            Quaternion.CreateFromAxisAngle(Globals.Right, pitchRadians);
    }

    private void ApplyEyeOffsets()
    {
        float halfSeparation = EyeSeparationDistance * 0.5f;

        if (LeftEyeCamera?.DefaultTransform is Transform leftTransform)
            leftTransform.Translation = new Vector3(-halfSeparation, 0.0f, 0.0f);

        if (RightEyeCamera?.DefaultTransform is Transform rightTransform)
            rightTransform.Translation = new Vector3(halfSeparation, 0.0f, 0.0f);

        if (CyclopsEyeCamera?.DefaultTransform is Transform cyclopsTransform)
            cyclopsTransform.Translation = Vector3.Zero;
    }

    private void ApplyCameraParameters()
    {
        LeftEyeCamera?.SetPerspective(VerticalFieldOfView, NearPlane, FarPlane, AspectRatio);
        RightEyeCamera?.SetPerspective(VerticalFieldOfView, NearPlane, FarPlane, AspectRatio);
        CyclopsEyeCamera?.SetPerspective(CyclopsVerticalFieldOfView, NearPlane, FarPlane, AspectRatio);
    }

    private void UpdateDebugFrusta()
    {
        if (LeftEyeCamera is null || RightEyeCamera is null || CyclopsEyeCamera is null || DebugDraw is null)
            return;

        Matrix4x4 leftProjection = LeftEyeCamera.Camera.ProjectionMatrixUnjittered;
        Matrix4x4 rightProjection = RightEyeCamera.Camera.ProjectionMatrixUnjittered;
        Matrix4x4 cyclopsProjection = CyclopsEyeCamera.Camera.ProjectionMatrixUnjittered;

        // Compute view matrices relative to the rig root.
        Matrix4x4 referenceWorld = DefaultTransform?.WorldMatrix ?? DebugDraw.Transform.WorldMatrix;
        Matrix4x4 leftInvWorld = LeftEyeCamera.Transform.InverseWorldMatrix;
        Matrix4x4 rightInvWorld = RightEyeCamera.Transform.InverseWorldMatrix;
        Matrix4x4 cyclopsInvWorld = CyclopsEyeCamera.Transform.InverseWorldMatrix;
        Matrix4x4 leftViewInReference = referenceWorld * leftInvWorld;
        Matrix4x4 rightViewInReference = referenceWorld * rightInvWorld;
        Matrix4x4 cyclopsViewInReference = referenceWorld * cyclopsInvWorld;

        Matrix4x4[] projections = [leftProjection, rightProjection, cyclopsProjection];
        Matrix4x4[] referenceViews = [leftViewInReference, rightViewInReference, cyclopsViewInReference];
        int? farBoundsSourceCount = PreferStereoFarDistance ? 2 : null;

        ProjectionMatrixCombiner.FrustumSolveResult combinedSolution = HighSpeedMode
            ? ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
                GetOrCreateCachedPointCloud(projections, referenceViews, farBoundsSourceCount),
                referenceViews,
                SolveCombinedViewOrientation,
                RefineCombinedViewOrientation,
                highSpeedMode: true)
            : ProjectionMatrixCombiner.SolveMinimalEnclosingFrustum(
                projections,
                referenceViews,
                farBoundsSourceCount,
                SolveCombinedViewOrientation,
                RefineCombinedViewOrientation,
                highSpeedMode: false);
        LastCombinedViewCandidateLabel = combinedSolution.CandidateLabel;
        LastCombinedViewCost = combinedSolution.Cost;

        // Compute view matrices in DebugDraw's local space for visualization.
        // DebugDrawComponent renders coordinates in its node's local space,
        // so all frustum corners must be expressed relative to the draw node.
        Matrix4x4 drawWorld = DebugDraw.Transform.WorldMatrix;
        Matrix4x4 leftViewInDraw = drawWorld * leftInvWorld;
        Matrix4x4 rightViewInDraw = drawWorld * rightInvWorld;
        Matrix4x4 cyclopsViewInDraw = drawWorld * cyclopsInvWorld;
        Matrix4x4 referenceViewInDraw = Matrix4x4.Identity;
        if (Matrix4x4.Invert(referenceWorld, out Matrix4x4 referenceInvWorld))
            referenceViewInDraw = drawWorld * referenceInvWorld;

        Frustum leftFrustum = CreateFrustum(leftViewInDraw, leftProjection);
        Frustum rightFrustum = CreateFrustum(rightViewInDraw, rightProjection);
        Frustum cyclopsFrustum = CreateFrustum(cyclopsViewInDraw, cyclopsProjection);
        Frustum combinedFrustum = CreateFrustum(referenceViewInDraw * combinedSolution.View, combinedSolution.Projection);

        DebugDraw.ClearShapes();
        if (ShowLeftFrustum)
            DrawFrustum(DebugDraw, leftFrustum, LeftFrustumColor);
        if (ShowRightFrustum)
            DrawFrustum(DebugDraw, rightFrustum, RightFrustumColor);
        if (ShowCyclopsFrustum)
            DrawFrustum(DebugDraw, cyclopsFrustum, CyclopsFrustumColor);
        if (ShowCombinedFrustum)
            DrawFrustum(DebugDraw, combinedFrustum, CombinedFrustumColor);
        if (ShowCombinedViewBasis)
            DrawCombinedViewBasis(DebugDraw, referenceViewInDraw, combinedSolution);
        if (ShowEyeMarkers)
            DrawEyeMarkers(DebugDraw);
    }

    private void DrawCombinedViewBasis(DebugDrawComponent debugDraw, Matrix4x4 referenceViewInDraw, ProjectionMatrixCombiner.FrustumSolveResult combinedSolution)
    {
        if (!Matrix4x4.Invert(referenceViewInDraw, out Matrix4x4 referenceToDraw))
            referenceToDraw = Matrix4x4.Identity;

        Matrix4x4 combinedTransformInReference = Matrix4x4.CreateFromQuaternion(combinedSolution.Orientation);
        Vector3 origin = Vector3.Transform(Vector3.Zero, referenceToDraw);
        Vector3 right = Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(Globals.Right, combinedTransformInReference), referenceToDraw));
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(Globals.Up, combinedTransformInReference), referenceToDraw));
        Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.TransformNormal(Globals.Forward, combinedTransformInReference), referenceToDraw));

        debugDraw.AddLine(origin, origin + right * CombinedViewBasisLength, ColorF4.Red);
        debugDraw.AddLine(origin, origin + up * CombinedViewBasisLength, ColorF4.LightGreen);
        debugDraw.AddLine(origin, origin + forward * CombinedViewBasisLength, ColorF4.LightBlue);
    }

    private void DrawEyeMarkers(DebugDrawComponent debugDraw)
    {
        const float markerLength = 1.5f;
        Matrix4x4 drawInvWorld = debugDraw.Transform.InverseWorldMatrix;

        DrawMarker(LeftEyeCamera!, LeftFrustumColor);
        DrawMarker(CyclopsEyeCamera!, CyclopsFrustumColor);
        DrawMarker(RightEyeCamera!, RightFrustumColor);

        void DrawMarker(CameraComponent camera, ColorF4 color)
        {
            Matrix4x4 camWorld = camera.Transform.WorldMatrix;
            Vector3 origin = Vector3.Transform(camWorld.Translation, drawInvWorld);
            Vector3 worldForward = Vector3.TransformNormal(Globals.Forward, camWorld);
            Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(worldForward, drawInvWorld));
            debugDraw.AddPoint(origin, color);
            debugDraw.AddLine(origin, origin + forward * markerLength, color);
        }
    }

    private static Frustum CreateFrustum(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix)
    {
        if (!Matrix4x4.Invert(viewMatrix * projectionMatrix, out Matrix4x4 inverseViewProjection))
            return new Frustum();

        return new Frustum(inverseViewProjection);
    }

    private static void DrawFrustum(DebugDrawComponent debug, Frustum frustum, ColorF4 color)
    {
        var corners = frustum.Corners;

        AddEdge(0, 1); AddEdge(1, 3); AddEdge(3, 2); AddEdge(2, 0);
        AddEdge(4, 5); AddEdge(5, 7); AddEdge(7, 6); AddEdge(6, 4);
        AddEdge(0, 4); AddEdge(1, 5); AddEdge(2, 6); AddEdge(3, 7);

        void AddEdge(int a, int b)
            => debug.AddLine(corners[a], corners[b], color);
    }

    private ProjectionMatrixCombiner.FrustumPointCloud GetOrCreateCachedPointCloud(
        Matrix4x4[] projections,
        Matrix4x4[] views,
        int? farBoundsSourceCount)
    {
        int normalizedFarSourceCount = farBoundsSourceCount ?? projections.Length;
        if (!_hasCachedPointCloud ||
            _cachedPointCloudFarSourceCount != normalizedFarSourceCount ||
            !MatrixArraysEqual(_cachedPointCloudProjections, projections) ||
            !MatrixArraysEqual(_cachedPointCloudViews, views))
        {
            _cachedPointCloud = ProjectionMatrixCombiner.BuildFrustumPointCloud(projections, views, farBoundsSourceCount);
            _cachedPointCloudProjections = [.. projections];
            _cachedPointCloudViews = [.. views];
            _cachedPointCloudFarSourceCount = normalizedFarSourceCount;
            _hasCachedPointCloud = true;
        }

        return _cachedPointCloud;
    }

    private static bool MatrixArraysEqual(Matrix4x4[]? left, Matrix4x4[] right)
    {
        if (left is null || left.Length != right.Length)
            return false;

        for (int i = 0; i < right.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }
}