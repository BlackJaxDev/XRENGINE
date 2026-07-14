using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Networking;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsSceneSerializationTests
{
    [Test]
    public void FullSceneYamlRoundTrip_PreservesBodiesControllerJointReferenceLimitsAndAuthority()
    {
        SceneNode root = new("PhysicsRoot");
        SceneNode dynamicNode = new(root, "DynamicBody");
        SceneNode staticNode = new(root, "StaticBody");
        SceneNode controllerNode = new(root, "Controller");

        DynamicRigidBodyComponent dynamicBody = dynamicNode.AddComponent<DynamicRigidBodyComponent>()!;
        dynamicBody.AutoCreateRigidBody = false;
        dynamicBody.GravityEnabled = false;
        dynamicBody.SimulationEnabled = true;
        dynamicBody.CollisionGroup = 3;
        dynamicBody.GroupsMask = new PhysicsGroupsMask(0x25u, 2u, 3u, 4u);
        dynamicBody.BodyFlags = PhysicsRigidBodyFlags.EnableCcd;
        dynamicBody.LockFlags = PhysicsLockFlags.AngularX | PhysicsLockFlags.LinearZ;
        dynamicBody.LinearDamping = 0.17f;
        dynamicBody.AngularDamping = 0.29f;
        dynamicBody.MaxLinearVelocity = 42.0f;
        dynamicBody.MaxAngularVelocity = 23.0f;
        dynamicBody.Mass = 6.5f;
        dynamicBody.CenterOfMassLocalPose = new PhysicsMassFrame(
            new Vector3(0.1f, 0.2f, 0.3f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f));
        dynamicBody.SolverIterations = new PhysicsSolverIterations(11, 4);
        dynamicBody.ShapeOffsetTranslation = new Vector3(0.2f, 0.0f, -0.1f);
        dynamicBody.MaterialDefinition = new PhysicsMaterialDefinition
        {
            StaticFriction = 0.8f,
            DynamicFriction = 0.6f,
            Restitution = 0.35f,
            Damping = 0.05f,
        };
        dynamicBody.ColliderShapes =
        [
            new PhysicsColliderShape
            {
                Name = "Primary",
                Geometry = new IPhysicsGeometry.Sphere(0.75f),
                LocalPosition = new Vector3(0.25f, 0.0f, 0.0f),
            },
            new PhysicsColliderShape
            {
                Name = "Secondary",
                Geometry = new IPhysicsGeometry.Box(new Vector3(0.2f, 0.3f, 0.4f)),
                LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f),
            },
        ];
        dynamicBody.ReplicationAuthority = PhysicsReplicationAuthority.ClientAuthoritative;
        dynamicBody.NetworkEntityId = NetworkEntityId.FromGuid(Guid.NewGuid());
        dynamicBody.OwnerClientId = "physics-owner";
        dynamicBody.OwnerServerPlayerIndex = 12;

        StaticRigidBodyComponent staticBody = staticNode.AddComponent<StaticRigidBodyComponent>()!;
        staticBody.AutoCreateRigidBody = false;
        staticBody.Geometry = new IPhysicsGeometry.Box(new Vector3(2.0f, 0.5f, 2.0f));
        staticBody.CollisionGroup = 9;
        staticBody.ActorName = "serialized-ground";
        staticBody.ReplicationAuthority = PhysicsReplicationAuthority.ServerAuthoritative;

        CharacterControllerComponent controller = controllerNode.AddComponent<CharacterControllerComponent>()!;
        controller.Radius = 0.42f;
        controller.TotalHeight = 1.3f;
        controller.ContactOffset = 0.03f;
        controller.StepOffset = 0.27f;
        controller.SlopeLimit = 0.61f;
        controller.Density = 2.2f;
        controller.UpDirection = Vector3.UnitZ;
        controller.MotionInputModel = CharacterMotionInputModel.Displacement;
        controller.PredictiveContactDistance = 0.12f;
        controller.CollisionTolerance = 0.002f;
        controller.StickToFloorDistance = 0.16f;
        controller.StepDownExtra = 0.04f;
        controller.ReplicationAuthority = PhysicsReplicationAuthority.SharedDeterministic;

        D6JointComponent joint = dynamicNode.AddComponent<D6JointComponent>()!;
        joint.ConnectedBody = staticBody;
        joint.AutoConfigureConnectedAnchor = false;
        joint.AnchorPosition = new Vector3(0.1f, 0.2f, 0.3f);
        joint.ConnectedAnchorPosition = new Vector3(-0.4f, 0.5f, -0.6f);
        joint.BreakForce = 91.0f;
        joint.BreakTorque = 37.0f;
        joint.EnableCollision = true;
        joint.MotionX = JointMotion.Limited;
        joint.MotionY = JointMotion.Free;
        joint.MotionTwist = JointMotion.Limited;
        joint.TwistLowerRadians = -0.35f;
        joint.TwistUpperRadians = 0.65f;
        joint.SwingLimitYAngle = 0.45f;
        joint.SwingLimitZAngle = 0.55f;
        joint.DistanceLimitValue = 2.75f;
        joint.LinearLimitXLower = -1.25f;
        joint.LinearLimitXUpper = 1.75f;
        joint.DriveX = new JointDrive(20.0f, 3.0f, 100.0f, true);
        joint.DriveTargetPosition = new Vector3(1.0f, 2.0f, 3.0f);
        joint.ProjectionLinearTolerance = 0.08f;
        joint.ReplicationAuthority = PhysicsReplicationAuthority.ClientAuthoritative;
        joint.NetworkEntityId = NetworkEntityId.FromGuid(Guid.NewGuid());
        joint.OwnerClientId = "physics-owner";
        joint.OwnerServerPlayerIndex = 12;

        string yaml = AssetManager.Serializer.Serialize(root);
        SceneNode clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);

        SceneNode clonedDynamicNode = FindChild(clone, "DynamicBody");
        SceneNode clonedStaticNode = FindChild(clone, "StaticBody");
        SceneNode clonedControllerNode = FindChild(clone, "Controller");
        DynamicRigidBodyComponent clonedDynamic = clonedDynamicNode.GetComponent<DynamicRigidBodyComponent>()!;
        StaticRigidBodyComponent clonedStatic = clonedStaticNode.GetComponent<StaticRigidBodyComponent>()!;
        CharacterControllerComponent clonedController = clonedControllerNode.GetComponent<CharacterControllerComponent>()!;
        D6JointComponent clonedJoint = clonedDynamicNode.GetComponent<D6JointComponent>()!;

        clonedDynamic.ShouldNotBeNull();
        clonedStatic.ShouldNotBeNull();
        clonedController.ShouldNotBeNull();
        clonedJoint.ShouldNotBeNull();
        clonedDynamic.ColliderShapes.Count.ShouldBe(2);
        clonedDynamic.ColliderShapes[0].Geometry.ShouldBeOfType<IPhysicsGeometry.Sphere>();
        clonedDynamic.ColliderShapes[1].Geometry.ShouldBeOfType<IPhysicsGeometry.Box>();
        clonedDynamic.MaterialDefinition!.Restitution.ShouldBe(0.35f);
        clonedDynamic.BodyFlags.ShouldBe(PhysicsRigidBodyFlags.EnableCcd);
        clonedDynamic.LockFlags.ShouldBe(PhysicsLockFlags.AngularX | PhysicsLockFlags.LinearZ);
        clonedDynamic.SolverIterations.MinPositionIterations.ShouldBe(11u);
        clonedDynamic.SolverIterations.MinVelocityIterations.ShouldBe(4u);
        clonedDynamic.NetworkEntityId.ShouldBe(dynamicBody.NetworkEntityId);
        clonedDynamic.OwnerClientId.ShouldBe("physics-owner");
        clonedStatic.ActorName.ShouldBe("serialized-ground");
        clonedController.UpDirection.ShouldBe(Vector3.UnitZ);
        clonedController.TotalHeight.ShouldBe(1.3f);
        clonedController.StepOffset.ShouldBe(0.27f);
        clonedController.MotionInputModel.ShouldBe(CharacterMotionInputModel.Displacement);
        clonedController.PredictiveContactDistance.ShouldBe(0.12f);
        clonedController.CollisionTolerance.ShouldBe(0.002f);
        clonedController.StickToFloorDistance.ShouldBe(0.16f);
        clonedController.StepDownExtra.ShouldBe(0.04f);
        clonedController.ReplicationAuthority.ShouldBe(PhysicsReplicationAuthority.SharedDeterministic);

        clonedJoint.ConnectedBody.ShouldBeSameAs(clonedStatic);
        clonedJoint.MotionX.ShouldBe(JointMotion.Limited);
        clonedJoint.MotionY.ShouldBe(JointMotion.Free);
        clonedJoint.TwistLowerRadians.ShouldBe(-0.35f);
        clonedJoint.TwistUpperRadians.ShouldBe(0.65f);
        clonedJoint.LinearLimitXLower.ShouldBe(-1.25f);
        clonedJoint.LinearLimitXUpper.ShouldBe(1.75f);
        clonedJoint.DriveX.Stiffness.ShouldBe(20.0f);
        clonedJoint.DriveTargetPosition.ShouldBe(new Vector3(1.0f, 2.0f, 3.0f));
        clonedJoint.NetworkEntityId.ShouldBe(joint.NetworkEntityId);
    }

    [Test]
    public void EveryJointComponentType_CanRoundTripAsPartOfAScene()
    {
        SceneNode root = new("AllJointTypes");
        AddJointNode<FixedJointComponent>(root, "Fixed");
        AddJointNode<DistanceJointComponent>(root, "Distance");
        AddJointNode<HingeJointComponent>(root, "Hinge");
        AddJointNode<PrismaticJointComponent>(root, "Prismatic");
        AddJointNode<SphericalJointComponent>(root, "Spherical");
        AddJointNode<D6JointComponent>(root, "D6");

        string yaml = AssetManager.Serializer.Serialize(root);
        SceneNode clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);

        FindChild(clone, "Fixed").GetComponent<FixedJointComponent>().ShouldNotBeNull();
        FindChild(clone, "Distance").GetComponent<DistanceJointComponent>().ShouldNotBeNull();
        FindChild(clone, "Hinge").GetComponent<HingeJointComponent>().ShouldNotBeNull();
        FindChild(clone, "Prismatic").GetComponent<PrismaticJointComponent>().ShouldNotBeNull();
        FindChild(clone, "Spherical").GetComponent<SphericalJointComponent>().ShouldNotBeNull();
        FindChild(clone, "D6").GetComponent<D6JointComponent>().ShouldNotBeNull();
    }

    private static void AddJointNode<T>(SceneNode root, string name)
        where T : PhysicsJointComponent
    {
        SceneNode node = new(root, name);
        node.AddComponent<T>().ShouldNotBeNull();
    }

    private static SceneNode FindChild(SceneNode root, string name)
        => root.Transform.Children
            .Select(static transform => transform.SceneNode)
            .Single(node => node?.Name == name)!;
}
