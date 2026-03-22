using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using GeometryPlaneIntersection = XREngine.Data.Geometry.EPlaneIntersection;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    private static readonly AABB s_smallMathTestBounds = AABB.FromCenterSize(new Vector3(0.0f, 1.5f, 0.0f), new Vector3(8.0f, 5.0f, 8.0f));
    private static readonly AABB s_physicsChainTestBounds = AABB.FromCenterSize(new Vector3(0.0f, 3.0f, 2.0f), new Vector3(8.0f, 8.0f, 8.0f));

    private sealed record MathIntersectionsWorldTestDefinition(
        string DisplayName,
        string Description,
        AABB Bounds,
        MathIntersectionsWorldTestFactory Factory);

    private static IReadOnlyList<MathIntersectionsWorldTestDefinition> GetMathIntersectionsTestDefinitions()
        =>
        [
            new(
                "Projection Matrix Combiner Test",
                "Fits a shared cyclops projection that encloses the animated left and right eye frusta.",
                AABB.FromCenterSize(new Vector3(0.0f, 4.0f, 9.0f), new Vector3(36.0f, 12.0f, 24.0f)),
                (parent, controller) => AddProjectionMatrixCombinerRig(parent, controller)),
            new(
                "Frustum Intersection Test",
                "Two moving frusta sweep through each other while the green box shows the overlap AABB.",
                AABB.FromCenterSize(new Vector3(0.5f, 4.0f, 7.0f), new Vector3(24.0f, 10.0f, 24.0f)),
                (parent, controller) => AddFrustumIntersectionRig(parent, controller)),
            new(
                "Frustum Containment Test",
                "A moving sphere, AABB, and capsule change color as the frustum contains, intersects, or rejects them.",
                AABB.FromCenterSize(new Vector3(0.0f, 3.0f, 4.0f), new Vector3(18.0f, 8.0f, 24.0f)),
                (parent, controller) => AddFrustumContainmentRig(parent, controller)),
            new(
                "Ray vs Sphere Test",
                "A fixed ray probes a moving sphere; green means hit and yellow marks the first intersection.",
                AABB.FromCenterSize(new Vector3(0.5f, 2.5f, 4.5f), new Vector3(20.0f, 6.0f, 20.0f)),
                (parent, _) => AddRaySphereRig(parent)),
            new(
                "Segment vs AABB Test",
                "A moving segment clips against an AABB and exposes the entry and exit points.",
                AABB.FromCenterSize(new Vector3(8.0f, 2.0f, -2.0f), new Vector3(10.0f, 5.0f, 10.0f)),
                (parent, _) => AddSegmentAabbRig(parent)),
            new(
                "Ray vs Triangle Test",
                "An animated ray probes a triangle and highlights the barycentric hit point when it intersects.",
                AABB.FromCenterSize(new Vector3(-2.0f, 2.5f, 20.5f), new Vector3(26.0f, 6.0f, 26.0f)),
                (parent, _) => AddRayTriangleRig(parent)),
            new(
                "GeoUtil Ray with Point Test",
                "Exercises GeoUtil.Intersect.RayWithPoint with an aligned and offset probe point.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilRayWithPointRig(parent)),
            new(
                "GeoUtil Ray with Ray Test",
                "Exercises GeoUtil.Intersect.RayWithRay with intersecting and skewed rays.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilRayWithRayRig(parent)),
            new(
                "GeoUtil Ray with Plane Test",
                "Exercises GeoUtil.Intersect.RayWithPlane with hit and miss directions.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilRayWithPlaneRig(parent)),
            new(
                "GeoUtil Ray with AABB Test",
                "Exercises GeoUtil.Intersect.RayWithAABB against a moving target point.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilRayWithAabbRig(parent)),
            new(
                "GeoUtil Ray with Box Test",
                "Exercises GeoUtil.Intersect.RayWithBox against a rotating oriented box.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilRayWithBoxRig(parent)),
            new(
                "GeoUtil AABB with AABB Test",
                "Exercises GeoUtil.Intersect.AABBWithAABB with two moving bounds.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilAabbWithAabbRig(parent)),
            new(
                "GeoUtil AABB with Sphere Test",
                "Exercises GeoUtil.Intersect.AABBWithSphere with an oscillating sphere.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilAabbWithSphereRig(parent)),
            new(
                "GeoUtil Box with Sphere Test",
                "Exercises GeoUtil.Intersect.BoxWithSphere against a rotating oriented box.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilBoxWithSphereRig(parent)),
            new(
                "GeoUtil Sphere with Triangle Test",
                "Exercises GeoUtil.Intersect.SphereWithTriangle with a moving sphere.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilSphereWithTriangleRig(parent)),
            new(
                "GeoUtil Sphere with Sphere Test",
                "Exercises GeoUtil.Intersect.SphereWithSphere with oscillating centers.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilSphereWithSphereRig(parent)),
            new(
                "GeoUtil Segment with Plane Test",
                "Exercises GeoUtil.Intersect.SegmentWithPlane with a moving line segment.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilSegmentWithPlaneRig(parent)),
            new(
                "GeoUtil Plane with Point Test",
                "Exercises plane-side classification for a moving point.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPlaneWithPointRig(parent)),
            new(
                "GeoUtil Plane with Plane Test",
                "Exercises plane-plane classification and line extraction.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPlaneWithPlaneRig(parent)),
            new(
                "GeoUtil Plane with Triangle Test",
                "Exercises plane-triangle classification with an animated triangle.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPlaneWithTriangleRig(parent)),
            new(
                "GeoUtil Plane with Box Test",
                "Exercises plane-box classification against a rotating oriented box.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPlaneWithBoxRig(parent)),
            new(
                "GeoUtil Plane with Sphere Test",
                "Exercises plane-sphere classification with an oscillating sphere.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPlaneWithSphereRig(parent)),
            new(
                "GeoUtil Point Between Planes Test",
                "Exercises PointBetweenPlanes for facing, away, and straddling examples.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilPointBetweenPlanesRig(parent)),
            new(
                "GeoUtil Three Planes Test",
                "Exercises ThreePlanes by drawing the shared intersection point.",
                s_smallMathTestBounds,
                (parent, _) => AddGeoUtilThreePlanesRig(parent)),
            new(
                "Physics Chain CPU Test",
                "Runs a single-threaded CPU physics chain.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain CPU Test", useGpu: false, multithread: false, useBatchedDispatcher: false, gpuSyncToBones: false, phaseOffset: 0.0f, ColorF4.LightBlue, ColorF4.LightGold)),
            new(
                "Physics Chain CPU Multithreaded Test",
                "Runs a CPU physics chain with multithreaded execution enabled.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain CPU Multithreaded Test", useGpu: false, multithread: true, useBatchedDispatcher: false, gpuSyncToBones: false, phaseOffset: 0.35f, new ColorF4(0.39f, 0.58f, 0.93f, 1.0f), ColorF4.LightGold)),
            new(
                "Physics Chain GPU Standalone Test",
                "Runs a GPU physics chain without the batched dispatcher.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Standalone Test", useGpu: true, multithread: false, useBatchedDispatcher: false, gpuSyncToBones: false, phaseOffset: 0.7f, ColorF4.LightGreen, ColorF4.Orange)),
            new(
                "Physics Chain GPU Dispatcher Test",
                "Runs a GPU physics chain through the shared batched dispatcher.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: false, phaseOffset: 1.05f, new ColorF4(0.0f, 1.0f, 0.5f, 1.0f), ColorF4.Orange)),
            new(
                "Physics Chain GPU Sync To Bones Test",
                "Runs a standalone GPU physics chain with Sync To Bones enabled.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Sync To Bones Test", useGpu: true, multithread: false, useBatchedDispatcher: false, gpuSyncToBones: true, phaseOffset: 1.4f, new ColorF4(0.13f, 0.70f, 0.67f, 1.0f), ColorF4.Orange)),
            new(
                "Physics Chain GPU Dispatcher Sync To Bones Test",
                "Runs a dispatcher-backed GPU physics chain with Sync To Bones enabled.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher Sync To Bones Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: true, phaseOffset: 1.75f, new ColorF4(0.24f, 0.88f, 0.55f, 1.0f), ColorF4.Orange)),
            new(
                "Physics Chain GPU Dispatcher Skinned Mesh Test",
                "Runs the dispatcher-backed GPU physics chain with a skinned box mesh driven directly from the GPU bone palette path.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher Skinned Mesh Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: false, phaseOffset: 1.92f, new ColorF4(0.18f, 0.84f, 0.72f, 1.0f), ColorF4.Orange, includeSkinnedMesh: true)),
            new(
                "Physics Chain GPU Dispatcher Skinned Mesh Sync To Bones Test",
                "Runs the dispatcher-backed GPU physics chain with a skinned box mesh while the higher-cost CPU Sync To Bones compatibility path remains enabled.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher Skinned Mesh Sync To Bones Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: true, phaseOffset: 2.0f, new ColorF4(0.32f, 0.79f, 0.58f, 1.0f), ColorF4.Orange, includeSkinnedMesh: true)),
            new(
                "Physics Chain GPU Dispatcher No Collider Test",
                "Runs the dispatcher-backed GPU physics chain without colliders to isolate solver, upload, and transform costs.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher No Collider Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: false, phaseOffset: 2.1f, new ColorF4(0.31f, 0.76f, 0.97f, 1.0f), ColorF4.LightGold, PhysicsChainColliderScenario.None)),
            new(
                "Physics Chain GPU Dispatcher Heavy Collider Test",
                "Runs the dispatcher-backed GPU physics chain with a heavier collider set to stress collider upload and collision cost.",
                s_physicsChainTestBounds,
                (parent, _) => AddPhysicsChainVariantRig(parent, "Physics Chain GPU Dispatcher Heavy Collider Test", useGpu: true, multithread: false, useBatchedDispatcher: true, gpuSyncToBones: false, phaseOffset: 2.45f, new ColorF4(0.0f, 0.84f, 0.84f, 1.0f), new ColorF4(1.0f, 0.76f, 0.25f, 1.0f), PhysicsChainColliderScenario.Heavy)),
        ];

    private static SceneNode AddGeoUtilRayWithPointRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Ray With Point Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
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

            debug.ClearShapes();
            debug.AddLine(origin, origin + direction * 6.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(origin, ColorF4.LightGold);
            debug.AddPoint(point, hit ? ColorF4.Yellow : ColorF4.DarkRed);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilRayWithRayRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Ray With Ray Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Ray ray1 = new(new Vector3(-2.5f, 1.0f, 0.0f), Vector3.UnitX);
            bool aligned = MathF.Sin(t * 0.8f) >= 0.0f;
            Ray ray2 = new(new Vector3(0.0f, aligned ? 1.0f : 1.75f, -2.5f), Vector3.UnitZ);
            bool hit = GeoUtil.Intersect.RayWithRay(ray1, ray2, out Vector3 intersection);

            debug.ClearShapes();
            debug.AddLine(ray1.StartPoint, ray1.StartPoint + ray1.Direction * 5.0f, ColorF4.LightBlue);
            debug.AddLine(ray2.StartPoint, ray2.StartPoint + ray2.Direction * 5.0f, ColorF4.Orange);
            debug.AddPoint(ray1.StartPoint, ColorF4.LightGold);
            debug.AddPoint(ray2.StartPoint, ColorF4.LightGold);
            if (hit)
                debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilRayWithPlaneRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Ray With Plane Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 planePoint = new(0.0f, 1.25f, 0.0f);
            Vector3 planeNormal = Vector3.UnitY;
            Vector3 origin = new(-2.5f, 0.4f, -2.0f);
            Vector3 direction = MathF.Sin(t * 0.75f) >= 0.0f
                ? Vector3.Normalize(new Vector3(1.0f, 0.55f, 1.0f))
                : Vector3.Normalize(new Vector3(1.0f, -0.1f, 1.0f));
            bool hit = GeoUtil.Intersect.RayWithPlane(origin, direction, planePoint, planeNormal, out Vector3 intersection);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, planeNormal, 2.0f, ColorF4.LightBlue);
            debug.AddLine(origin, origin + direction * 6.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(origin, ColorF4.LightGold);
            if (hit)
                debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilRayWithAabbRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Ray With AABB Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
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

            debug.ClearShapes();
            debug.AddBox(half, center, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            debug.AddLine(origin, origin + direction * 7.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(origin, ColorF4.LightGold);
            if (hit)
                debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilRayWithBoxRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Ray With Box Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        var boxVisualNode = rigNode.NewChild("BoxVisual");
        var boxVisualTransform = boxVisualNode.SetTransform<Transform>();
        var boxVisualDebug = boxVisualNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.2f, 0.0f);
            Vector3 half = new(1.1f, 0.7f, 0.8f);
            Quaternion rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, DegreesToRadians(t * 35.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, DegreesToRadians(18.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);

            Vector3 origin = new(-3.0f, 1.1f, -1.8f);
            Vector3 target = MathF.Sin(t * 0.65f) >= 0.0f ? center : center + new Vector3(0.0f, 2.0f, 1.8f);
            Vector3 direction = Vector3.Normalize(target - origin);
            bool hit = GeoUtil.Intersect.RayWithBox(origin, direction, half, inverseWorld, out Vector3 intersection);

            boxVisualTransform.Translation = center;
            boxVisualTransform.Rotation = rotation;

            debug.ClearShapes();
            boxVisualDebug.ClearShapes();
            debug.AddLine(origin, origin + direction * 7.0f, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(origin, ColorF4.LightGold);
            boxVisualDebug.AddBox(half, Vector3.Zero, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            if (hit)
                debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilAabbWithAabbRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil AABB With AABB Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 box1Center = new(-0.8f, 1.0f, 0.0f);
            Vector3 box1Half = new(1.0f, 0.8f, 0.9f);
            Vector3 box2Center = new(1.6f + MathF.Sin(t * 0.8f) * 1.8f, 1.0f, 0.0f);
            Vector3 box2Half = new(0.9f, 0.75f, 0.8f);
            bool hit = GeoUtil.Intersect.AABBWithAABB(
                new AABB(box1Center - box1Half, box1Center + box1Half),
                new AABB(box2Center - box2Half, box2Center + box2Half));

            debug.ClearShapes();
            debug.AddBox(box1Half, box1Center, hit ? ColorF4.LightGreen : ColorF4.LightBlue, false);
            debug.AddBox(box2Half, box2Center, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilAabbWithSphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil AABB With Sphere Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.15f, 0.0f);
            Vector3 half = new(1.0f, 0.9f, 0.8f);
            Vector3 sphereCenter = new(1.6f + MathF.Sin(t * 0.9f) * 1.7f, 1.15f, 0.0f);
            const float sphereRadius = 0.7f;
            bool hit = GeoUtil.Intersect.AABBWithSphere(center - half, center + half, sphereCenter, sphereRadius);

            debug.ClearShapes();
            debug.AddBox(half, center, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            debug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilBoxWithSphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Box With Sphere Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        var boxVisualNode = rigNode.NewChild("BoxVisual");
        var boxVisualTransform = boxVisualNode.SetTransform<Transform>();
        var boxVisualDebug = boxVisualNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 center = new(0.0f, 1.15f, 0.0f);
            Vector3 half = new(1.0f, 0.7f, 0.9f);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f)), DegreesToRadians(t * 30.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
            Vector3 sphereCenter = new(1.7f + MathF.Sin(t * 0.8f) * 1.6f, 1.15f, 0.0f);
            const float sphereRadius = 0.65f;
            bool hit = GeoUtil.Intersect.BoxWithSphere(half, inverseWorld, sphereCenter, sphereRadius);

            boxVisualTransform.Translation = center;
            boxVisualTransform.Rotation = rotation;

            debug.ClearShapes();
            boxVisualDebug.ClearShapes();
            boxVisualDebug.AddBox(half, Vector3.Zero, hit ? ColorF4.LightGreen : ColorF4.Gray, false);
            debug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilSphereWithTriangleRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Sphere With Triangle Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 a = new(-1.5f, 0.6f, -0.8f);
            Vector3 b = new(1.5f, 1.8f, -0.2f);
            Vector3 c = new(0.2f, 0.7f, 1.7f);
            Vector3 sphereCenter = new(0.0f, 1.0f + MathF.Sin(t * 0.85f) * 1.2f, 0.3f + MathF.Cos(t * 0.55f) * 0.9f);
            const float sphereRadius = 0.65f;
            bool hit = GeoUtil.Intersect.SphereWithTriangle(sphereCenter, sphereRadius, a, b, c);

            debug.ClearShapes();
            debug.AddLine(a, b, ColorF4.White);
            debug.AddLine(b, c, ColorF4.White);
            debug.AddLine(c, a, ColorF4.White);
            debug.AddSphere(sphereRadius, sphereCenter, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilSphereWithSphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Sphere With Sphere Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 centerA = new(-0.9f, 1.15f, 0.0f);
            Vector3 centerB = new(1.4f + MathF.Sin(t * 0.95f) * 1.7f, 1.15f, 0.0f);
            const float radiusA = 0.85f;
            const float radiusB = 0.7f;
            bool hit = GeoUtil.Intersect.SphereWithSphere(centerA, radiusA, centerB, radiusB);

            debug.ClearShapes();
            debug.AddSphere(radiusA, centerA, hit ? ColorF4.LightGreen : ColorF4.LightBlue, false);
            debug.AddSphere(radiusB, centerB, hit ? ColorF4.LightGreen : ColorF4.Orange, false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilSegmentWithPlaneRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Segment With Plane Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Vector3 planePoint = new(0.0f, 1.2f, 0.0f);
            Vector3 planeNormal = Vector3.UnitY;
            float planeDistance = -Vector3.Dot(planePoint, planeNormal);
            Vector3 start = new(-2.0f, 0.45f + MathF.Sin(t * 0.7f) * 1.3f, -0.8f);
            Vector3 end = new(2.0f, 2.15f + MathF.Cos(t * 0.9f) * 1.2f, 0.8f);
            bool hit = GeoUtil.Intersect.SegmentWithPlane(start, end, planeDistance, planeNormal, out Vector3 intersection);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, planeNormal, 1.8f, ColorF4.LightBlue);
            debug.AddLine(start, end, hit ? ColorF4.LightGreen : ColorF4.LightGray);
            debug.AddPoint(start, ColorF4.LightGold);
            debug.AddPoint(end, ColorF4.LightGold);
            if (hit)
                debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneWithPointRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane With Point Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.1f);
            Vector3 planePoint = new(0.0f, 1.1f, 0.0f);
            Vector3 point = new(0.0f, 1.1f + MathF.Sin(t * 0.9f) * 1.7f, 0.0f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithPoint(plane, point);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            debug.AddPoint(point, PlaneIntersectionColor(intersection));
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneWithPlaneRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane With Plane Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane planeA = new(Vector3.UnitY, -1.0f);
            Plane planeB = MathF.Sin(t * 0.8f) >= 0.0f
                ? new Plane(Vector3.UnitZ, 0.0f)
                : new Plane(Vector3.UnitY, -2.0f);

            bool intersects = GeoUtil.Intersect.PlaneWithPlane(planeA, planeB);
            bool lineHit = GeoUtil.Intersect.PlaneWithPlane(planeA, planeB, out Ray line);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, new Vector3(0.0f, 1.0f, 0.0f), planeA.Normal, 1.8f, ColorF4.LightBlue);
            DrawPlaneIndicator(debug, intersects ? Vector3.Zero : new Vector3(0.0f, 2.0f, 0.0f), planeB.Normal, 1.8f, ColorF4.Orange);
            if (lineHit)
                debug.AddLine(line.StartPoint - line.Direction * 2.5f, line.StartPoint + line.Direction * 2.5f, ColorF4.LightGreen);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneWithTriangleRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane With Triangle Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.1f);
            Vector3 planePoint = new(0.0f, 1.1f, 0.0f);
            float offset = MathF.Sin(t * 0.75f) * 1.4f;
            Vector3 a = new(-1.4f, 0.6f + offset, -0.9f);
            Vector3 b = new(1.3f, 1.6f + offset, -0.2f);
            Vector3 c = new(0.1f, 0.85f - offset, 1.4f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithTriangle(plane, a, b, c);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            ColorF4 triangleColor = PlaneIntersectionColor(intersection);
            debug.AddLine(a, b, triangleColor);
            debug.AddLine(b, c, triangleColor);
            debug.AddLine(c, a, triangleColor);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneWithBoxRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane With Box Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        var boxVisualNode = rigNode.NewChild("BoxVisual");
        var boxVisualTransform = boxVisualNode.SetTransform<Transform>();
        var boxVisualDebug = boxVisualNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = Plane.Normalize(new Plane(Vector3.Normalize(new Vector3(0.0f, 1.0f, 0.25f)), -1.0f));
            Vector3 planePoint = -plane.Normal * plane.D;
            Vector3 center = new(0.0f, 1.25f + MathF.Sin(t * 0.7f) * 1.3f, 0.0f);
            Vector3 half = new(0.95f, 0.7f, 0.85f);
            Quaternion rotation =
                Quaternion.CreateFromAxisAngle(Globals.Up, DegreesToRadians(t * 28.0f)) *
                Quaternion.CreateFromAxisAngle(Globals.Right, DegreesToRadians(20.0f));
            Matrix4x4 worldMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center);
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithBox(plane, -half, half, inverseWorld);

            boxVisualTransform.Translation = center;
            boxVisualTransform.Rotation = rotation;

            debug.ClearShapes();
            boxVisualDebug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, plane.Normal, 2.0f, ColorF4.LightBlue);
            boxVisualDebug.AddBox(half, Vector3.Zero, PlaneIntersectionColor(intersection), false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPlaneWithSphereRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Plane With Sphere Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            float t = (float)Engine.ElapsedTime;
            Plane plane = new(Vector3.UnitY, -1.15f);
            Vector3 planePoint = new(0.0f, 1.15f, 0.0f);
            Sphere sphere = new(new Vector3(0.0f, 1.15f + MathF.Sin(t * 0.8f) * 1.7f, 0.0f), 0.7f);
            GeometryPlaneIntersection intersection = GeoUtil.Intersect.PlaneWithSphere(plane, sphere);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, planePoint, plane.Normal, 1.9f, ColorF4.LightBlue);
            debug.AddSphere(sphere.Radius, sphere.Center, PlaneIntersectionColor(intersection), false);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilPointBetweenPlanesRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Point Between Planes Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            Plane planeA = new(Vector3.UnitX, 0.0f);
            Plane planeB = new(Vector3.UnitZ, 0.0f);
            Vector3 facingPoint = new(1.0f, 1.0f, 1.0f);
            Vector3 awayPoint = new(-1.0f, 1.0f, -1.0f);
            Vector3 straddlePoint = new(1.0f, 1.0f, -1.0f);

            bool facing = GeoUtil.Intersect.PointBetweenPlanes(facingPoint, planeA, planeB, EBetweenPlanes.NormalsFacing);
            bool away = GeoUtil.Intersect.PointBetweenPlanes(awayPoint, planeA, planeB, EBetweenPlanes.NormalsAway);
            bool straddles = GeoUtil.Intersect.PointBetweenPlanes(straddlePoint, planeA, planeB, EBetweenPlanes.DontCare);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, Vector3.Zero, planeA.Normal, 1.8f, ColorF4.LightBlue);
            DrawPlaneIndicator(debug, Vector3.Zero, planeB.Normal, 1.8f, ColorF4.Orange);
            debug.AddPoint(facingPoint, facing ? ColorF4.LightGreen : ColorF4.DarkRed);
            debug.AddPoint(awayPoint, away ? ColorF4.LightGreen : ColorF4.DarkRed);
            debug.AddPoint(straddlePoint, straddles ? ColorF4.Yellow : ColorF4.DarkRed);
        });

        return rigNode;
    }

    private static SceneNode AddGeoUtilThreePlanesRig(SceneNode rootNode)
    {
        var rigNode = rootNode.NewChild("GeoUtil Three Planes Test");
        rigNode.SetTransform<Transform>();
        var debug = rigNode.AddComponent<DebugDrawComponent>()!;
        rigNode.RegisterAnimationTick<SceneNode>(_ =>
        {
            Plane planeA = new(Vector3.Normalize(new Vector3(1.0f, 1.0f, 0.0f)), 0.0f);
            Plane planeB = new(Vector3.Normalize(new Vector3(0.0f, 1.0f, 1.0f)), 0.0f);
            Plane planeC = new(Vector3.Normalize(new Vector3(1.0f, 0.0f, 1.0f)), 0.0f);
            Vector3 intersection = GeoUtil.Intersect.ThreePlanes(planeA, planeB, planeC);

            debug.ClearShapes();
            DrawPlaneIndicator(debug, Vector3.Zero, planeA.Normal, 1.5f, ColorF4.LightBlue);
            DrawPlaneIndicator(debug, Vector3.Zero, planeB.Normal, 1.5f, ColorF4.Orange);
            DrawPlaneIndicator(debug, Vector3.Zero, planeC.Normal, 1.5f, ColorF4.LightGreen);
            debug.AddPoint(intersection, ColorF4.Yellow);
        });

        return rigNode;
    }

    private static SceneNode AddPhysicsChainVariantRig(
        SceneNode rootNode,
        string rigName,
        bool useGpu,
        bool multithread,
        bool useBatchedDispatcher,
        bool gpuSyncToBones,
        float phaseOffset,
        ColorF4 chainColor,
        ColorF4 rootColor,
        PhysicsChainColliderScenario colliderScenario = PhysicsChainColliderScenario.Default,
        bool includeSkinnedMesh = false)
    {
        var rigNode = rootNode.NewChild(rigName);
        rigNode.SetTransform<Transform>();
        AddPhysicsChainTest(
            rigNode,
            $"{rigName} Solver",
            Vector3.Zero,
            useGpu,
            multithread,
            useBatchedDispatcher,
            gpuSyncToBones,
            phaseOffset,
            chainColor,
            rootColor,
            colliderScenario,
            includeSkinnedMesh);
        return rigNode;
    }

    private static float DegreesToRadians(float degrees)
        => degrees * (MathF.PI / 180.0f);
}
