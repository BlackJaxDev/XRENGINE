using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsTestWorldBuilderTests
{
    [Test]
    public void AddPlayground_BuildsEveryPhysicsCoverageZoneAndJointType()
    {
        SceneNode root = new("Root");

        SceneNode playground = BootstrapPhysicsTestWorldBuilder.AddPlayground(root, sphereCount: 4);
        SceneNode[] nodes = EnumerateHierarchy(playground).ToArray();
        var components = nodes.SelectMany(static node => node.Components).ToArray();

        playground.Name.ShouldBe("Physics Playground");
        nodes.ShouldContain(static node => node.Name == "Static Environment");
        nodes.ShouldContain(static node => node.Name == "Rigid Bodies and Collisions");
        nodes.ShouldContain(static node => node.Name == "Joints");
        nodes.ShouldContain(static node => node.Name == "Character Controller Course");

        components.OfType<StaticRigidBodyComponent>().Count().ShouldBeGreaterThanOrEqualTo(10);
        components.OfType<DynamicRigidBodyComponent>().Count().ShouldBeGreaterThanOrEqualTo(20);
        components.OfType<FixedJointComponent>().Count().ShouldBe(1);
        components.OfType<DistanceJointComponent>().Count().ShouldBe(1);
        components.OfType<HingeJointComponent>().Count().ShouldBe(1);
        components.OfType<PrismaticJointComponent>().Count().ShouldBe(2);
        components.OfType<SphericalJointComponent>().Count().ShouldBe(1);
        components.OfType<D6JointComponent>().Count().ShouldBe(1);

        nodes.Count(static node => node.Name?.StartsWith("Restitution Sphere ", StringComparison.Ordinal) == true)
            .ShouldBe(4);
        nodes.ShouldContain(static node => node.Name == "Dynamic Capsule");
        nodes.ShouldContain(static node => node.Name == "Compound Rigid Body");
        nodes.ShouldContain(static node => node.Name == "CCD Projectile");
        nodes.ShouldContain(static node => node.Name == "Walkable Ramp 25 Degrees");
        nodes.ShouldContain(static node => node.Name == "Steep Ramp 55 Degrees");
        nodes.ShouldContain(static node => node.Name == "Crouch Tunnel Ceiling");
        nodes.ShouldContain(static node => node.Name == "Moving Ground Candidate");
    }

    [Test]
    public void PhysicsTestingWorldKind_PreservesTheLegacyPhysxTestingSelectorAsAnAlias()
    {
        Enum.Parse<UnitTestWorldKind>("PhysicsTesting").ShouldBe(UnitTestWorldKind.PhysicsTesting);
        Enum.Parse<UnitTestWorldKind>("PhysxTesting").ShouldBe(UnitTestWorldKind.PhysicsTesting);
    }

    private static IEnumerable<SceneNode> EnumerateHierarchy(SceneNode node)
    {
        yield return node;
        foreach (TransformBase? childTransform in node.Transform.Children)
        {
            if (childTransform?.SceneNode is not SceneNode child)
                continue;

            foreach (SceneNode descendant in EnumerateHierarchy(child))
                yield return descendant;
        }
    }
}
