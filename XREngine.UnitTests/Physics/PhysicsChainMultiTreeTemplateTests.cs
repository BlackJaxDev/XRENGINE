using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainMultiTreeTemplateTests
{
    private static readonly PropertyInfo WorldProperty = typeof(RuntimeWorldObjectBase).GetProperty(
        nameof(RuntimeWorldObjectBase.World),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void FlattenedTemplateOffsetsParentsAndBoneMappingsForEveryTree()
    {
        var world = new TestWorldContext();
        var componentNode = new SceneNode("Component");
        var firstRootNode = new SceneNode(componentNode, "FirstRoot");
        var secondRootNode = new SceneNode(componentNode, "SecondRoot");
        Transform firstRoot = firstRootNode.GetTransformAs<Transform>(true)!;
        Transform secondRoot = secondRootNode.GetTransformAs<Transform>(true)!;
        PhysicsChainComponent component = componentNode.AddComponent<PhysicsChainComponent>()!;
        component.Roots = [firstRoot, secondRoot];
        component.EndLength = 0.25f;

        WorldProperty.SetValue(componentNode, world);
        world.Invoke(ETickGroup.Normal);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        scheduler.ShouldNotBeNull();
        scheduler!.TryGetOrCreateTemplate(component.RuntimeHandle, out PhysicsChainTemplate? template).ShouldBeTrue();
        template.ShouldNotBeNull();

        ReadOnlySpan<PhysicsChainTemplateTree> trees = template!.Trees.Span;
        ReadOnlySpan<PhysicsChainTemplateParticle> particles = template.Particles.Span;
        trees.Length.ShouldBe(2);
        trees[0].ParticleCount.ShouldBe(2);
        trees[1].ParticleCount.ShouldBe(2);
        particles[trees[0].ParticleStart].ParentIndex.ShouldBe(-1);
        particles[trees[0].ParticleStart + 1].ParentIndex.ShouldBe(trees[0].ParticleStart);
        particles[trees[1].ParticleStart].ParentIndex.ShouldBe(-1);
        particles[trees[1].ParticleStart + 1].ParentIndex.ShouldBe(trees[1].ParticleStart);
        particles[trees[1].ParticleStart].BoneIndex.ShouldBe(trees[1].ParticleStart);

        WorldProperty.SetValue(componentNode, null);
        world.Invoke(ETickGroup.Normal);
    }

    private sealed class TestWorldContext : IRuntimeWorldContext
    {
        private readonly List<(ETickGroup Group, WorldTick Tick)> _ticks = [];
        public bool IsPlaySessionActive => false;
        public void RegisterTick(ETickGroup group, int order, WorldTick tick) => _ticks.Add((group, tick));
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) => _ticks.Remove((group, tick));
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
        public void Invoke(ETickGroup group)
        {
            for (int i = 0; i < _ticks.Count; ++i)
                if (_ticks[i].Group == group)
                    _ticks[i].Tick();
        }
    }
}
