using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
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
        var world = new XRWorldInstance();
        var manager = world.Lights.LightmapBaking;
        var light = CreateStationaryDynamicCachedLight(movementVersion: 1u);

        manager.ProcessDynamicCachedAutoBake(light);

        GetPendingBakeCount(manager).ShouldBe(0);
    }

    [Test]
    public void ProcessDynamicCachedAutoBake_QueuesOnceWhenEnabled()
    {
        var world = new XRWorldInstance();
        var manager = world.Lights.LightmapBaking;
        manager.AutoBakeDynamicCachedLights = true;
        var light = CreateStationaryDynamicCachedLight(movementVersion: 7u);

        manager.ProcessDynamicCachedAutoBake(light);
        GetPendingBakeCount(manager).ShouldBe(1);

        manager.ProcessDynamicCachedAutoBake(light);
        GetPendingBakeCount(manager).ShouldBe(1);
    }

    private static DirectionalLightComponent CreateStationaryDynamicCachedLight(uint movementVersion)
    {
        var light = new DirectionalLightComponent
        {
            Type = ELightType.DynamicCached
        };

        LastMovedTicksField.SetValue(light, Engine.ElapsedTicks - EngineTimer.SecondsToStopwatchTicks(1.0));
        MovementVersionField.SetValue(light, movementVersion);
        return light;
    }

    private static int GetPendingBakeCount(LightmapBakeManager manager)
        => ((ConcurrentQueue<LightComponent>)ManualBakeRequestsField.GetValue(manager)!).Count;
}