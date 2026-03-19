using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static XRWorld CreateMathIntersectionsWorld(bool setUI, bool isServer)
    {
        ApplyRenderSettingsFromToggles();

        var scene = new XRScene("Math Intersections Scene");
        var rootNode = new SceneNode("Root Node");
        scene.RootNodes.Add(rootNode);

        Pawns.CreatePlayerPawn(setUI, isServer, rootNode);
        if (Toggles.DirLight)
            Lighting.AddDirLight(rootNode);

        AddGroundCrosshair(rootNode);
        AddProjectionMatrixCombinerRig(rootNode);
        AddFrustumIntersectionRig(rootNode);
        AddFrustumContainmentRig(rootNode);
        AddRaySphereRig(rootNode);
        AddSegmentAabbRig(rootNode);
        AddRayTriangleRig(rootNode);

        var world = new XRWorld("Math Intersections World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static void AddGroundCrosshair(SceneNode rootNode)
    {
        var gridNode = rootNode.NewChild("ReferenceGrid");
        var debug = gridNode.AddComponent<DebugDrawComponent>()!;
        const float extent = 25.0f;
        const float step = 5.0f;
        for (float x = -extent; x <= extent; x += step)
        {
            debug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
        }
        for (float z = -extent; z <= extent; z += step)
        {
            debug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
        }
        debug.AddLine(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(0.0f, 4.0f, 0.0f), ColorF4.LightGold);
    }

    private static void AddFrustumIntersectionRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("FrustumIntersectionRig");

        var frustumANode = rigNode.NewChild("FrustumA");
        var frustumATfm = frustumANode.SetTransform<Transform>();
        frustumATfm.Translation = new Vector3(-2.5f, 2.0f, 7.5f);
        frustumATfm.Rotation = Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(-10.0f));
        var frustumADebug = frustumANode.AddComponent<DebugDrawComponent>()!;

        var frustumBNode = rigNode.NewChild("FrustumB");
        var frustumBTfm = frustumBNode.SetTransform<Transform>();
        frustumBTfm.Translation = new Vector3(3.5f, 2.5f, 5.0f);
        var frustumBDebug = frustumBNode.AddComponent<DebugDrawComponent>()!;

        var intersectionDebug = rigNode.NewChild("FrustumIntersection").AddComponent<DebugDrawComponent>()!;

        const float aspect = 16.0f / 9.0f;
        const float nearZ = 0.3f;
        const float farZ = 18.0f;
        const float frustumAFov = 70.0f;
        const float frustumBFov = 60.0f;

        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            frustumBTfm.Rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(t * 25.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(MathF.Sin(t * 0.7f) * 18.0f));
            frustumBTfm.Translation = new Vector3(
                3.5f + MathF.Sin(t * 0.8f) * 1.6f,
                2.5f + MathF.Sin(t * 0.6f) * 0.6f,
                5.0f + MathF.Cos(t * 0.5f) * 1.2f);

            var frustumA = BuildFrustum(frustumATfm, frustumAFov, aspect, nearZ, farZ);
            var frustumB = BuildFrustum(frustumBTfm, frustumBFov, aspect, nearZ, farZ);

            DrawFrustum(frustumADebug, frustumA, ColorF4.LightBlue);
            DrawFrustum(frustumBDebug, frustumB, ColorF4.Orange);

            DrawFrustumIntersection(intersectionDebug, frustumA, frustumB);
        });
    }

    private static void AddProjectionMatrixCombinerRig(SceneNode rootNode)
    {
        var rigRootNode = rootNode.NewChild("ProjectionMatrixCombinerRig");
        var rigRootTransform = rigRootNode.SetTransform<Transform>();
        rigRootTransform.Translation = new Vector3(-10.0f, 2.5f, -10.0f);

        var cyclopsEyeNode = rigRootNode.NewChild("CyclopsEye");
        var cyclopsEyeTransform = cyclopsEyeNode.SetTransform<Transform>();
        cyclopsEyeTransform.Translation = Vector3.Zero;

        var leftEyeNode = rigRootNode.NewChild("LeftEye");
        leftEyeNode.SetTransform<Transform>();
        var rightEyeNode = rigRootNode.NewChild("RightEye");
        rightEyeNode.SetTransform<Transform>();

        var debugDraw = rigRootNode.AddComponent<DebugDrawComponent>()!;

        var cyclopsEyeCamera = cyclopsEyeNode.AddComponent<CameraComponent>()!;
        cyclopsEyeCamera.Name = "CyclopsEyeCamera";
        var leftEyeCamera = leftEyeNode.AddComponent<CameraComponent>()!;
        leftEyeCamera.Name = "LeftEyeCamera";
        var rightEyeCamera = rightEyeNode.AddComponent<CameraComponent>()!;
        rightEyeCamera.Name = "RightEyeCamera";

        var combinerComponent = rigRootNode.AddComponent<ProjectionMatrixCombinerDebugComponent>()!;
        combinerComponent.Name = "ProjectionMatrixCombiner";
        combinerComponent.Configure(leftEyeCamera, rightEyeCamera, cyclopsEyeCamera, debugDraw, cyclopsEyeTransform);
        combinerComponent.HighSpeedMode = true;

        var customUi = rigRootNode.AddComponent<CustomUIComponent>()!;
        customUi.Name = "CombinerControls";
        customUi.AddFloatField(
            "Rotation Angle (deg)",
            () => combinerComponent.RotationAngleDegrees,
            value => combinerComponent.RotationAngleDegrees = value,
            0.0f,
            60.0f,
            0.25f,
            "%.2f",
            "Angular radius used by the cyclops-eye orbit clip.");
        customUi.AddFloatField(
            "Eye Separation (m)",
            () => combinerComponent.EyeSeparationDistance,
            value => combinerComponent.EyeSeparationDistance = value,
            0.0f,
            0.5f,
            0.001f,
            "%.3f",
            "Distance between the left and right eye cameras.");

        var animationClipComponent = rigRootNode.AddComponent<AnimationClipComponent>()!;
        animationClipComponent.Name = "CyclopsOrbitClip";
        animationClipComponent.Animation = CreateCyclopsOrbitClip();
        animationClipComponent.StartOnActivate = true;

        customUi.AddFloatField(
            "Vertical FOV (deg)",
            () => combinerComponent.VerticalFieldOfView,
            value => combinerComponent.VerticalFieldOfView = value,
            20.0f,
            140.0f,
            0.25f,
            "%.2f",
            "Shared vertical field of view used by the left and right eye cameras.");
        customUi.AddFloatField(
            "Cyclops FOV (deg)",
            () => combinerComponent.CyclopsVerticalFieldOfView,
            value => combinerComponent.CyclopsVerticalFieldOfView = value,
            20.0f,
            140.0f,
            0.25f,
            "%.2f",
            "Independent vertical field of view for the cyclops camera only.");
        customUi.AddFloatField(
            "Aspect Ratio",
            () => combinerComponent.AspectRatio,
            value => combinerComponent.AspectRatio = value,
            0.25f,
            4.0f,
            0.01f,
            "%.3f",
            "Projection aspect ratio applied to all three eye cameras.");
        customUi.AddFloatField(
            "Near Plane (m)",
            () => combinerComponent.NearPlane,
            value => combinerComponent.NearPlane = value,
            0.01f,
            5.0f,
            0.01f,
            "%.3f",
            "Near clip plane shared by the test cameras.");
        customUi.AddFloatField(
            "Far Plane (m)",
            () => combinerComponent.FarPlane,
            value => combinerComponent.FarPlane = value,
            1.0f,
            200.0f,
            0.1f,
            "%.2f",
            "Far clip plane shared by the test cameras.");
        customUi.AddFloatField(
            "Orbit Speed",
            () => animationClipComponent.Speed,
            value => animationClipComponent.Speed = value,
            0.1f,
            4.0f,
            0.01f,
            "%.2f",
            "Playback speed multiplier for the cyclops-eye orbit animation clip.");
        customUi.AddVector3Field(
            "Rig Offset",
            () => combinerComponent.RigOffset,
            value => combinerComponent.RigOffset = value,
            0.05f,
            "%.3f",
            "Local offset applied to the animated cyclops rig transform.");
        customUi.AddFloatField(
            "Yaw Amplitude Scale",
            () => combinerComponent.YawAmplitudeScale,
            value => combinerComponent.YawAmplitudeScale = value,
            0.0f,
            2.0f,
            0.01f,
            "%.2f",
            "Ellipse scale multiplier for yaw motion around the orbit.");
        customUi.AddFloatField(
            "Pitch Amplitude Scale",
            () => combinerComponent.PitchAmplitudeScale,
            value => combinerComponent.PitchAmplitudeScale = value,
            0.0f,
            2.0f,
            0.01f,
            "%.2f",
            "Ellipse scale multiplier for pitch motion around the orbit.");
        customUi.AddBoolField(
            "Show Left Frustum",
            () => combinerComponent.ShowLeftFrustum,
            value => combinerComponent.ShowLeftFrustum = value,
            "Toggle left-eye frustum debug rendering.");
        customUi.AddBoolField(
            "Show Right Frustum",
            () => combinerComponent.ShowRightFrustum,
            value => combinerComponent.ShowRightFrustum = value,
            "Toggle right-eye frustum debug rendering.");
        customUi.AddBoolField(
            "Show Cyclops Frustum",
            () => combinerComponent.ShowCyclopsFrustum,
            value => combinerComponent.ShowCyclopsFrustum = value,
            "Toggle cyclops frustum debug rendering.");
        customUi.AddBoolField(
            "Show Combined Frustum",
            () => combinerComponent.ShowCombinedFrustum,
            value => combinerComponent.ShowCombinedFrustum = value,
            "Toggle the final combined-frustum debug rendering.");
        customUi.AddBoolField(
            "Show Eye Markers",
            () => combinerComponent.ShowEyeMarkers,
            value => combinerComponent.ShowEyeMarkers = value,
            "Toggle the eye-origin points and forward markers.");
        customUi.AddBoolField(
            "Prefer Stereo Far Distance",
            () => combinerComponent.PreferStereoFarDistance,
            value => combinerComponent.PreferStereoFarDistance = value,
            "When enabled, the combined frustum uses only the left and right eye frusta to determine its far distance.");
        customUi.AddBoolField(
            "Solve Combined View Orientation",
            () => combinerComponent.SolveCombinedViewOrientation,
            value => combinerComponent.SolveCombinedViewOrientation = value,
            "When enabled, the combiner also searches for a better shared view orientation before fitting the enclosing projection.");
        customUi.AddBoolField(
            "Refine Combined View Orientation",
            () => combinerComponent.RefineCombinedViewOrientation,
            value => combinerComponent.RefineCombinedViewOrientation = value,
            "Runs the local yaw/pitch/roll refinement pass after the initial combined-view orientation candidate search. Disable this to reduce solve cost.");
        customUi.AddBoolField(
            "High Speed Mode",
            () => combinerComponent.HighSpeedMode,
            value => combinerComponent.HighSpeedMode = value,
            "Bundles the fast path for this debug rig: skips refinement, reuses per-candidate transformed points, and caches source frustum point clouds while the inputs stay unchanged.");
        customUi.AddBoolField(
            "Show Combined View Basis",
            () => combinerComponent.ShowCombinedViewBasis,
            value => combinerComponent.ShowCombinedViewBasis = value,
            "Draw the solved combined-view basis axes at the rig origin for debugging.");
        customUi.AddFloatField(
            "Combined View Basis Length",
            () => combinerComponent.CombinedViewBasisLength,
            value => combinerComponent.CombinedViewBasisLength = value,
            0.1f,
            10.0f,
            0.05f,
            "%.2f",
            "Length of the debug axes for the solved combined-view basis.");
        customUi.AddTextField(
            "Combined View Candidate",
            () => combinerComponent.LastCombinedViewCandidateLabel,
            "Which orientation candidate won the combined-view solve.");
        customUi.AddTextField(
            "Combined View Cost",
            () => combinerComponent.LastCombinedViewCost.ToString("0.0000"),
            "Current enclosure cost for the solved combined view.");
        customUi.AddColorField(
            "Left Frustum Color",
            () => combinerComponent.LeftFrustumColor,
            value => combinerComponent.LeftFrustumColor = value,
            true,
            "Debug color for the left-eye frustum and marker.");
        customUi.AddColorField(
            "Right Frustum Color",
            () => combinerComponent.RightFrustumColor,
            value => combinerComponent.RightFrustumColor = value,
            true,
            "Debug color for the right-eye frustum and marker.");
        customUi.AddColorField(
            "Cyclops Frustum Color",
            () => combinerComponent.CyclopsFrustumColor,
            value => combinerComponent.CyclopsFrustumColor = value,
            true,
            "Debug color for the cyclops frustum and marker.");
        customUi.AddColorField(
            "Combined Frustum Color",
            () => combinerComponent.CombinedFrustumColor,
            value => combinerComponent.CombinedFrustumColor = value,
            true,
            "Debug color for the final combined frustum.");
    }

    private static void AddRaySphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("RaySphereRig");
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;

        Vector3 rayOrigin = new(-8.0f, 2.0f, -4.0f);
        Vector3 rayDirection = Vector3.Normalize(new Vector3(1.0f, 0.05f, 1.0f));
        const float rayLength = 24.0f;
        const float sphereRadius = 1.35f;

        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 sphereCenter = new(
                MathF.Sin(t * 0.8f) * 3.5f,
                1.5f + MathF.Sin(t * 0.6f) * 0.75f,
                6.0f + MathF.Cos(t * 0.9f) * 2.0f);

            bool hit = TryRaySphere(rayOrigin, rayDirection, sphereCenter, sphereRadius, out float hitDistance);
            Vector3 rayEnd = rayOrigin + rayDirection * rayLength;

            debug.ClearShapes();
            debug.AddLine(rayOrigin, rayEnd, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.DarkRed, false);
            debug.AddPoint(rayOrigin, ColorF4.LightGold);

            if (hit)
            {
                Vector3 hitPoint = rayOrigin + rayDirection * hitDistance;
                debug.AddPoint(hitPoint, ColorF4.Yellow);
                debug.AddLine(rayOrigin, hitPoint, ColorF4.LightGold);
            }
        });
    }

    private static void AddFrustumContainmentRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("FrustumContainmentRig");

        var frustumNode = rigNode.NewChild("CameraFrustum");
        var frustumTfm = frustumNode.SetTransform<Transform>();
        frustumTfm.Translation = new Vector3(0.0f, 2.0f, -6.0f);
        frustumTfm.Rotation = Quaternion.Identity;
        var frustumDebug = frustumNode.AddComponent<DebugDrawComponent>()!;

        var shapesNode = rigNode.NewChild("TestShapes");
        var shapesDebug = shapesNode.AddComponent<DebugDrawComponent>()!;

        const float aspect = 16.0f / 9.0f;
        const float nearZ = 0.35f;
        const float farZ = 16.0f;
        const float fov = 70.0f;

        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            frustumTfm.Rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(MathF.Sin(t * 0.35f) * 25.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(MathF.Sin(t * 0.25f) * 10.0f));

            var frustum = BuildFrustum(frustumTfm, fov, aspect, nearZ, farZ);
            DrawFrustum(frustumDebug, frustum, ColorF4.LightBlue);

            shapesDebug.ClearShapes();

            // Moving sphere
            var sphere = new Sphere(
                new Vector3(MathF.Sin(t * 0.8f) * 4.0f, 2.0f + MathF.Sin(t * 0.55f) * 0.8f, 4.0f + MathF.Cos(t * 0.7f) * 2.0f),
                0.9f);
            var sphereContain = frustum.ContainsSphere(sphere);
            shapesDebug.AddSphere(sphere.Radius, sphere.Center, ContainmentColor(sphereContain), false);

            // Moving AABB
            Vector3 boxCenter = new(MathF.Cos(t * 0.5f) * 3.0f, 1.25f + MathF.Sin(t * 0.4f) * 0.5f, 7.0f);
            Vector3 boxHalf = new(0.75f, 0.6f, 0.9f);
            var aabb = new AABB(boxCenter - boxHalf, boxCenter + boxHalf);
            var aabbContain = frustum.ContainsAABB(aabb);
            shapesDebug.AddBox(boxHalf, boxCenter, ContainmentColor(aabbContain), false);

            // Capsule (approximated as two spheres for containment visualization)
            Vector3 capA = new(-4.5f, 1.2f + MathF.Sin(t * 0.6f) * 0.8f, 6.5f);
            Vector3 capB = capA + new Vector3(0.0f, 2.3f, 0.0f);
            float capR = 0.5f;
            Vector3 capDir = capB - capA;
            float capLen = capDir.Length();
            Vector3 capUp = capLen > 1e-6f ? capDir / capLen : Globals.Up;
            var capsule = new Capsule((capA + capB) * 0.5f, capUp, capR, capLen * 0.5f);
            var capContain = frustum.ContainsCapsule(capsule);
            shapesDebug.AddCapsule(capR, capA, capB, ContainmentColor(capContain), false);
        });

        static ColorF4 ContainmentColor(EContainment containment)
            => containment switch
            {
                EContainment.Contains => ColorF4.LightGreen,
                EContainment.Intersects => ColorF4.Yellow,
                _ => ColorF4.DarkRed,
            };
    }

    private static void AddSegmentAabbRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("SegmentAabbRig");
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;

        Vector3 aabbCenter = new(8.0f, 1.5f, -2.0f);
        Vector3 aabbHalf = new(1.3f, 1.0f, 0.9f);
        Vector3 aabbMin = aabbCenter - aabbHalf;
        Vector3 aabbMax = aabbCenter + aabbHalf;

        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 segA = new(4.0f, 1.0f + MathF.Sin(t * 0.7f) * 1.2f, -6.0f);
            Vector3 segB = new(12.0f, 2.0f + MathF.Cos(t * 0.8f) * 1.0f, 2.0f);

            bool hit = GeoUtil.Intersect.SegmentWithAABB(segA, segB, aabbMin, aabbMax, out Vector3 pEnter, out Vector3 pExit);

            debug.ClearShapes();
            debug.AddBox(aabbHalf, aabbCenter, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            debug.AddLine(segA, segB, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(segA, ColorF4.LightGold);
            debug.AddPoint(segB, ColorF4.LightGold);

            if (hit)
            {
                debug.AddPoint(pEnter, ColorF4.Yellow);
                debug.AddPoint(pExit, ColorF4.Orange);
            }
        });
    }

    private static void AddRayTriangleRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("RayTriangleRig");
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;

        Vector3 a = new(-10.0f, 1.0f, 10.0f);
        Vector3 b = new(-6.0f, 3.0f, 12.0f);
        Vector3 c = new(-8.0f, 0.5f, 15.0f);

        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 origin = new(-14.0f, 2.0f + MathF.Sin(t * 0.65f) * 1.25f, 8.0f);
            Vector3 dir = Vector3.Normalize(new Vector3(1.0f, MathF.Sin(t * 0.35f) * 0.15f, 0.85f));
            const float length = 30.0f;

            bool hit = GeoUtil.Intersect.RayWithTriangle(origin, dir, a, b, c, out float dist);
            Vector3 end = origin + dir * length;

            debug.ClearShapes();
            debug.AddLine(a, b, ColorF4.White);
            debug.AddLine(b, c, ColorF4.White);
            debug.AddLine(c, a, ColorF4.White);
            debug.AddLine(origin, end, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(origin, ColorF4.LightGold);

            if (hit)
            {
                Vector3 hitPoint = origin + dir * dist;
                debug.AddPoint(hitPoint, ColorF4.Yellow);
                debug.AddLine(origin, hitPoint, ColorF4.LightGold);
            }
        });
    }

    private static Frustum BuildFrustum(TransformBase tfm, float fovY, float aspect, float nearZ, float farZ)
    {
        Vector3 forward = tfm.WorldForward;
        Vector3 up = tfm.WorldUp;
        Vector3 position = tfm.WorldTranslation;
        return new Frustum(fovY, aspect, nearZ, farZ, forward, up, position);
    }

    private static void DrawFrustum(DebugDrawComponent debug, Frustum frustum, ColorF4 color)
    {
        debug.ClearShapes();
        var c = frustum.Corners;

        AddEdge(0, 1); AddEdge(1, 3); AddEdge(3, 2); AddEdge(2, 0);
        AddEdge(4, 5); AddEdge(5, 7); AddEdge(7, 6); AddEdge(6, 4);
        AddEdge(0, 4); AddEdge(1, 5); AddEdge(2, 6); AddEdge(3, 7);

        void AddEdge(int a, int b) => debug.AddLine(c[a], c[b], color);
    }

    private static void DrawFrustumIntersection(DebugDrawComponent debug, Frustum a, Frustum b)
    {
        debug.ClearShapes();

        PreparedFrustum preparedA = PreparedFrustum.FromFrustum(a);
        PreparedFrustum preparedB = PreparedFrustum.FromFrustum(b);

        if (!PreparedFrustum.FrustumIntersection.TryIntersectFrustaAabb(preparedA, preparedB, out Vector3 min, out Vector3 max))
            return;

        Vector3 center = (min + max) * 0.5f;
        Vector3 halfExtents = (max - min) * 0.5f;
        debug.AddBox(halfExtents, center, ColorF4.LightGreen, false);
    }

    private static bool TryRaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float distance)
    {
        Vector3 oc = origin - center;
        float b = Vector3.Dot(oc, dir);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0.0f)
        {
            distance = 0.0f;
            return false;
        }

        float sqrt = MathF.Sqrt(discriminant);
        float t0 = -b - sqrt;
        float t1 = -b + sqrt;

        distance = t0 >= 0.0f ? t0 : t1;
        return distance >= 0.0f;
    }

    private static AnimationClip CreateCyclopsOrbitClip()
    {
        const float durationSeconds = 4.0f;
        const int sampleCount = 17;

        PropAnimFloat yawAnimation = new(durationSeconds, looped: true, useKeyframes: true);
        PropAnimFloat pitchAnimation = new(durationSeconds, looped: true, useKeyframes: true);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            float time = durationSeconds * t;
            float orbitRadians = MathF.Tau * t;
            yawAnimation.Keyframes.Add(new FloatKeyframe(time, MathF.Cos(orbitRadians), 0.0f, EVectorInterpType.Linear));
            pitchAnimation.Keyframes.Add(new FloatKeyframe(time, MathF.Sin(orbitRadians), 0.0f, EVectorInterpType.Linear));
        }

        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        var sceneNodeMember = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var controllerMember = new AnimationMember("GetComponent", EAnimationMemberType.Method)
        {
            MethodArguments = [nameof(ProjectionMatrixCombinerDebugComponent)],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
            Children =
            [
                new AnimationMember(nameof(ProjectionMatrixCombinerDebugComponent.OrbitYawNormalized), EAnimationMemberType.Property, yawAnimation),
                new AnimationMember(nameof(ProjectionMatrixCombinerDebugComponent.OrbitPitchNormalized), EAnimationMemberType.Property, pitchAnimation),
            ]
        };

        root.Children.Add(sceneNodeMember);
        sceneNodeMember.Children.Add(controllerMember);

        return new AnimationClip(root)
        {
            Name = "CyclopsEyeOrbit",
            LengthInSeconds = durationSeconds,
            Looped = true,
            SampleRate = sampleCount - 1,
        };
    }
}
