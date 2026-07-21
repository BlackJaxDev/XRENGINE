using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainTemplateTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void IdenticalAuthoredChainsShareOneImmutableWorldTemplate()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent first = CreateTwoParticleChain("First", world);
        PhysicsChainComponent second = CreateTwoParticleChain("Second", world);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.TryGetOrCreateTemplate(first.RuntimeHandle, out PhysicsChainTemplate? firstTemplate).ShouldBeTrue();
        scheduler.TryGetOrCreateTemplate(second.RuntimeHandle, out PhysicsChainTemplate? secondTemplate).ShouldBeTrue();

        firstTemplate.ShouldNotBeNull();
        secondTemplate.ShouldBeSameAs(firstTemplate);
        scheduler.GetUniqueTemplateCount().ShouldBe(1);
        firstTemplate!.Particles.Length.ShouldBe(2);
        firstTemplate.DepthRanges.Length.ShouldBe(2);
        firstTemplate.DepthOrderedParticleIndices.Span.SequenceEqual([0, 1]).ShouldBeTrue();

        second.Damping = 0.25f;
        scheduler.TryGetOrCreateTemplate(second.RuntimeHandle, out PhysicsChainTemplate? changedTemplate).ShouldBeTrue();
        changedTemplate.ShouldNotBeSameAs(firstTemplate);
        scheduler.GetUniqueTemplateCount().ShouldBe(2);

        SetWorld(first.SceneNode!, null);
        SetWorld(second.SceneNode!, null);
        world.Invoke(ETickGroup.Normal);
    }

    [Test]
    public void StaleHandleCannotResolveOrCreateTemplateAfterSlotReuse()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent first = CreateTwoParticleChain("First", world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainRuntimeHandle staleHandle = first.RuntimeHandle;

        SetWorld(first.SceneNode!, null);
        world.Invoke(ETickGroup.Normal);

        PhysicsChainComponent second = CreateTwoParticleChain("Second", world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();

        scheduler!.TryGetOrCreateTemplate(staleHandle, out _).ShouldBeFalse();
        scheduler.TryGetOrCreateTemplate(second.RuntimeHandle, out _).ShouldBeTrue();

        SetWorld(second.SceneNode!, null);
        world.Invoke(ETickGroup.Normal);
    }

    private static PhysicsChainComponent CreateTwoParticleChain(string name, IRuntimeWorldContext world)
    {
        var node = new SceneNode(name);
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.EndLength = 0.25f;
        SetWorld(node, world);
        return component;
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
