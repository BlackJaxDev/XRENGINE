using NUnit.Framework;
using Shouldly;
using XREngine.Scene;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainTemplateFeatureTests
{
    [Test]
    public void TemplatePrecomputesKernelFeaturesAndConservativeTreeInfluence()
    {
        var world = new TestWorldContext();
        var node = new SceneNode();
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        component.World = world;
        world.Run(ETickGroup.PostPhysics);
        PhysicsChainWorld.TryGet(world, out PhysicsChainWorld? scheduler).ShouldBeTrue();
        PhysicsChainWorld activeScheduler = scheduler!;

        activeScheduler.TryGetOrCreateTemplate(component.RuntimeHandle, out PhysicsChainTemplate? template).ShouldBeTrue();
        template.ShouldNotBeNull();
        template!.InfluenceBounds.Length.ShouldBe(template.Trees.Length);
        for (int i = 0; i < template.InfluenceBounds.Length; ++i)
            template.InfluenceBounds.Span[i].IsValid.ShouldBeTrue();
        (template.FeatureMask & PhysicsChainTemplateFeatureMask.BranchedTopology)
            .ShouldBe(PhysicsChainTemplateFeatureMask.None);
    }
}
