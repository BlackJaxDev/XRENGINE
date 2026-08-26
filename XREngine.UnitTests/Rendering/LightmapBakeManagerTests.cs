using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Rendering.Lightmapping;
using XREngine.Timers;

namespace XREngine.UnitTests.Rendering;

public sealed class LightmapBakeManagerTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo ManualBakeRequestsField = typeof(LightmapBakeManager)
        .GetField("_manualBakeRequests", InstanceFlags)!;

    private static readonly FieldInfo LastMovedTicksField = typeof(LightComponent)
        .GetField("_lastMovedTicks", InstanceFlags)!;

    private static readonly FieldInfo MovementVersionField = typeof(LightComponent)
        .GetField("_movementVersion", InstanceFlags)!;

    [Test]
    public void ProcessDynamicCachedAutoBake_IsDisabledByDefault()
    {
        using RuntimeWorld coreWorld = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(coreWorld, new VisualScene3D());
        var manager = renderWorld.Lights.LightmapBaking;
        var light = CreateStationaryDynamicCachedLight(coreWorld, movementVersion: 1u);

        manager.ProcessDynamicCachedAutoBake(light);

        GetPendingBakeCount(manager).ShouldBe(0);
    }

    [Test]
    public void ProcessDynamicCachedAutoBake_QueuesOnceWhenEnabled()
    {
        using RuntimeWorld coreWorld = new(new JitterScene());
        using RuntimeWorldRenderer renderWorld = new(coreWorld, new VisualScene3D());
        var manager = renderWorld.Lights.LightmapBaking;
        manager.AutoBakeDynamicCachedLights = true;
        var light = CreateStationaryDynamicCachedLight(coreWorld, movementVersion: 7u);

        manager.ProcessDynamicCachedAutoBake(light);
        GetPendingBakeCount(manager).ShouldBe(1);

        manager.ProcessDynamicCachedAutoBake(light);
        GetPendingBakeCount(manager).ShouldBe(1);
    }

    private static DirectionalLightComponent CreateStationaryDynamicCachedLight(RuntimeWorld world, uint movementVersion)
    {
        var node = new SceneNode(world) { IsActiveSelf = false };
        var light = node.AddComponent<DirectionalLightComponent>()!;
        light.Type = ELightType.DynamicCached;

        LastMovedTicksField.SetValue(light, Engine.ElapsedTicks - EngineTimer.SecondsToStopwatchTicks(1.0));
        MovementVersionField.SetValue(light, movementVersion);
        return light;
    }

    private static int GetPendingBakeCount(LightmapBakeManager manager)
        => ((ConcurrentQueue<LightComponent>)ManualBakeRequestsField.GetValue(manager)!).Count;
}
