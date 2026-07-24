using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;

namespace XREngine.UnitTests.Physics;

[TestFixture]
[NonParallelizable]
public sealed class PhysicsTestWorldBuilderTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

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

    [Test]
    public void AddPlayground_DisablesJointGizmosWithPhysicsDebugVisualization()
    {
        SceneNode root = new("Root");

        SceneNode playground = BootstrapPhysicsTestWorldBuilder.AddPlayground(
            root,
            sphereCount: 0,
            drawPhysicsGizmos: false);

        PhysicsJointComponent[] joints = EnumerateHierarchy(playground)
            .SelectMany(static node => node.Components)
            .OfType<PhysicsJointComponent>()
            .ToArray();

        joints.Length.ShouldBeGreaterThan(0);
        joints.ShouldAllBe(static joint => !joint.DrawGizmos);
    }

    [Test]
    public void AddPlayground_BatchesBoxFixtureVisualsAsLitOpaqueGeometry()
    {
        SceneNode root = new("Root");

        SceneNode playground = BootstrapPhysicsTestWorldBuilder.AddPlayground(root, sphereCount: 0);
        var components = EnumerateHierarchy(playground)
            .SelectMany(static node => node.Components)
            .ToArray();
        LitBoxBatchComponent boxBatch = components
            .OfType<LitBoxBatchComponent>()
            .ShouldHaveSingleItem();

        boxBatch.IsBuilt.ShouldBeTrue();
        boxBatch.BoxCount.ShouldBeGreaterThanOrEqualTo(20);
        components.OfType<ModelComponent>().Count().ShouldBe(5);
        DebugDrawComponent.DebugShapeBase[] debugShapes = components
            .OfType<DebugDrawComponent>()
            .SelectMany(static debug => debug.Shapes)
            .ToArray();
        debugShapes.ShouldNotContain(static shape => shape is DebugDrawComponent.DebugDrawBox);
        debugShapes.ShouldNotContain(static shape => shape is DebugDrawComponent.DebugDrawSphere);
        debugShapes.ShouldNotContain(static shape => shape is DebugDrawComponent.DebugDrawCapsule);
    }

    [Test]
    public void SceneSnapshot_RebuildsLitBoxBatchFromRestoredFixtureRegistrations()
    {
        SceneNode root = new("Root");
        SceneNode playground = BootstrapPhysicsTestWorldBuilder.AddPlayground(root, sphereCount: 0);
        LitBoxBatchComponent originalBatch = playground.GetComponent<LitBoxBatchComponent>()
            .ShouldNotBeNull();
        XRScene scene = new("Physics Snapshot Scene");
        scene.RootNodes.Add(root);

        byte[] payload = SnapshotBinarySerializer.Serialize(scene).ShouldNotBeNull();
        XRScene restoredScene = SnapshotBinarySerializer.Deserialize<XRScene>(payload)
            .ShouldNotBeNull();
        SceneNode[] restoredNodes = restoredScene.RootNodes
            .SelectMany(EnumerateHierarchy)
            .ToArray();
        LitBoxBatchComponent restoredBatch = restoredNodes
            .SelectMany(static node => node.Components)
            .OfType<LitBoxBatchComponent>()
            .ShouldHaveSingleItem();
        HashSet<Guid> restoredNodeIds = restoredNodes
            .Select(static node => node.ID)
            .ToHashSet();

        restoredBatch.BoxCount.ShouldBe(originalBatch.BoxCount);
        restoredBatch.IsBuilt.ShouldBeTrue();
        restoredBatch.Entries.ShouldAllBe(entry => restoredNodeIds.Contains(entry.SceneNodeId));
    }

    [Test]
    public void AddPlayground_RampCollidersMeetFloorWithoutPenetratingIt()
    {
        SceneNode root = new("Root");

        SceneNode playground = BootstrapPhysicsTestWorldBuilder.AddPlayground(root, sphereCount: 0);
        SceneNode[] nodes = EnumerateHierarchy(playground).ToArray();

        AssertBoxBottomTouchesFloor(
            nodes.Single(static node => node.Name == "Walkable Ramp 25 Degrees"));
        AssertBoxBottomTouchesFloor(
            nodes.Single(static node => node.Name == "Steep Ramp 55 Degrees"));
    }

    private static void AssertBoxBottomTouchesFloor(SceneNode node)
    {
        IPhysicsGeometry.Box geometry = node
            .GetComponent<StaticRigidBodyComponent>()!
            .Geometry
            .ShouldBeOfType<IPhysicsGeometry.Box>();
        Quaternion rotation = node.Transform.WorldRotation;
        Vector3 halfExtents = geometry.HalfExtents;
        float verticalHalfExtent =
            MathF.Abs(Vector3.Transform(Vector3.UnitX, rotation).Y) * halfExtents.X +
            MathF.Abs(Vector3.Transform(Vector3.UnitY, rotation).Y) * halfExtents.Y +
            MathF.Abs(Vector3.Transform(Vector3.UnitZ, rotation).Y) * halfExtents.Z;
        float bottom = node.Transform.WorldTranslation.Y - verticalHalfExtent;

        MathF.Abs(bottom).ShouldBeLessThan(0.0001f);
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
