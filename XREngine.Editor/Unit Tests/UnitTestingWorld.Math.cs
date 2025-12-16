using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
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

            bool hit = GeoUtil.SegmentIntersectsAABB(segA, segB, aabbMin, aabbMax, out Vector3 pEnter, out Vector3 pExit);

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

            bool hit = GeoUtil.RayIntersectsTriangle(origin, dir, a, b, c, out float dist);
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

        if (!FrustumIntersection.TryIntersectFrustaAabb(preparedA, preparedB, out Vector3 min, out Vector3 max))
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
}
