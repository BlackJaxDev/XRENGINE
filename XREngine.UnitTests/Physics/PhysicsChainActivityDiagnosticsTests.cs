using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainActivityDiagnosticsTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SleepingField = typeof(PhysicsChainComponent).GetField(
        "_isRuntimeSleeping", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public void CountersAndSelectionAreDelayedBoundedAndDeduplicated()
    {
        var world = new TestWorldContext();
        var firstNode = new SceneNode("FirstPhysicsChain");
        var secondNode = new SceneNode("SecondPhysicsChain");
        PhysicsChainComponent first = firstNode.AddComponent<PhysicsChainComponent>()!;
        PhysicsChainComponent second = secondNode.AddComponent<PhysicsChainComponent>()!;
        SetWorld(firstNode, world);
        SetWorld(secondNode, world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();

        PhysicsChainRuntimeHandle[] selection = new PhysicsChainRuntimeHandle[
            PhysicsChainWorld.MaximumSelectedActivityDiagnostics + 4];
        for (int i = 0; i < selection.Length; ++i)
            selection[i] = (i & 1) == 0 ? first.RuntimeHandle : second.RuntimeHandle;
        scheduler!.SetActivityDiagnosticSelection(selection);

        world.Invoke(ETickGroup.Late);
        scheduler.GetActivityCounters().SampledFrame.ShouldBe(-1L);
        world.Invoke(ETickGroup.Late);
        PhysicsChainActivityCounters active = scheduler.GetActivityCounters();
        active.SampledFrame.ShouldBe(0L);
        active.ActiveCount.ShouldBe(2);
        Span<PhysicsChainSelectedActivityDiagnostic> selected =
            stackalloc PhysicsChainSelectedActivityDiagnostic[PhysicsChainWorld.MaximumSelectedActivityDiagnostics];
        scheduler.CopySelectedActivityDiagnostics(selected).ShouldBe(2);

        SleepingField.SetValue(first, true);
        world.Invoke(ETickGroup.Late);
        world.Invoke(ETickGroup.Late);
        PhysicsChainActivityCounters sleeping = scheduler.GetActivityCounters();
        sleeping.SleepingCount.ShouldBe(1);
        sleeping.EnteredSleepCount.ShouldBe(1);

        first.Wake();
        world.Invoke(ETickGroup.Late);
        world.Invoke(ETickGroup.Late);
        scheduler.GetActivityCounters().WakeCount.ShouldBe(1UL);

        SetWorld(firstNode, null);
        SetWorld(secondNode, null);
        world.Invoke(ETickGroup.Normal);
    }

    private static void SetWorld(SceneNode node, IRuntimeWorldContext? world)
        => WorldProperty.SetValue(node, world);

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
