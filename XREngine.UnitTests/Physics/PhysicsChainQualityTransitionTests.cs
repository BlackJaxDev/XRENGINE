using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainQualityTransitionTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void TierTransitionPreservesNormalizedCadenceProgress()
    {
        // The world owns the simulation clock of each registered runtime slot.
        var world = new TestWorldContext();
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.UpdateRate = 60.0f;
        SetWorld(node, world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        PhysicsChainRuntimeHandle handle = component.RuntimeHandle;
        handle.IsValid.ShouldBeTrue();
        component.EffectiveQualityPolicy.SimulationRateHz.ShouldBe(60.0f, 0.00001f);

        scheduler!.SetCadenceProgress(handle, component, 0.75f, 60.0f);
        scheduler.TryGetSimulationClock(handle, component, out PhysicsChainSimulationClock clock).ShouldBeTrue();
        clock.ElapsedSeconds.ShouldBe(0.0125f, 0.00001f);

        component.SetEffectiveQualityTier(PhysicsChainQualityTier.Hz30);

        component.EffectiveQualityPolicy.SimulationRateHz.ShouldBe(30.0f, 0.00001f);
        scheduler.TryGetSimulationClock(handle, component, out clock).ShouldBeTrue();
        clock.CadenceProgress.ShouldBe(0.75f, 0.00001f);
        clock.ElapsedSeconds.ShouldBe(0.025f, 0.00001f);

        SetWorld(node, null);
        world.Invoke(ETickGroup.Normal);
    }

    [Test]
    public void BecomingVisibleWakesSleepingChainAndStartsInteractionGrace()
    {
        var component = new PhysicsChainComponent
        {
            RecentInteractionQualityFrameCount = 12,
        };
        component.SetRuntimeVisibility(false);
        SetField(component, "_isRuntimeSleeping", true);

        component.SetRuntimeVisibility(true);

        component.IsRuntimeSleeping.ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.RelevanceChanged);
        GetField<int>(component, "_recentInteractionQualityFramesRemaining").ShouldBe(12);
    }

    private static void SetWorld(SceneNode node, IRuntimeWorldContext? world)
        => WorldProperty.SetValue(node, world);

    private static void SetField<T>(PhysicsChainComponent component, string name, T value)
    {
        FieldInfo field = typeof(PhysicsChainComponent).GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(typeof(PhysicsChainComponent).FullName, name);
        field.SetValue(component, value);
    }

    private static T GetField<T>(PhysicsChainComponent component, string name)
    {
        FieldInfo field = typeof(PhysicsChainComponent).GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(typeof(PhysicsChainComponent).FullName, name);
        return (T)field.GetValue(component)!;
    }

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];

        public bool IsPlaySessionActive => false;

        public void RegisterTick(ETickGroup group, int order, WorldTick tick)
            => _ticks.Add((group, tick));

        public void UnregisterTick(ETickGroup group, int order, WorldTick tick)
            => _ticks.Remove((group, tick));

        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject)
        {
        }

        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix)
        {
        }

        public void Invoke(ETickGroup group)
        {
            for (int i = 0; i < _ticks.Count; ++i)
                if (_ticks[i].Group == group)
                    _ticks[i].Tick();
        }
    }
}
