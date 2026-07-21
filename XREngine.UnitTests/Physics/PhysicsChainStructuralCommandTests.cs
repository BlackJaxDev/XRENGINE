using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainStructuralCommandTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void AddRemoveAddBeforeDrainAppliesOnlyLatestIntent()
    {
        var world = new TestWorldContext();
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;

        SetWorld(node, world);
        SetWorld(node, null);
        SetWorld(node, world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.RegisteredCount.ShouldBe(1);
        component.RuntimeHandle.IsValid.ShouldBeTrue();
        scheduler.TryResolveRuntimeHandle(component.RuntimeHandle, out PhysicsChainComponent? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(component);

        SetWorld(node, null);
        world.Invoke(ETickGroup.Normal);
        scheduler.RegisteredCount.ShouldBe(0);
        component.RuntimeHandle.ShouldBe(PhysicsChainRuntimeHandle.Invalid);
    }

    [Test]
    public void RemoveAddBeforeDrainDoesNotRecycleTheLiveSlot()
    {
        var world = new TestWorldContext();
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        SetWorld(node, world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainRuntimeHandle originalHandle = component.RuntimeHandle;

        SetWorld(node, null);
        SetWorld(node, world);
        world.Invoke(ETickGroup.Normal);

        component.RuntimeHandle.ShouldBe(originalHandle);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.RegisteredCount.ShouldBe(1);

        SetWorld(node, null);
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
