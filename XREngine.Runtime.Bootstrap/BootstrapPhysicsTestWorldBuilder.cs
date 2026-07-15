using System.Numerics;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;

namespace XREngine.Runtime.Bootstrap.Builders;

/// <summary>
/// Builds a deterministic, backend-neutral playground for visual and interactive physics validation.
/// Stable zone and fixture names are intentional so MCP and automated tests can locate them.
/// </summary>
public static class BootstrapPhysicsTestWorldBuilder
{
    private const ushort CollisionGroup = 1;
    private static readonly PhysicsGroupsMask CollisionMask = new(0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF);

    public static SceneNode AddPlayground(SceneNode rootNode, int sphereCount = 10)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        SceneNode playground = rootNode.NewChild("Physics Playground");
        PhysicsMaterialDefinition defaultMaterial = CreateMaterial(0.6f, 0.5f, 0.1f);

        AddStaticEnvironment(playground, defaultMaterial);
        AddRigidBodyAndCollisionZone(playground, Math.Max(0, sphereCount));
        AddJointZone(playground, defaultMaterial);
        AddCharacterControllerCourse(playground, defaultMaterial);
        AddReferenceAxes(playground);
        return playground;
    }

    private static void AddStaticEnvironment(SceneNode playground, PhysicsMaterialDefinition material)
    {
        SceneNode zone = playground.NewChild("Static Environment");
        AddStaticBox(
            zone,
            "Physics Floor",
            new Vector3(30.0f, 0.5f, 30.0f),
            new Vector3(0.0f, -0.5f, 12.0f),
            Quaternion.Identity,
            material,
            ColorF4.Gray);

        AddStaticBox(
            zone,
            "Collision Backstop",
            new Vector3(0.25f, 2.5f, 4.0f),
            new Vector3(-5.0f, 2.5f, 22.0f),
            Quaternion.Identity,
            material,
            ColorF4.DarkGray);
    }

    private static void AddRigidBodyAndCollisionZone(SceneNode playground, int sphereCount)
    {
        SceneNode zone = playground.NewChild("Rigid Bodies and Collisions");
        PhysicsMaterialDefinition defaultMaterial = CreateMaterial(0.6f, 0.5f, 0.1f);

        for (int index = 0; index < 8; index++)
        {
            AddDynamicBox(
                zone,
                $"Box Stack {index:D2}",
                new Vector3(0.5f),
                new Vector3(-12.0f, 0.55f + index * 1.05f, 8.0f),
                Quaternion.Identity,
                defaultMaterial,
                ColorF4.LightGray);
        }

        Random random = new(7331);
        for (int index = 0; index < sphereCount; index++)
        {
            float restitution = sphereCount <= 1 ? 0.8f : index / (float)(sphereCount - 1);
            PhysicsMaterialDefinition material = CreateMaterial(0.25f, 0.2f, restitution);
            Vector3 position = new(
                -18.0f + index % 5 * 1.25f,
                3.0f + index / 5 * 1.25f,
                13.0f + random.NextSingle() * 0.2f);
            AddDynamicSphere(
                zone,
                $"Restitution Sphere {index:D2}",
                0.45f,
                position,
                material,
                new ColorF4(0.2f + restitution * 0.8f, 0.35f, 1.0f - restitution * 0.7f, 1.0f));
        }

        AddDynamicCapsule(
            zone,
            "Dynamic Capsule",
            radius: 0.45f,
            halfHeight: 0.8f,
            new Vector3(-10.0f, 5.0f, 14.0f),
            defaultMaterial,
            ColorF4.Orange);

        AddCompoundBody(zone, defaultMaterial);

        DynamicRigidBodyComponent heavyBody = AddDynamicBox(
            zone,
            "Heavy Rigid Body",
            new Vector3(1.1f),
            new Vector3(-14.5f, 4.5f, 18.0f),
            Quaternion.Identity,
            defaultMaterial,
            ColorF4.DarkGray,
            density: 12.0f);
        heavyBody.AngularDamping = 0.1f;

        DynamicRigidBodyComponent projectile = AddDynamicSphere(
            zone,
            "CCD Projectile",
            0.35f,
            new Vector3(-18.0f, 1.0f, 22.0f),
            CreateMaterial(0.1f, 0.1f, 0.2f),
            ColorF4.Red);
        projectile.BodyFlags |= PhysicsRigidBodyFlags.EnableCcd
            | PhysicsRigidBodyFlags.EnableSpeculativeCcd
            | PhysicsRigidBodyFlags.EnableCcdFriction;
        SetInitialVelocity(projectile.SceneNode, projectile, new Vector3(45.0f, 0.0f, 0.0f), Vector3.Zero);
    }

    private static void AddCompoundBody(SceneNode zone, PhysicsMaterialDefinition material)
    {
        SceneNode node = CreateRigidBodyNode(zone, "Compound Rigid Body", new Vector3(-8.0f, 4.0f, 18.0f), Quaternion.Identity);
        DynamicRigidBodyComponent body = node.AddComponent<DynamicRigidBodyComponent>()!;
        body.ColliderShapes =
        [
            new PhysicsColliderShape
            {
                Name = "Center Box",
                Geometry = new IPhysicsGeometry.Box(new Vector3(0.5f)),
                Material = material,
            },
            new PhysicsColliderShape
            {
                Name = "Left Sphere",
                Geometry = new IPhysicsGeometry.Sphere(0.45f),
                Material = material,
                LocalPosition = -Vector3.UnitX,
            },
            new PhysicsColliderShape
            {
                Name = "Right Sphere",
                Geometry = new IPhysicsGeometry.Sphere(0.45f),
                Material = material,
                LocalPosition = Vector3.UnitX,
            },
        ];
        ConfigureDynamicBody(body, density: 1.0f);

        AddBoxVisual(node, new Vector3(0.5f), ColorF4.LightGray);
        DebugDrawComponent debug = node.AddComponent<DebugDrawComponent>()!;
        debug.AddSphere(0.45f, -Vector3.UnitX, ColorF4.LightBlue, solid: true);
        debug.AddSphere(0.45f, Vector3.UnitX, ColorF4.LightBlue, solid: true);
    }

    private static void AddJointZone(SceneNode playground, PhysicsMaterialDefinition material)
    {
        SceneNode zone = playground.NewChild("Joints");
        AddFixedJointFixture(zone, material);
        AddDistanceJointFixture(zone, material);
        AddHingeJointFixture(zone, material);
        AddPrismaticJointFixture(zone, material);
        AddSphericalJointFixture(zone, material);
        AddD6JointFixture(zone, material);
    }

    private static void AddFixedJointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        DynamicRigidBodyComponent lower = AddDynamicBox(
            zone,
            "Fixed Joint Body A",
            new Vector3(0.45f),
            new Vector3(8.0f, 3.0f, 5.0f),
            Quaternion.Identity,
            material,
            ColorF4.LightBlue);
        DynamicRigidBodyComponent upper = AddDynamicBox(
            zone,
            "Fixed Joint Body B",
            new Vector3(0.45f),
            new Vector3(8.0f, 4.0f, 5.0f),
            Quaternion.Identity,
            material,
            ColorF4.Blue);

        FixedJointComponent joint = lower.SceneNode.AddComponent<FixedJointComponent>()!;
        joint.ConnectedBody = upper;
        joint.AutoConfigureConnectedAnchor = false;
        joint.AnchorPosition = Vector3.UnitY * 0.5f;
        joint.ConnectedAnchorPosition = -Vector3.UnitY * 0.5f;
    }

    private static void AddDistanceJointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        DynamicRigidBodyComponent bodyA = AddDynamicSphere(
            zone,
            "Distance Joint Body A",
            0.45f,
            new Vector3(11.0f, 5.0f, 5.0f),
            material,
            ColorF4.Cyan);
        DynamicRigidBodyComponent bodyB = AddDynamicSphere(
            zone,
            "Distance Joint Body B",
            0.45f,
            new Vector3(11.0f, 2.0f, 5.0f),
            material,
            ColorF4.LightBlue);

        DistanceJointComponent joint = bodyA.SceneNode.AddComponent<DistanceJointComponent>()!;
        joint.ConnectedBody = bodyB;
        joint.AutoConfigureConnectedAnchor = false;
        joint.EnableMinDistance = true;
        joint.EnableMaxDistance = true;
        joint.MinDistance = 2.5f;
        joint.MaxDistance = 3.5f;
        joint.Stiffness = 40.0f;
        joint.Damping = 4.0f;
    }

    private static void AddHingeJointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        Vector3 position = new(14.0f, 3.25f, 5.0f);
        DynamicRigidBodyComponent body = AddDynamicBox(
            zone,
            "Hinge Joint Pendulum",
            new Vector3(0.25f, 1.25f, 0.25f),
            position,
            Quaternion.Identity,
            material,
            ColorF4.Orange);

        HingeJointComponent joint = body.SceneNode.AddComponent<HingeJointComponent>()!;
        ConfigureWorldJoint(joint, Vector3.UnitY * 1.25f, position + Vector3.UnitY * 1.25f);
        joint.EnableLimit = true;
        joint.LowerAngleRadians = -MathF.PI * 0.6f;
        joint.UpperAngleRadians = MathF.PI * 0.6f;
        joint.EnableDrive = true;
        joint.DriveVelocity = 0.75f;
        joint.DriveForceLimit = 20.0f;
    }

    private static void AddPrismaticJointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        Vector3 position = new(8.0f, 3.0f, 11.0f);
        DynamicRigidBodyComponent body = AddDynamicBox(
            zone,
            "Prismatic Joint Slider",
            new Vector3(0.6f, 0.35f, 0.6f),
            position,
            Quaternion.Identity,
            material,
            ColorF4.Green);
        body.GravityEnabled = false;

        PrismaticJointComponent joint = body.SceneNode.AddComponent<PrismaticJointComponent>()!;
        ConfigureWorldJoint(joint, Vector3.Zero, position);
        joint.EnableLimit = true;
        joint.LowerLimit = -2.0f;
        joint.UpperLimit = 2.0f;
        joint.LimitRestitution = 0.8f;
        SetInitialVelocity(body.SceneNode, body, Vector3.UnitX * 2.0f, Vector3.Zero);
    }

    private static void AddSphericalJointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        Vector3 position = new(11.0f, 3.25f, 11.0f);
        DynamicRigidBodyComponent body = AddDynamicBox(
            zone,
            "Spherical Joint Pendulum",
            new Vector3(0.3f, 1.25f, 0.3f),
            position,
            Quaternion.Identity,
            material,
            ColorF4.LightGold);

        SphericalJointComponent joint = body.SceneNode.AddComponent<SphericalJointComponent>()!;
        ConfigureWorldJoint(joint, Vector3.UnitY * 1.25f, position + Vector3.UnitY * 1.25f);
        joint.EnableLimitCone = true;
        joint.LimitConeYAngleRadians = MathF.PI / 3.0f;
        joint.LimitConeZAngleRadians = MathF.PI / 4.0f;
    }

    private static void AddD6JointFixture(SceneNode zone, PhysicsMaterialDefinition material)
    {
        Vector3 position = new(14.0f, 3.0f, 11.0f);
        DynamicRigidBodyComponent body = AddDynamicBox(
            zone,
            "D6 Joint Body",
            new Vector3(0.6f),
            position,
            Quaternion.Identity,
            material,
            ColorF4.LightGreen);
        body.GravityEnabled = false;

        D6JointComponent joint = body.SceneNode.AddComponent<D6JointComponent>()!;
        ConfigureWorldJoint(joint, Vector3.Zero, position);
        joint.MotionX = JointMotion.Limited;
        joint.MotionY = JointMotion.Locked;
        joint.MotionZ = JointMotion.Locked;
        joint.LinearLimitXLower = -1.5f;
        joint.LinearLimitXUpper = 1.5f;
        joint.MotionTwist = JointMotion.Limited;
        joint.TwistLowerRadians = -MathF.PI / 4.0f;
        joint.TwistUpperRadians = MathF.PI / 4.0f;
        joint.DriveX = new JointDrive(20.0f, 4.0f, 100.0f);
        joint.DriveTargetPosition = Vector3.UnitX;
    }

    private static void AddCharacterControllerCourse(SceneNode playground, PhysicsMaterialDefinition material)
    {
        SceneNode zone = playground.NewChild("Character Controller Course");

        float[] stepHeights = [0.15f, 0.3f, 0.45f, 0.6f];
        for (int index = 0; index < stepHeights.Length; index++)
        {
            float height = stepHeights[index];
            AddStaticBox(
                zone,
                $"Controller Step {index + 1}",
                new Vector3(1.25f, height * 0.5f, 0.6f),
                new Vector3(0.0f, height * 0.5f, 4.0f + index * 1.2f),
                Quaternion.Identity,
                material,
                ColorF4.LightGray);
        }

        AddStaticBox(
            zone,
            "Walkable Ramp 25 Degrees",
            new Vector3(1.5f, 0.2f, 3.0f),
            new Vector3(0.0f, 1.2f, 10.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-25.0f)),
            material,
            ColorF4.LightGreen);
        AddStaticBox(
            zone,
            "Steep Ramp 55 Degrees",
            new Vector3(1.5f, 0.2f, 3.0f),
            new Vector3(4.0f, 1.8f, 10.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, XRMath.DegToRad(-55.0f)),
            material,
            ColorF4.Red);
        AddStaticBox(
            zone,
            "Controller Side Wall",
            new Vector3(0.25f, 1.5f, 2.5f),
            new Vector3(-3.0f, 1.5f, 10.5f),
            Quaternion.Identity,
            material,
            ColorF4.DarkGray);
        AddStaticBox(
            zone,
            "Crouch Tunnel Ceiling",
            new Vector3(1.5f, 0.15f, 2.0f),
            new Vector3(-4.0f, 1.35f, 16.0f),
            Quaternion.Identity,
            material,
            ColorF4.Orange);

        DynamicRigidBodyComponent movingGround = AddDynamicBox(
            zone,
            "Moving Ground Candidate",
            new Vector3(1.5f, 0.15f, 1.5f),
            new Vector3(2.5f, 1.0f, 17.0f),
            Quaternion.Identity,
            material,
            ColorF4.Cyan,
            density: 20.0f);
        movingGround.GravityEnabled = false;
        PrismaticJointComponent platformJoint = movingGround.SceneNode.AddComponent<PrismaticJointComponent>()!;
        ConfigureWorldJoint(platformJoint, Vector3.Zero, movingGround.Transform.WorldTranslation);
        platformJoint.EnableLimit = true;
        platformJoint.LowerLimit = -2.0f;
        platformJoint.UpperLimit = 2.0f;
        platformJoint.LimitRestitution = 1.0f;
        SetInitialVelocity(movingGround.SceneNode, movingGround, Vector3.UnitX, Vector3.Zero);
    }

    private static DynamicRigidBodyComponent AddDynamicBox(
        SceneNode parent,
        string name,
        Vector3 halfExtents,
        Vector3 position,
        Quaternion rotation,
        PhysicsMaterialDefinition material,
        ColorF4 color,
        float density = 1.0f)
    {
        SceneNode node = CreateRigidBodyNode(parent, name, position, rotation);
        DynamicRigidBodyComponent body = node.AddComponent<DynamicRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Box(halfExtents);
        body.MaterialDefinition = material;
        ConfigureDynamicBody(body, density);
        AddBoxVisual(node, halfExtents, color);
        return body;
    }

    private static DynamicRigidBodyComponent AddDynamicSphere(
        SceneNode parent,
        string name,
        float radius,
        Vector3 position,
        PhysicsMaterialDefinition material,
        ColorF4 color)
    {
        SceneNode node = CreateRigidBodyNode(parent, name, position, Quaternion.Identity);
        DynamicRigidBodyComponent body = node.AddComponent<DynamicRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Sphere(radius);
        body.MaterialDefinition = material;
        ConfigureDynamicBody(body, density: 1.0f);
        AddSphereVisual(node, radius, color);
        return body;
    }

    private static DynamicRigidBodyComponent AddDynamicCapsule(
        SceneNode parent,
        string name,
        float radius,
        float halfHeight,
        Vector3 position,
        PhysicsMaterialDefinition material,
        ColorF4 color)
    {
        SceneNode node = CreateRigidBodyNode(parent, name, position, Quaternion.Identity);
        DynamicRigidBodyComponent body = node.AddComponent<DynamicRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Capsule(radius, halfHeight);
        body.MaterialDefinition = material;
        ConfigureDynamicBody(body, density: 1.0f);

        DebugDrawComponent debug = node.AddComponent<DebugDrawComponent>()!;
        debug.AddCapsule(radius, -Vector3.UnitY * halfHeight, Vector3.UnitY * halfHeight, color, solid: true);
        return body;
    }

    private static StaticRigidBodyComponent AddStaticBox(
        SceneNode parent,
        string name,
        Vector3 halfExtents,
        Vector3 position,
        Quaternion rotation,
        PhysicsMaterialDefinition material,
        ColorF4 color)
    {
        SceneNode node = CreateRigidBodyNode(parent, name, position, rotation);
        StaticRigidBodyComponent body = node.AddComponent<StaticRigidBodyComponent>()!;
        body.Geometry = new IPhysicsGeometry.Box(halfExtents);
        body.MaterialDefinition = material;
        body.CollisionGroup = CollisionGroup;
        body.GroupsMask = CollisionMask;
        AddBoxVisual(node, halfExtents, color);
        return body;
    }

    private static SceneNode CreateRigidBodyNode(SceneNode parent, string name, Vector3 position, Quaternion rotation)
    {
        SceneNode node = new(parent) { Name = name };
        RigidBodyTransform transform = node.SetTransform<RigidBodyTransform>();
        transform.InterpolationMode = EInterpolationMode.Interpolate;
        transform.SetPositionAndRotation(position, rotation);
        return node;
    }

    private static void ConfigureDynamicBody(DynamicRigidBodyComponent body, float density)
    {
        body.Density = density;
        body.LinearDamping = 0.05f;
        body.AngularDamping = 0.05f;
        body.BodyFlags |= PhysicsRigidBodyFlags.EnableCcd | PhysicsRigidBodyFlags.EnableSpeculativeCcd;
        body.CollisionGroup = CollisionGroup;
        body.GroupsMask = CollisionMask;
    }

    private static void ConfigureWorldJoint(PhysicsJointComponent joint, Vector3 localAnchor, Vector3 worldAnchor)
    {
        joint.AutoConfigureConnectedAnchor = false;
        joint.AnchorPosition = localAnchor;
        joint.ConnectedAnchorPosition = worldAnchor;
    }

    private static void SetInitialVelocity(
        SceneNode node,
        DynamicRigidBodyComponent body,
        Vector3 linearVelocity,
        Vector3 angularVelocity)
    {
        void Apply(SceneNode activatedNode)
        {
            body.LinearVelocity = linearVelocity;
            body.AngularVelocity = angularVelocity;
            activatedNode.Activated -= Apply;
        }

        node.Activated += Apply;
    }

    private static PhysicsMaterialDefinition CreateMaterial(float staticFriction, float dynamicFriction, float restitution)
        => new()
        {
            StaticFriction = staticFriction,
            DynamicFriction = dynamicFriction,
            Restitution = restitution,
        };

    private static void AddBoxVisual(SceneNode node, Vector3 halfExtents, ColorF4 color)
    {
        XRMaterial material = XRMaterial.CreateLitColorMaterial(color);
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Model = new Model([new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, halfExtents * 2.0f), material)]);
    }

    private static void AddSphereVisual(SceneNode node, float radius, ColorF4 color)
    {
        XRMaterial material = XRMaterial.CreateLitColorMaterial(color);
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, radius, 24), material)]);
    }

    private static void AddReferenceAxes(SceneNode playground)
    {
        SceneNode node = playground.NewChild("Reference Axes");
        DebugDrawComponent debug = node.AddComponent<DebugDrawComponent>()!;
        debug.AddLine(Vector3.Zero, Vector3.UnitX * 3.0f, ColorF4.Red);
        debug.AddLine(Vector3.Zero, Vector3.UnitY * 3.0f, ColorF4.Green);
        debug.AddLine(Vector3.Zero, Vector3.UnitZ * 3.0f, ColorF4.Blue);
    }
}
