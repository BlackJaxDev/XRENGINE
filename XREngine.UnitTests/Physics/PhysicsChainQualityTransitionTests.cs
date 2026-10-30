using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
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
        var world = new TestWorldContext();
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.UpdateRate = 60.0f;
        node.World = world;
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        PhysicsChainRuntimeHandle handle = component.RuntimeHandle;
        handle.IsValid.ShouldBeTrue();

        // The world owns the cadence clock of each runtime slot.
        // Put the clock at three quarters of one 60 Hz step.
        scheduler!.SetCadenceProgress(handle, component, 0.75f, 60.0f);

        component.SetEffectiveQualityTier(PhysicsChainQualityTier.Hz30);

        scheduler.TryGetSimulationClock(handle, component, out PhysicsChainSimulationClock clock).ShouldBeTrue();
        clock.CadenceProgress.ShouldBe(0.75f, 0.00001f);
        clock.ElapsedSeconds.ShouldBe(0.025f, 0.00001f);

        node.World = null;
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
