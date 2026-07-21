using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainTemplateInvariantTests
{
    [Test]
    public void Construction_CachesTopologyCoefficientsAndInfluenceBounds()
    {
        PhysicsChainTemplateParticle[] particles =
        [
            new(-1, 0, 0, 1, 0.0f, 0.0f, 0.0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.1f, Vector3.Zero, Quaternion.Identity),
            new(0, 1, 1, 0, 4.0f, 0.25f, 2.0f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 0.2f, new Vector3(0.0f, 2.0f, 0.0f), Quaternion.Identity),
        ];
        var template = new PhysicsChainTemplate(
            [new PhysicsChainTemplateTree(0, particles.Length, 1, 2.0f)],
            particles,
            [0, 1],
            [new(0, 0, 0, 1), new(0, 1, 1, 1)],
            freezeAxis: 1);

        template.Trees.Span[0].MaximumDepth.ShouldBe(1);
        template.DepthRanges.Span.SequenceEqual([new(0, 0, 0, 1), new(0, 1, 1, 1)]).ShouldBeTrue();
        template.Particles.Span[1].InverseSegmentLength.ShouldBe(0.25f);
        PhysicsChainCoefficientPack pack = template.CoefficientPacks.Span[1];
        pack.Damping.ShouldBe(0.6f);
        pack.Elasticity.ShouldBe(0.7f);
        pack.Stiffness.ShouldBe(0.8f);
        pack.Inertia.ShouldBe(0.9f);
        pack.Friction.ShouldBe(1.0f);
        pack.Radius.ShouldBe(0.2f);
        pack.SegmentLength.ShouldBe(4.0f);
        pack.InverseSegmentLength.ShouldBe(0.25f);
        template.InfluenceBounds.Span[0].Radius.ShouldBe(2.2f, 1e-6f);
        (template.FeatureMask & PhysicsChainTemplateFeatureMask.FreezeAxis).ShouldNotBe(PhysicsChainTemplateFeatureMask.None);
        (template.FeatureMask & PhysicsChainTemplateFeatureMask.Elasticity).ShouldNotBe(PhysicsChainTemplateFeatureMask.None);
    }
}
