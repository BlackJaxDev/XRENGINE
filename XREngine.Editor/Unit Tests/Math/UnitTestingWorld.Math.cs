using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using GeometryPlaneIntersection = XREngine.Data.Geometry.EPlaneIntersection;
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

        var testLayoutController = rootNode.AddComponent<MathIntersectionsWorldControllerComponent>()!;
        testLayoutController.Name = "Math Intersections Test Layout";
        var rootCustomUi = rootNode.AddComponent<CustomUIComponent>()!;
        rootCustomUi.Name = "Math Intersections Test Controls";

        AddGroundCrosshair(rootNode);
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddProjectionMatrixCombinerRig(rootNode, testLayoutController),
            "Projection Matrix Combiner Test",
            "Fits a shared cyclops projection that encloses the animated left and right eye frusta.",
            AABB.FromCenterSize(new Vector3(0.0f, 4.0f, 9.0f), new Vector3(36.0f, 12.0f, 24.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddFrustumIntersectionRig(rootNode, testLayoutController),
            "Frustum Intersection Test",
            "Two moving frusta sweep through each other while the green box shows the overlap AABB.",
            AABB.FromCenterSize(new Vector3(0.5f, 4.0f, 7.0f), new Vector3(24.0f, 10.0f, 24.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddFrustumContainmentRig(rootNode, testLayoutController),
            "Frustum Containment Test",
            "A moving sphere, AABB, and capsule change color as the frustum contains, intersects, or rejects them.",
            AABB.FromCenterSize(new Vector3(0.0f, 3.0f, 4.0f), new Vector3(18.0f, 8.0f, 24.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddRaySphereRig(rootNode),
            "Ray vs Sphere Test",
            "A fixed ray probes a moving sphere; green means hit and yellow marks the first intersection.",
            AABB.FromCenterSize(new Vector3(0.5f, 2.5f, 4.5f), new Vector3(20.0f, 6.0f, 20.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddSegmentAabbRig(rootNode),
            "Segment vs AABB Test",
            "A moving segment clips against an AABB and exposes the entry and exit points.",
            AABB.FromCenterSize(new Vector3(8.0f, 2.0f, -2.0f), new Vector3(10.0f, 5.0f, 10.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddRayTriangleRig(rootNode),
            "Ray vs Triangle Test",
            "An animated ray probes a triangle and highlights the barycentric hit point when it intersects.",
            AABB.FromCenterSize(new Vector3(-2.0f, 2.5f, 20.5f), new Vector3(26.0f, 6.0f, 26.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddGeoUtilPrimitiveIntersectionRig(rootNode, testLayoutController),
            "GeoUtil Primitive Intersection Test",
            "Each cell exercises a primitive GeoUtil overlap query and flips between hit and miss states.",
            AABB.FromCenterSize(new Vector3(0.0f, 2.5f, 0.0f), new Vector3(48.0f, 7.0f, 24.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddGeoUtilPlaneIntersectionRig(rootNode, testLayoutController),
            "GeoUtil Plane Intersection Test",
            "Each cell visualizes plane-side classification, plane intersections, and between-plane checks.",
            AABB.FromCenterSize(new Vector3(0.0f, 2.5f, 0.0f), new Vector3(40.0f, 7.0f, 24.0f)));
        RegisterMathIntersectionsTest(
            testLayoutController,
            AddPhysicsChainComparisonRig(rootNode, testLayoutController),
            "Physics Chain Comparison Test",
            "CPU and GPU physics-chain solvers run side by side so their motion can be compared directly.",
            AABB.FromCenterSize(new Vector3(0.0f, 3.0f, 2.0f), new Vector3(12.0f, 8.0f, 8.0f)));

        var world = new XRWorld("Math Intersections World", scene);
        Undo.TrackWorld(world);
        return world;
    }

    private static void RegisterMathIntersectionsTest(
        MathIntersectionsWorldControllerComponent controller,
        SceneNode testRootNode,
        string displayName,
        string description,
        AABB bounds)
    {
        testRootNode.Name = displayName;
        testRootNode.IsActiveSelf = false;
        controller.RegisterTest(testRootNode, displayName, description, bounds);
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

    private static SceneNode AddFrustumIntersectionRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigNode = rootNode.NewChild("Frustum Intersection Test");

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

        controller.RegisterSubLabel(rigNode, frustumATfm, "Frustum A", 1.5f);
        controller.RegisterSubLabel(rigNode, frustumBTfm, "Frustum B", 1.5f);

        return rigNode;
    }

    private static SceneNode AddProjectionMatrixCombinerRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigRootNode = rootNode.NewChild("Projection Matrix Combiner Test");
        rigRootNode.SetTransform<Transform>();

        var cyclopsEyeNode = rigRootNode.NewChild("CyclopsEye");
        var cyclopsEyeTransform = cyclopsEyeNode.SetTransform<Transform>();
        cyclopsEyeTransform.Translation = Vector3.Zero;

        var leftEyeNode = rigRootNode.NewChild("LeftEye");
        var leftEyeTransform = leftEyeNode.SetTransform<Transform>();
        var rightEyeNode = rigRootNode.NewChild("RightEye");
        var rightEyeTransform = rightEyeNode.SetTransform<Transform>();

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

        controller.RegisterSubLabel(rigRootNode, leftEyeTransform, "Left Eye", 1.0f);
        controller.RegisterSubLabel(rigRootNode, rightEyeTransform, "Right Eye", 1.0f);
        controller.RegisterSubLabel(rigRootNode, cyclopsEyeTransform, "Cyclops Eye", 1.0f);

        return rigRootNode;
    }

    private static SceneNode AddRaySphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("Ray vs Sphere Test");
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

        return rigNode;
    }

    private static SceneNode AddFrustumContainmentRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigNode = rootNode.NewChild("Frustum Containment Test");

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

        controller.RegisterSubLabel(rigNode, frustumTfm, "Camera", 1.0f);

        return rigNode;
    }

    private static SceneNode AddSegmentAabbRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("Segment vs AABB Test");
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

        return rigNode;
    }

    private static SceneNode AddRayTriangleRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("Ray vs Triangle Test");
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

        return rigNode;
    }

    private static SceneNode AddGeoUtilPrimitiveIntersectionRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigNode = rootNode.NewChild("GeoUtil Primitive Intersection Test");
        rigNode.SetTransform<Transform>();

        SceneNode rayPointNode = CreateMathTestCell(rigNode, "RayWithPoint", new Vector3(-16.0f, 0.0f, -6.0f));
        var rayPointDebug = rayPointNode.AddComponent<DebugDrawComponent>()!;
        rayPointNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 origin = new(-2.5f, 1.1f, -2.0f);
            Vector3 direction = Vector3.Normalize(new Vector3(1.0f, 0.18f, 1.0f));
            Vector3 perpendicular = Vector3.Normalize(Vector3.Cross(direction, Globals.Up));
            if (perpendicular.LengthSquared() <= 1e-6f)
                perpendicular = Globals.Right;

            float travel = 3.0f + MathF.Sin(t * 0.45f) * 0.8f;
            bool aligned = MathF.Sin(t * 0.9f) >= 0.0f;
            Vector3 point = origin + direction * travel + (aligned ? Vector3.Zero : perpendicular * 0.5f);
            bool hit = GeoUtil.Intersect.RayWithPoint(new Ray(origin, direction), point);

            rayPointDebug.ClearShapes();
            rayPointDebug.AddLine(origin, origin + direction * 6.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            rayPointDebug.AddPoint(origin, ColorF4.LightGold);
            rayPointDebug.AddPoint(point, hit ? ColorF4.Yellow : ColorF4.DarkRed);
        });

        SceneNode rayRayNode = CreateMathTestCell(rigNode, "RayWithRay", new Vector3(-8.0f, 0.0f, -6.0f));
        var rayRayDebug = rayRayNode.AddComponent<DebugDrawComponent>()!;
        rayRayNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Ray ray1 = new(new Vector3(-2.5f, 1.0f, 0.0f), Vector3.UnitX);
            bool aligned = MathF.Sin(t * 0.8f) >= 0.0f;
            Ray ray2 = new(new Vector3(0.0f, aligned ? 1.0f : 1.75f, -2.5f), Vector3.UnitZ);
            bool hit = GeoUtil.Intersect.RayWithRay(ray1, ray2, out Vector3 intersection);

            rayRayDebug.ClearShapes();
            rayRayDebug.AddLine(ray1.StartPoint, ray1.StartPoint + ray1.Direction * 5.0f, ColorF4.LightBlue);
            rayRayDebug.AddLine(ray2.StartPoint, ray2.StartPoint + ray2.Direction * 5.0f, ColorF4.Orange);
            rayRayDebug.AddPoint(ray1.StartPoint, ColorF4.LightGold);
            rayRayDebug.AddPoint(ray2.StartPoint, ColorF4.LightGold);
            if (hit)
                rayRayDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        SceneNode rayPlaneNode = CreateMathTestCell(rigNode, "RayWithPlane", new Vector3(0.0f, 0.0f, -6.0f));
        var rayPlaneDebug = rayPlaneNode.AddComponent<DebugDrawComponent>()!;
        rayPlaneNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 planePoint = new(0.0f, 1.25f, 0.0f);
            Vector3 planeNormal = Vector3.UnitY;
            Vector3 origin = new(-2.5f, 0.4f, -2.0f);
            Vector3 direction = MathF.Sin(t * 0.75f) >= 0.0f
                ? Vector3.Normalize(new Vector3(1.0f, 0.55f, 1.0f))
                : Vector3.Normalize(new Vector3(1.0f, -0.1f, 1.0f));
            bool hit = GeoUtil.Intersect.RayWithPlane(origin, direction, planePoint, planeNormal, out Vector3 intersection);

            rayPlaneDebug.ClearShapes();
            DrawPlaneIndicator(rayPlaneDebug, planePoint, planeNormal, 2.0f, ColorF4.LightBlue);
            rayPlaneDebug.AddLine(origin, origin + direction * 6.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            rayPlaneDebug.AddPoint(origin, ColorF4.LightGold);
            if (hit)
                rayPlaneDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        SceneNode rayAabbNode = CreateMathTestCell(rigNode, "RayWithAABB", new Vector3(8.0f, 0.0f, -6.0f));
        var rayAabbDebug = rayAabbNode.AddComponent<DebugDrawComponent>()!;
        rayAabbNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.25f, 0.0f);
            Vector3 half = new(1.0f, 0.8f, 0.9f);
            Vector3 min = center - half;
            Vector3 max = center + half;
            Vector3 origin = new(-3.0f, 1.15f, -2.0f);
            Vector3 target = MathF.Sin(t * 0.7f) >= 0.0f ? center : center + new Vector3(0.0f, 2.2f, 0.0f);
            Vector3 direction = Vector3.Normalize(target - origin);
            bool hit = GeoUtil.Intersect.RayWithAABB(new Ray(origin, direction), min, max, out Vector3 intersection);

            rayAabbDebug.ClearShapes();
            rayAabbDebug.AddBox(half, center, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            rayAabbDebug.AddLine(origin, origin + direction * 7.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            rayAabbDebug.AddPoint(origin, ColorF4.LightGold);
            if (hit)
                rayAabbDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        SceneNode rayBoxNode = CreateMathTestCell(rigNode, "RayWithBox", new Vector3(16.0f, 0.0f, -6.0f));
        var rayBoxDebug = rayBoxNode.AddComponent<DebugDrawComponent>()!;
        var rayBoxVisualNode = rayBoxNode.NewChild("BoxVisual");
        var rayBoxVisualTransform = rayBoxVisualNode.SetTransform<Transform>();
        var rayBoxVisualDebug = rayBoxVisualNode.AddComponent<DebugDrawComponent>()!;
        rayBoxNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.2f, 0.0f);
            Vector3 half = new(1.1f, 0.7f, 0.8f);
            Quaternion rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(t * 35.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(18.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);

            Vector3 origin = new(-3.0f, 1.1f, -1.8f);
            Vector3 target = MathF.Sin(t * 0.65f) >= 0.0f ? center : center + new Vector3(0.0f, 2.0f, 1.8f);
            Vector3 direction = Vector3.Normalize(target - origin);
            bool hit = GeoUtil.Intersect.RayWithBox(origin, direction, half, inverseWorld, out Vector3 intersection);

            rayBoxVisualTransform.Translation = center;
            rayBoxVisualTransform.Rotation = rotation;

            rayBoxDebug.ClearShapes();
            rayBoxVisualDebug.ClearShapes();
            rayBoxDebug.AddLine(origin, origin + direction * 7.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            rayBoxDebug.AddPoint(origin, ColorF4.LightGold);
            rayBoxVisualDebug.AddBox(half, Vector3.Zero, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            if (hit)
                rayBoxDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        SceneNode aabbAabbNode = CreateMathTestCell(rigNode, "AABBWithAABB", new Vector3(-16.0f, 0.0f, 6.0f));
        var aabbAabbDebug = aabbAabbNode.AddComponent<DebugDrawComponent>()!;
        aabbAabbNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 box1Center = new(-0.8f, 1.0f, 0.0f);
            Vector3 box1Half = new(1.0f, 0.8f, 0.9f);
            Vector3 box2Center = new(1.6f + MathF.Sin(t * 0.8f) * 1.8f, 1.0f, 0.0f);
            Vector3 box2Half = new(0.9f, 0.75f, 0.8f);
            bool hit = GeoUtil.Intersect.AABBWithAABB(
                new AABB(box1Center - box1Half, box1Center + box1Half),
                new AABB(box2Center - box2Half, box2Center + box2Half));

            aabbAabbDebug.ClearShapes();
            aabbAabbDebug.AddBox(box1Half, box1Center, hit ? ColorF4.LightGreen : ColorF4.LightBlue, false);
            aabbAabbDebug.AddBox(box2Half, box2Center, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        SceneNode aabbSphereNode = CreateMathTestCell(rigNode, "AABBWithSphere", new Vector3(-8.0f, 0.0f, 6.0f));
        var aabbSphereDebug = aabbSphereNode.AddComponent<DebugDrawComponent>()!;
        aabbSphereNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.15f, 0.0f);
            Vector3 half = new(1.0f, 0.9f, 0.8f);
            Vector3 sphereCenter = new(1.6f + MathF.Sin(t * 0.9f) * 1.7f, 1.15f, 0.0f);
            const float sphereRadius = 0.7f;
            bool hit = GeoUtil.Intersect.AABBWithSphere(center - half, center + half, sphereCenter, sphereRadius);

            aabbSphereDebug.ClearShapes();
            aabbSphereDebug.AddBox(half, center, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            aabbSphereDebug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        SceneNode boxSphereNode = CreateMathTestCell(rigNode, "BoxWithSphere", new Vector3(0.0f, 0.0f, 6.0f));
        var boxSphereDebug = boxSphereNode.AddComponent<DebugDrawComponent>()!;
        var boxSphereVisualNode = boxSphereNode.NewChild("BoxVisual");
        var boxSphereVisualTransform = boxSphereVisualNode.SetTransform<Transform>();
        var boxSphereVisualDebug = boxSphereVisualNode.AddComponent<DebugDrawComponent>()!;
        boxSphereNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.15f, 0.0f);
            Vector3 half = new(1.0f, 0.7f, 0.9f);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f)), XRMath.DegToRad(t * 30.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
            Vector3 sphereCenter = new(1.7f + MathF.Sin(t * 0.8f) * 1.6f, 1.15f, 0.0f);
            const float sphereRadius = 0.65f;
            bool hit = GeoUtil.Intersect.BoxWithSphere(half, inverseWorld, sphereCenter, sphereRadius);

            boxSphereVisualTransform.Translation = center;
            boxSphereVisualTransform.Rotation = rotation;

            boxSphereDebug.ClearShapes();
            boxSphereVisualDebug.ClearShapes();
            boxSphereVisualDebug.AddBox(half, Vector3.Zero, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            boxSphereDebug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        SceneNode sphereTriangleNode = CreateMathTestCell(rigNode, "SphereWithTriangle", new Vector3(8.0f, 0.0f, 6.0f));
        var sphereTriangleDebug = sphereTriangleNode.AddComponent<DebugDrawComponent>()!;
        sphereTriangleNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 a = new(-1.5f, 0.6f, -0.8f);
            Vector3 b = new(1.5f, 1.8f, -0.2f);
            Vector3 c = new(0.2f, 0.7f, 1.7f);
            Vector3 sphereCenter = new(0.0f, 1.0f + MathF.Sin(t * 0.85f) * 1.2f, 0.3f + MathF.Cos(t * 0.55f) * 0.9f);
            const float sphereRadius = 0.65f;
            bool hit = GeoUtil.Intersect.SphereWithTriangle(sphereCenter, sphereRadius, a, b, c);

            sphereTriangleDebug.ClearShapes();
            sphereTriangleDebug.AddLine(a, b, ColorF4.White);
            sphereTriangleDebug.AddLine(b, c, ColorF4.White);
            sphereTriangleDebug.AddLine(c, a, ColorF4.White);
            sphereTriangleDebug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        SceneNode sphereSphereNode = CreateMathTestCell(rigNode, "SphereWithSphere", new Vector3(16.0f, 0.0f, 6.0f));
        var sphereSphereDebug = sphereSphereNode.AddComponent<DebugDrawComponent>()!;
        sphereSphereNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 centerA = new(-0.9f, 1.15f, 0.0f);
            Vector3 centerB = new(1.4f + MathF.Sin(t * 0.95f) * 1.7f, 1.15f, 0.0f);
            const float radiusA = 0.85f;
            const float radiusB = 0.7f;
            bool hit = GeoUtil.Intersect.SphereWithSphere(centerA, radiusA, centerB, radiusB);

            sphereSphereDebug.ClearShapes();
            sphereSphereDebug.AddSphere(radiusA, centerA, hit ? ColorF4.LightGreen : ColorF4.LightBlue, false);
            sphereSphereDebug.AddSphere(radiusB, centerB, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        SceneNode segmentPlaneNode = CreateMathTestCell(rigNode, "SegmentWithPlane", new Vector3(24.0f, 0.0f, 6.0f));
        var segmentPlaneDebug = segmentPlaneNode.AddComponent<DebugDrawComponent>()!;
        segmentPlaneNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 planePoint = new(0.0f, 1.2f, 0.0f);
            Vector3 planeNormal = Vector3.UnitY;
            float planeDistance = -Vector3.Dot(planePoint, planeNormal);
            Vector3 start = new(-2.0f, 0.45f + MathF.Sin(t * 0.7f) * 1.3f, -0.8f);
            Vector3 end = new(2.0f, 2.15f + MathF.Cos(t * 0.9f) * 1.2f, 0.8f);
            bool hit = GeoUtil.Intersect.SegmentWithPlane(start, end, planeDistance, planeNormal, out Vector3 intersection);

            segmentPlaneDebug.ClearShapes();
            DrawPlaneIndicator(segmentPlaneDebug, planePoint, planeNormal, 1.8f, ColorF4.LightBlue);
            segmentPlaneDebug.AddLine(start, end, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            segmentPlaneDebug.AddPoint(start, ColorF4.LightGold);
            segmentPlaneDebug.AddPoint(end, ColorF4.LightGold);
            if (hit)
                segmentPlaneDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        void SubLabel(SceneNode node, string text) =>
            controller.RegisterSubLabel(rigNode, node.GetTransformAs<Transform>(true)!, text, 3.2f);

        SubLabel(rayPointNode, "Ray \u2192 Point");
        SubLabel(rayRayNode, "Ray \u2192 Ray");
        SubLabel(rayPlaneNode, "Ray \u2192 Plane");
        SubLabel(rayAabbNode, "Ray \u2192 AABB");
        SubLabel(rayBoxNode, "Ray \u2192 Box");
        SubLabel(aabbAabbNode, "AABB \u2192 AABB");
        SubLabel(aabbSphereNode, "AABB \u2192 Sphere");
        SubLabel(boxSphereNode, "Box \u2192 Sphere");
        SubLabel(sphereTriangleNode, "Sphere \u2192 Triangle");
        SubLabel(sphereSphereNode, "Sphere \u2192 Sphere");
        SubLabel(segmentPlaneNode, "Segment \u2192 Plane");

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneIntersectionRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane Intersection Test");
        rigNode.SetTransform<Transform>();

        SceneNode planePointNode = CreateMathTestCell(rigNode, "PlaneWithPoint", new Vector3(-12.0f, 0.0f, -6.0f));
        var planePointDebug = planePointNode.AddComponent<DebugDrawComponent>()!;
        planePointNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.1f);
            Vector3 planePoint = new(0.0f, 1.1f, 0.0f);
            Vector3 point = new(0.0f, 1.1f + MathF.Sin(t * 0.9f) * 1.7f, 0.0f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithPoint(plane, point);

            planePointDebug.ClearShapes();
            DrawPlaneIndicator(planePointDebug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            planePointDebug.AddPoint(point, PlaneIntersectionColor(intersection));
        });

        SceneNode planePlaneNode = CreateMathTestCell(rigNode, "PlaneWithPlane", new Vector3(-4.0f, 0.0f, -6.0f));
        var planePlaneDebug = planePlaneNode.AddComponent<DebugDrawComponent>()!;
        planePlaneNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane planeA = new(Vector3.UnitY, -1.0f);
            Plane planeB = MathF.Sin(t * 0.8f) >= 0.0f
                ? new Plane(Vector3.UnitZ, 0.0f)
                : new Plane(Vector3.UnitY, -2.0f);

            bool intersects = GeoUtil.Intersect.PlaneWithPlane(planeA, planeB);
            bool lineHit = GeoUtil.Intersect.PlaneWithPlane(planeA, planeB, out Ray line);

            planePlaneDebug.ClearShapes();
            DrawPlaneIndicator(planePlaneDebug, new Vector3(0.0f, 1.0f, 0.0f), planeA.Normal, 1.8f, ColorF4.LightBlue);
            DrawPlaneIndicator(planePlaneDebug, intersects ? Vector3.Zero : new Vector3(0.0f, 2.0f, 0.0f), planeB.Normal, 1.8f, ColorF4.Orange);
            if (lineHit)
                planePlaneDebug.AddLine(line.StartPoint - line.Direction * 2.5f, line.StartPoint + line.Direction * 2.5f, ColorF4.LightGreen);
        });

        SceneNode planeTriangleNode = CreateMathTestCell(rigNode, "PlaneWithTriangle", new Vector3(4.0f, 0.0f, -6.0f));
        var planeTriangleDebug = planeTriangleNode.AddComponent<DebugDrawComponent>()!;
        planeTriangleNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.1f);
            Vector3 planePoint = new(0.0f, 1.1f, 0.0f);
            float offset = MathF.Sin(t * 0.75f) * 1.4f;
            Vector3 a = new(-1.4f, 0.6f + offset, -0.9f);
            Vector3 b = new(1.3f, 1.6f + offset, -0.2f);
            Vector3 c = new(0.1f, 0.85f - offset, 1.4f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithTriangle(plane, a, b, c);

            planeTriangleDebug.ClearShapes();
            DrawPlaneIndicator(planeTriangleDebug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            ColorF4 triangleColor = PlaneIntersectionColor(intersection);
            planeTriangleDebug.AddLine(a, b, triangleColor);
            planeTriangleDebug.AddLine(b, c, triangleColor);
            planeTriangleDebug.AddLine(c, a, triangleColor);
        });

        SceneNode planeBoxNode = CreateMathTestCell(rigNode, "PlaneWithBox", new Vector3(12.0f, 0.0f, -6.0f));
        var planeBoxDebug = planeBoxNode.AddComponent<DebugDrawComponent>()!;
        var planeBoxVisualNode = planeBoxNode.NewChild("BoxVisual");
        var planeBoxVisualTransform = planeBoxVisualNode.SetTransform<Transform>();
        var planeBoxVisualDebug = planeBoxVisualNode.AddComponent<DebugDrawComponent>()!;
        planeBoxNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = Plane.Normalize(new Plane(Vector3.Normalize(new Vector3(0.0f, 1.0f, 0.25f)), -1.0f));
            Vector3 planePoint = -plane.Normal * plane.D;
            Vector3 center = new(0.0f, 1.25f + MathF.Sin(t * 0.7f) * 1.3f, 0.0f);
            Vector3 half = new(0.95f, 0.7f, 0.85f);
            Quaternion rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, XRMath.DegToRad(t * 28.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(20.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithBox(plane, -half, half, inverseWorld);

            planeBoxVisualTransform.Translation = center;
            planeBoxVisualTransform.Rotation = rotation;

            planeBoxDebug.ClearShapes();
            planeBoxVisualDebug.ClearShapes();
            DrawPlaneIndicator(planeBoxDebug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            planeBoxVisualDebug.AddBox(half, Vector3.Zero, PlaneIntersectionColor(intersection), false);
        });

        SceneNode planeSphereNode = CreateMathTestCell(rigNode, "PlaneWithSphere", new Vector3(-8.0f, 0.0f, 6.0f));
        var planeSphereDebug = planeSphereNode.AddComponent<DebugDrawComponent>()!;
        planeSphereNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.15f);
            Vector3 planePoint = new(0.0f, 1.15f, 0.0f);
            Sphere sphere = new(new Vector3(0.0f, 1.15f + MathF.Sin(t * 0.8f) * 1.7f, 0.0f), 0.7f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithSphere(plane, sphere);

            planeSphereDebug.ClearShapes();
            DrawPlaneIndicator(planeSphereDebug, planePoint, plane.Normal, 1.9f, ColorF4.LightBlue);
            planeSphereDebug.AddSphere(sphere.Radius, sphere.Center, PlaneIntersectionColor(intersection), false);
        });

        SceneNode pointBetweenPlanesNode = CreateMathTestCell(rigNode, "PointBetweenPlanes", new Vector3(0.0f, 0.0f, 6.0f));
        var pointBetweenPlanesDebug = pointBetweenPlanesNode.AddComponent<DebugDrawComponent>()!;
        pointBetweenPlanesNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            Plane planeA = new(Vector3.UnitX, 0.0f);
            Plane planeB = new(Vector3.UnitZ, 0.0f);
            Vector3 facingPoint = new(1.0f, 1.0f, 1.0f);
            Vector3 awayPoint = new(-1.0f, 1.0f, -1.0f);
            Vector3 straddlePoint = new(1.0f, 1.0f, -1.0f);

            bool facing = GeoUtil.Intersect.PointBetweenPlanes(facingPoint, planeA, planeB, EBetweenPlanes.NormalsFacing);
            bool away = GeoUtil.Intersect.PointBetweenPlanes(awayPoint, planeA, planeB, EBetweenPlanes.NormalsAway);
            bool straddles = GeoUtil.Intersect.PointBetweenPlanes(straddlePoint, planeA, planeB, EBetweenPlanes.DontCare);

            pointBetweenPlanesDebug.ClearShapes();
            DrawPlaneIndicator(pointBetweenPlanesDebug, Vector3.Zero, planeA.Normal, 1.8f, ColorF4.LightBlue);
            DrawPlaneIndicator(pointBetweenPlanesDebug, Vector3.Zero, planeB.Normal, 1.8f, ColorF4.Orange);
            pointBetweenPlanesDebug.AddPoint(facingPoint, facing ? ColorF4.LightGreen : ColorF4.DarkRed);
            pointBetweenPlanesDebug.AddPoint(awayPoint, away ? ColorF4.LightGreen : ColorF4.DarkRed);
            pointBetweenPlanesDebug.AddPoint(straddlePoint, straddles ? ColorF4.Yellow : ColorF4.DarkRed);
        });

        SceneNode threePlanesNode = CreateMathTestCell(rigNode, "ThreePlanes", new Vector3(8.0f, 0.0f, 6.0f));
        var threePlanesDebug = threePlanesNode.AddComponent<DebugDrawComponent>()!;
        threePlanesNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            Plane planeA = new(Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f)), 0.0f);
            Plane planeB = new(Vector3.Normalize(new Vector3(0.0f, 1.0f, 1.0f)), 0.0f);
            Plane planeC = new(Vector3.Normalize(new Vector3(1.0f, 0.0f, 1.0f)), 0.0f);
            Vector3 intersection = GeoUtil.Intersect.ThreePlanes(planeA, planeB, planeC);

            threePlanesDebug.ClearShapes();
            DrawPlaneIndicator(threePlanesDebug, Vector3.Zero, planeA.Normal, 1.5f, ColorF4.LightBlue);
            DrawPlaneIndicator(threePlanesDebug, Vector3.Zero, planeB.Normal, 1.5f, ColorF4.Orange);
            DrawPlaneIndicator(threePlanesDebug, Vector3.Zero, planeC.Normal, 1.5f, ColorF4.LightGreen);
            threePlanesDebug.AddPoint(intersection, ColorF4.Yellow);
        });

        void SubLabel(SceneNode node, string text) =>
            controller.RegisterSubLabel(rigNode, node.GetTransformAs<Transform>(true)!, text, 3.2f);

        SubLabel(planePointNode, "Plane \u2192 Point");
        SubLabel(planePlaneNode, "Plane \u2192 Plane");
        SubLabel(planeTriangleNode, "Plane \u2192 Triangle");
        SubLabel(planeBoxNode, "Plane \u2192 Box");
        SubLabel(planeSphereNode, "Plane \u2192 Sphere");
        SubLabel(pointBetweenPlanesNode, "Between Planes");
        SubLabel(threePlanesNode, "Three Planes");

        return rigNode;
    }

    private static SceneNode AddPhysicsChainComparisonRig(SceneNode rootNode, MathIntersectionsWorldControllerComponent controller)
    {
        var rigNode = rootNode.NewChild("Physics Chain Comparison Test");
        rigNode.SetTransform<Transform>();

        var cpuNode = AddPhysicsChainTest(rigNode, "CPUPhysicsChainTest", new Vector3(-2.75f, 0.0f, 0.0f), useGpu: false, phaseOffset: 0.0f, ColorF4.LightBlue, ColorF4.LightGold);
        var gpuNode = AddPhysicsChainTest(rigNode, "GPUPhysicsChainTest", new Vector3(2.75f, 0.0f, 0.0f), useGpu: true, phaseOffset: MathF.PI * 0.5f, ColorF4.LightGreen, ColorF4.Orange);

        controller.RegisterSubLabel(rigNode, cpuNode.GetTransformAs<Transform>(true)!, "CPU Solver", 5.5f);
        controller.RegisterSubLabel(rigNode, gpuNode.GetTransformAs<Transform>(true)!, "GPU Solver", 5.5f);

        return rigNode;
    }

    private static SceneNode AddPhysicsChainTest(SceneNode parentNode, string name, Vector3 position, bool useGpu, float phaseOffset, ColorF4 chainColor, ColorF4 rootColor)
    {
        var testNode = parentNode.NewChild(name);
        var testTransform = testNode.SetTransform<Transform>();
        testTransform.Translation = position;

        var rootNode = testNode.NewChild("Root");
        var rootTransform = rootNode.SetTransform<Transform>();
        Vector3 rootBaseTranslation = new(0.0f, 4.0f, 0.0f);
        rootTransform.Translation = rootBaseTranslation;

        var rootAnimation = rootNode.AddComponent<AnimationClipComponent>()!;
        rootAnimation.Name = $"{name}RootMotion";
        rootAnimation.Animation = CreateSineTranslationClip(rootBaseTranslation.Y, amplitude: 0.9f, frequency: 1.8f, phaseOffset);
        rootAnimation.StartOnActivate = true;

        const float segmentLength = 0.55f;
        Transform[] chainBones = CreatePhysicsChainBones(rootNode, segmentCount: 6, segmentLength);

        var sphereColliderNode = testNode.NewChild("SphereCollider");
        var sphereColliderTransform = sphereColliderNode.SetTransform<Transform>();
        sphereColliderTransform.Translation = new Vector3(0.2f, 3.45f, segmentLength * 2.75f);
        var sphereCollider = sphereColliderNode.AddComponent<PhysicsChainSphereCollider>()!;
        sphereCollider.Radius = 0.65f;

        var planeColliderNode = testNode.NewChild("PlaneCollider");
        var planeColliderTransform = planeColliderNode.SetTransform<Transform>();
        planeColliderTransform.Translation = new Vector3(0.0f, 0.65f, 0.0f);
        var planeCollider = planeColliderNode.AddComponent<PhysicsChainPlaneCollider>()!;

        PhysicsChainColliderBase[] colliders = [sphereCollider, planeCollider];
        var chain = rootNode.AddComponent<PhysicsChainComponent>()!;
        chain.Root = rootTransform;
        chain.UseGPU = useGpu;
        chain.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
        chain.UpdateRate = 60.0f;
        chain.Damping = 0.18f;
        chain.Elasticity = 0.12f;
        chain.Stiffness = 0.1f;
        chain.Inert = 0.25f;
        chain.Friction = 0.2f;
        chain.Radius = 0.08f;
        chain.Gravity = Vector3.Zero;
        chain.Force = Vector3.Zero;
        chain.BlendWeight = 1.0f;
        chain.Multithread = false;
        chain.UseBatchedDispatcher = useGpu;
        chain.Colliders = [.. colliders];

        var debug = testNode.AddComponent<DebugDrawComponent>()!;
        // Run in Late tick group AFTER PhysicsChainComponent.LateUpdate (Late+Animation)
        // so that bone transforms reflect the current frame's simulation results.
        testNode.RegisterTick(ETickGroup.Late, ETickOrder.Logic, () =>
        {
            debug.ClearShapes();

            rootTransform.RecalculateMatrixHierarchy(forceWorldRecalc: true, setRenderMatrixNow: true, childRecalcType: ELoopType.Parallel).Wait();
            testTransform.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

            // DebugDrawComponent positions are in local space of the owning node (testNode).
            // Convert world-space transform positions to testNode local-space to avoid doubling.
            Matrix4x4.Invert(testTransform.WorldMatrix, out var invWorld);
            Vector3 ToLocal(Vector3 world) => Vector3.Transform(world, invWorld);

            Vector3 localRoot = ToLocal(rootTransform.WorldTranslation);
            debug.AddLine(
                localRoot + new Vector3(0.0f, -0.9f, 0.0f),
                localRoot + new Vector3(0.0f, 0.9f, 0.0f),
                rootColor);
            debug.AddPoint(localRoot, rootColor);
            debug.AddSphere(sphereCollider.Radius, ToLocal(sphereColliderTransform.WorldTranslation), ColorF4.Magenta, false);
            debug.AddLine(
                ToLocal(planeColliderTransform.WorldTranslation) + new Vector3(-0.9f, 0.0f, 0.0f),
                ToLocal(planeColliderTransform.WorldTranslation) + new Vector3(0.9f, 0.0f, 0.0f),
                ColorF4.LightGray);
            debug.AddLine(
                localRoot,
                localRoot + new Vector3(0.0f, 0.0f, segmentLength * (chainBones.Length - 1)),
                ColorF4.DarkGray);

            for (int i = 1; i < chainBones.Length; i++)
            {
                Vector3 localBone = ToLocal(chainBones[i].WorldTranslation);
                Vector3 localPrev = ToLocal(chainBones[i - 1].WorldTranslation);
                debug.AddPoint(localBone, chainColor);
                debug.AddLine(localPrev, localBone, chainColor);
            }
        });

        return testNode;
    }

    private static Transform[] CreatePhysicsChainBones(SceneNode rootNode, int segmentCount, float segmentLength)
    {
        var bones = new Transform[segmentCount + 1];
        Transform parentTransform = rootNode.GetTransformAs<Transform>(true)!;
        bones[0] = parentTransform;

        SceneNode parentNode = rootNode;
        for (int i = 0; i < segmentCount; i++)
        {
            var boneNode = parentNode.NewChild($"Bone{i + 1}");
            var boneTransform = boneNode.SetTransform<Transform>();
            boneTransform.Translation = new Vector3(0.0f, 0.0f, segmentLength);
            bones[i + 1] = boneTransform;
            parentNode = boneNode;
        }

        return bones;
    }

    private static Frustum BuildFrustum(TransformBase tfm, float fovY, float aspect, float nearZ, float farZ)
    {
        tfm.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: false);

        Matrix4x4 worldMatrix = tfm.WorldMatrix;
        Vector3 forward = Vector3.Normalize(Vector3.TransformNormal(Globals.Backward, worldMatrix));
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Globals.Up, worldMatrix));
        Vector3 position = worldMatrix.Translation;
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

    private static SceneNode CreateMathTestCell(SceneNode parentNode, string name, Vector3 localTranslation)
    {
        var cellNode = parentNode.NewChild(name);
        var transform = cellNode.SetTransform<Transform>();
        transform.Translation = localTranslation;
        return cellNode;
    }

    private static void DrawPlaneIndicator(DebugDrawComponent debug, Vector3 planePoint, Vector3 planeNormal, float radius, ColorF4 color)
    {
        Vector3 normal = Vector3.Normalize(planeNormal);
        debug.AddCircle(radius, planePoint, normal, color, false);
        debug.AddLine(planePoint, planePoint + normal * (radius * 0.75f), color);
    }

    private static ColorF4 PlaneIntersectionColor(GeometryPlaneIntersection intersection)
        => intersection switch
        {
            GeometryPlaneIntersection.Intersecting => ColorF4.Yellow,
            GeometryPlaneIntersection.Front => ColorF4.LightGreen,
            _ => ColorF4.DarkRed,
        };

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

    private static AnimationClip CreateSineTranslationClip(float baseY, float amplitude, float frequency, float phaseOffset)
    {
        float durationSeconds = MathF.Tau / frequency;
        const int sampleCount = 33;

        PropAnimFloat translationY = new(durationSeconds, looped: true, useKeyframes: true);
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)(sampleCount - 1);
            float time = durationSeconds * t;
            float radians = MathF.Tau * t + phaseOffset;
            float value = baseY + MathF.Sin(radians) * amplitude;
            translationY.Keyframes.Add(new FloatKeyframe(time, value, 0.0f, EVectorInterpType.Linear));
        }

        var root = new AnimationMember("Root", EAnimationMemberType.Group);
        var sceneNodeMember = new AnimationMember("SceneNode", EAnimationMemberType.Property);
        var transformMember = new AnimationMember("Transform", EAnimationMemberType.Property);
        transformMember.Children.Add(new AnimationMember("TranslationY", EAnimationMemberType.Property, translationY));

        root.Children.Add(sceneNodeMember);
        sceneNodeMember.Children.Add(transformMember);

        return new AnimationClip(root)
        {
            Name = "SineTranslationY",
            LengthInSeconds = durationSeconds,
            Looped = true,
            SampleRate = sampleCount - 1,
        };
    }
}
