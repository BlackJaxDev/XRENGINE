using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace VR_MonkeyBall_Sample.Assets;

public sealed class MonkeyBallBootstrapComponent : XRComponent
{
    private bool _initialized;

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        if (_initialized)
            return;

        _initialized = true;
        AddCourseDebugGeometry(SceneNode);
        AddPlayerBall(SceneNode);
        AddGoalMarker(SceneNode);
    }

    protected override void OnEndPlay()
    {
        _initialized = false;
        base.OnEndPlay();
    }

    private static void AddCourseDebugGeometry(SceneNode rootNode)
    {
        SceneNode courseNode = rootNode.NewChild("Course");
        DebugDrawComponent courseDebug = courseNode.AddComponent<DebugDrawComponent>()!;

        AddGrid(courseDebug, halfExtent: 20.0f, step: 1.0f, majorEvery: 5);
        courseDebug.AddBox(new Vector3(8.0f, 0.20f, 8.0f), new Vector3(0.0f, -0.20f, 0.0f), ColorF4.DarkGray, solid: true);
        courseDebug.AddBox(new Vector3(1.50f, 0.12f, 4.0f), new Vector3(0.0f, 0.12f, -8.0f), ColorF4.LightGray, solid: true);
        courseDebug.AddLine(new Vector3(0.0f, 0.20f, -12.0f), new Vector3(0.0f, 0.20f, -16.0f), ColorF4.Cyan);
    }

    private static void AddPlayerBall(SceneNode rootNode)
    {
        SceneNode ballNode = rootNode.NewChild("PlayerBall");
        Transform ballTransform = ballNode.GetTransformAs<Transform>(true)!;
        ballTransform.Translation = new Vector3(0.0f, 1.0f, 0.0f);

        DebugDrawComponent debug = ballNode.AddComponent<DebugDrawComponent>()!;
        debug.AddSphere(0.50f, Vector3.Zero, ColorF4.Orange, solid: false);
        debug.AddLine(Vector3.Zero, new Vector3(0.0f, 0.0f, 0.75f), ColorF4.Yellow);

        ballNode.AddComponent<MonkeyBallBallComponent>();
    }

    private static void AddGoalMarker(SceneNode rootNode)
    {
        SceneNode goalNode = rootNode.NewChild("Goal");
        Transform goalTransform = goalNode.GetTransformAs<Transform>(true)!;
        goalTransform.Translation = new Vector3(0.0f, 0.0f, -14.0f);

        DebugDrawComponent goalDebug = goalNode.AddComponent<DebugDrawComponent>()!;
        goalDebug.AddCylinder(1.0f, 0.08f, Vector3.Zero, Vector3.UnitY, ColorF4.Green, solid: false);
        goalDebug.AddLine(new Vector3(-0.7f, 0.0f, 0.0f), new Vector3(0.7f, 0.0f, 0.0f), ColorF4.LightGreen);
        goalDebug.AddLine(new Vector3(0.0f, 0.0f, -0.7f), new Vector3(0.0f, 0.0f, 0.7f), ColorF4.LightGreen);
    }

    private static void AddGrid(DebugDrawComponent debug, float halfExtent, float step, int majorEvery)
    {
        for (float x = -halfExtent; x <= halfExtent; x += step)
        {
            int xi = (int)MathF.Round(x);
            bool isAxis = xi == 0;
            bool isMajor = (xi % majorEvery) == 0;
            ColorF4 color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
            debug.AddLine(new Vector3(x, 0.0f, -halfExtent), new Vector3(x, 0.0f, halfExtent), color);
        }

        for (float z = -halfExtent; z <= halfExtent; z += step)
        {
            int zi = (int)MathF.Round(z);
            bool isAxis = zi == 0;
            bool isMajor = (zi % majorEvery) == 0;
            ColorF4 color = isAxis ? ColorF4.White : isMajor ? ColorF4.Gray : ColorF4.DarkGray;
            debug.AddLine(new Vector3(-halfExtent, 0.0f, z), new Vector3(halfExtent, 0.0f, z), color);
        }
    }
}
