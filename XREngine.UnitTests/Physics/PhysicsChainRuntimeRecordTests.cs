using NUnit.Framework;
using Shouldly;
using XREngine.Scene;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainRuntimeRecordTests
{
    [Test]
    public void TemplateGetsAStablePerWorldIdentityAfterDeduplication()
    {
        var world = new TestWorldContext();
        PhysicsChainComponent first = Register(world);
        PhysicsChainComponent second = Register(world);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        PhysicsChainWorld activeScheduler = scheduler!;

        activeScheduler.TryGetOrCreateTemplate(first.RuntimeHandle, out PhysicsChainTemplate? firstTemplate).ShouldBeTrue();
        activeScheduler.TryGetOrCreateTemplate(second.RuntimeHandle, out PhysicsChainTemplate? secondTemplate).ShouldBeTrue();

        firstTemplate.ShouldBeSameAs(secondTemplate);
        firstTemplate!.StableId.ShouldBeGreaterThan(0L);
        secondTemplate!.StableId.ShouldBe(firstTemplate.StableId);
    }

    [Test]
    public void InstanceAndStateExposeOnlyStableSlicesAndResetTemporalHistory()
    {
        var current = new PhysicsChainArenaSlice(10, 4, 2u);
        var previous = new PhysicsChainArenaSlice(20, 4, 2u);
        var state = new PhysicsChainState
        {
            CurrentParticles = current,
            PreviousParticles = previous,
            VelocityAndInertia = new PhysicsChainArenaSlice(30, 4, 2u),
            FixedStepRemainderSeconds = 0.01,
            StateGeneration = 2u,
        };

        state.IsValid.ShouldBeTrue();
        state.ResetHistory();
        state.PreviousParticles.ShouldBe(current);
        state.FixedStepRemainderSeconds.ShouldBe(0.0);
    }

    private static PhysicsChainComponent Register(TestWorldContext world)
    {
        var node = new SceneNode();
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.World = world;
        world.Run(ETickGroup.PostPhysics);
        return component;
    }
}
