using MagicPhysX;
using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Scene.Physics;

namespace XREngine.Rendering.Physics.Physx;

internal interface IPhysxCharacterControllerExtension
{
    PhysxCapsuleController NativeController { get; }
}

/// <summary>
/// Explicit PhysX construction adapter. Native PhysX types do not cross the
/// <see cref="IPhysicsBackendService"/> boundary.
/// </summary>
internal sealed class PhysxBackendService(PhysxScene scene) : IPhysicsBackendService
{
    private sealed class CharacterControllerAdapter(
        PhysxCapsuleController controller,
        PhysxControllerActorProxy actorProxy) : IAbstractCharacterController, IPhysxCharacterControllerExtension
    {
        public PhysxCapsuleController NativeController => controller;

        public Vector3 Position { get => controller.Position; set => controller.Position = value; }
        public Vector3 FootPosition { get => controller.FootPosition; set => controller.FootPosition = value; }
        public Vector3 UpDirection { get => controller.UpDirection; set => controller.UpDirection = value; }
        public float Radius { get => controller.Radius; set => controller.Radius = value; }
        public float Height => controller.Height;
        public float SlopeLimit { get => controller.SlopeLimit; set => controller.SlopeLimit = value; }
        public float StepOffset { get => controller.StepOffset; set => controller.StepOffset = value; }
        public float ContactOffset { get => controller.ContactOffset; set => controller.ContactOffset = value; }
        public bool CollidingUp => controller.CollidingUp;
        public bool CollidingDown => controller.CollidingDown;
        public Vector3 LinearVelocity => actorProxy.LinearVelocity;
        public Vector3 AngularVelocity => actorProxy.AngularVelocity;
        public bool IsSleeping => actorProxy.IsSleeping;
        public (Vector3 position, Quaternion rotation) Transform => actorProxy.Transform;

        public void Move(Vector3 delta, float minDist, float elapsedTime)
            => controller.Move(delta, minDist, elapsedTime);

        public void Resize(float height)
            => controller.Resize(height);

        public void Synchronize()
            => actorProxy.RefreshFromNative();

        public void Destroy(bool wakeOnLostTouch = false)
            => RequestRelease();

        public void RequestRelease()
            => controller.RequestRelease();
    }

    public IAbstractStaticRigidBody? CreateStaticRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
    {
        PhysicsColliderShape? primaryShape = ResolvePrimaryShape(createInfo.ColliderShapes);
        IPhysicsGeometry? geometry = primaryShape?.Geometry ?? createInfo.FallbackGeometry;
        PhysxStaticRigidBody actor;

        if (geometry is null)
        {
            actor = new PhysxStaticRigidBody(createInfo.Pose.position, createInfo.Pose.rotation);
        }
        else
        {
            PhysxMaterial material = ResolveMaterial(
                createInfo.RuntimeMaterial,
                primaryShape?.Material ?? createInfo.MaterialDefinition);
            try
            {
                actor = new PhysxStaticRigidBody(
                    material,
                    geometry,
                    createInfo.Pose.position,
                    createInfo.Pose.rotation,
                    primaryShape?.LocalPosition ?? createInfo.FallbackShapeOffsetTranslation,
                    primaryShape?.LocalRotation ?? createInfo.FallbackShapeOffsetRotation);
            }
            finally
            {
                ReleaseTemporaryMaterial(createInfo.RuntimeMaterial, material);
            }
        }

        AttachAdditionalShapes(actor, createInfo, primaryShape);
        actor.GravityEnabled = createInfo.GravityEnabled;
        return actor;
    }

    public IAbstractDynamicRigidBody? CreateDynamicRigidBody(in PhysicsRigidBodyCreateInfo createInfo)
    {
        PhysicsColliderShape? primaryShape = ResolvePrimaryShape(createInfo.ColliderShapes);
        IPhysicsGeometry? geometry = primaryShape?.Geometry ?? createInfo.FallbackGeometry;
        PhysxDynamicRigidBody actor;

        if (geometry is null)
        {
            actor = new PhysxDynamicRigidBody(createInfo.Pose.position, createInfo.Pose.rotation);
        }
        else
        {
            PhysxMaterial material = ResolveMaterial(
                createInfo.RuntimeMaterial,
                primaryShape?.Material ?? createInfo.MaterialDefinition);
            try
            {
                actor = new PhysxDynamicRigidBody(
                    material,
                    geometry,
                    createInfo.Density,
                    createInfo.Pose.position,
                    createInfo.Pose.rotation,
                    primaryShape?.LocalPosition ?? createInfo.FallbackShapeOffsetTranslation,
                    primaryShape?.LocalRotation ?? createInfo.FallbackShapeOffsetRotation);
            }
            finally
            {
                ReleaseTemporaryMaterial(createInfo.RuntimeMaterial, material);
            }
        }

        AttachAdditionalShapes(actor, createInfo, primaryShape);
        actor.GravityEnabled = createInfo.GravityEnabled;
        actor.MaxLinearVelocity = createInfo.MaxLinearVelocity;
        actor.MaxAngularVelocity = createInfo.MaxAngularVelocity;
        actor.SolverIterationCounts = (
            createInfo.SolverIterations.MinPositionIterations,
            createInfo.SolverIterations.MinVelocityIterations);
        actor.Flags = ToPhysxRigidBodyFlags(createInfo.BodyFlags);
        actor.LockFlags = ToPhysxLockFlags(createInfo.LockFlags);
        return actor;
    }

    public unsafe IAbstractCharacterController? CreateCharacterController(
        in PhysicsCharacterControllerCreateInfo createInfo)
    {
        PhysxMaterial material = ResolveMaterial(null, createInfo.MaterialDefinition);
        ControllerManager manager = scene.GetOrCreateControllerManager();
        PhysxCapsuleController controller;
        try
        {
            controller = manager.CreateCapsuleController(
                createInfo.Position,
                createInfo.UpDirection,
                createInfo.SlopeLimit,
                createInfo.InvisibleWallHeight,
                createInfo.MaxJumpHeight,
                createInfo.ContactOffset,
                createInfo.StepOffset,
                createInfo.Density,
                createInfo.ScaleCoefficient,
                createInfo.VolumeGrowth,
                createInfo.SlideOnSteepSlopes
                    ? PxControllerNonWalkableMode.PreventClimbingAndForceSliding
                    : PxControllerNonWalkableMode.PreventClimbing,
                material,
                0,
                null,
                createInfo.Radius,
                createInfo.Height,
                createInfo.ConstrainedClimbing
                    ? PxCapsuleClimbingMode.Constrained
                    : PxCapsuleClimbingMode.Easy);
        }
        finally
        {
            material.Release();
        }
        var actorProxy = new PhysxControllerActorProxy(controller.ControllerPtr);
        return new CharacterControllerAdapter(controller, actorProxy);
    }

    public unsafe bool TryReplaceCollisionShapes(
        IAbstractRigidPhysicsActor actor,
        in PhysicsRigidBodyCreateInfo createInfo)
    {
        if (actor is not PhysxRigidActor physxActor || physxActor.IsReleased)
            return false;

        PhysxShape[] previousShapes = physxActor.GetShapes();
        List<PhysxShape> replacementShapes;
        try
        {
            replacementShapes = CreateReplacementShapes(createInfo);
        }
        catch
        {
            return false;
        }

        int attachedCount = 0;
        try
        {
            for (; attachedCount < replacementShapes.Count; attachedCount++)
                physxActor.AttachShape(replacementShapes[attachedCount]);
        }
        catch
        {
            for (int index = 0; index < attachedCount; index++)
                physxActor.DetachShape(replacementShapes[index], wakeOnLostTouch: false);
            for (int index = 0; index < replacementShapes.Count; index++)
                replacementShapes[index].Release();
            return false;
        }

        for (int index = 0; index < previousShapes.Length; index++)
        {
            PhysxShape previousShape = previousShapes[index];
            physxActor.DetachShape(previousShape, wakeOnLostTouch: false);
            previousShape.Release();
        }

        if (physxActor is PhysxDynamicRigidBody dynamicActor)
        {
            NativeMethods.PxRigidBodyExt_updateMassAndInertia_1(
                dynamicActor.BodyPtr,
                createInfo.Density,
                null,
                false);
        }

        physxActor.RefreshShapeFilterData();
        return true;
    }

    private static List<PhysxShape> CreateReplacementShapes(
        in PhysicsRigidBodyCreateInfo createInfo)
    {
        const PxShapeFlags flags = PxShapeFlags.SimulationShape
            | PxShapeFlags.SceneQueryShape
            | PxShapeFlags.Visualization;
        List<PhysxShape> shapes = [];
        PhysicsColliderShape? primaryShape = ResolvePrimaryShape(createInfo.ColliderShapes);

        try
        {
            IReadOnlyList<PhysicsColliderShape> authoredShapes = createInfo.ColliderShapes;
            for (int index = 0; index < authoredShapes.Count; index++)
            {
                PhysicsColliderShape shapeEntry = authoredShapes[index];
                if (!shapeEntry.Enabled || shapeEntry.Geometry is null)
                    continue;

                AbstractPhysicsMaterial? runtimeMaterial = ReferenceEquals(shapeEntry, primaryShape)
                    ? createInfo.RuntimeMaterial
                    : null;
                shapes.Add(CreateShape(
                    shapeEntry.Geometry,
                    runtimeMaterial,
                    shapeEntry.Material ?? createInfo.MaterialDefinition,
                    shapeEntry.LocalPosition,
                    shapeEntry.LocalRotation,
                    flags));
            }

            if (shapes.Count == 0 && createInfo.FallbackGeometry is { } fallbackGeometry)
            {
                shapes.Add(CreateShape(
                    fallbackGeometry,
                    createInfo.RuntimeMaterial,
                    createInfo.MaterialDefinition,
                    createInfo.FallbackShapeOffsetTranslation,
                    createInfo.FallbackShapeOffsetRotation,
                    flags));
            }

            return shapes;
        }
        catch
        {
            for (int index = 0; index < shapes.Count; index++)
                shapes[index].Release();
            throw;
        }
    }

    private static PhysicsColliderShape? ResolvePrimaryShape(IReadOnlyList<PhysicsColliderShape> shapes)
    {
        for (int i = 0; i < shapes.Count; i++)
        {
            PhysicsColliderShape shape = shapes[i];
            if (shape.Enabled && shape.Geometry is not null)
                return shape;
        }

        return null;
    }

    private static PhysxMaterial ResolveMaterial(
        AbstractPhysicsMaterial? runtimeMaterial,
        PhysicsMaterialDefinition? definition)
    {
        if (runtimeMaterial is PhysxMaterial physxMaterial && definition is null)
            return physxMaterial;

        return definition is null
            ? new PhysxMaterial(0.5f, 0.5f, 0.1f)
            : new PhysxMaterial(
                definition.StaticFriction,
                definition.DynamicFriction,
                definition.Restitution)
            {
                Damping = definition.Damping,
            };
    }

    private static void ReleaseTemporaryMaterial(
        AbstractPhysicsMaterial? runtimeMaterial,
        PhysxMaterial resolvedMaterial)
    {
        if (!ReferenceEquals(runtimeMaterial, resolvedMaterial))
            resolvedMaterial.Release();
    }

    private static PhysxShape CreateShape(
        IPhysicsGeometry geometry,
        AbstractPhysicsMaterial? runtimeMaterial,
        PhysicsMaterialDefinition? definition,
        Vector3 localPosition,
        Quaternion localRotation,
        PxShapeFlags flags)
    {
        PhysxMaterial material = ResolveMaterial(runtimeMaterial, definition);
        try
        {
            return new PhysxShape(geometry, material, flags, isExclusive: true)
            {
                LocalPose = (localPosition, localRotation),
            };
        }
        finally
        {
            ReleaseTemporaryMaterial(runtimeMaterial, material);
        }
    }

    private static void AttachAdditionalShapes(
        PhysxRigidActor actor,
        in PhysicsRigidBodyCreateInfo createInfo,
        PhysicsColliderShape? primaryShape)
    {
        IReadOnlyList<PhysicsColliderShape> shapes = createInfo.ColliderShapes;
        for (int i = 0; i < shapes.Count; i++)
        {
            PhysicsColliderShape shapeEntry = shapes[i];
            if (!shapeEntry.Enabled || shapeEntry.Geometry is null || ReferenceEquals(shapeEntry, primaryShape))
                continue;

            var shape = CreateShape(
                shapeEntry.Geometry,
                null,
                shapeEntry.Material ?? createInfo.MaterialDefinition,
                shapeEntry.LocalPosition,
                shapeEntry.LocalRotation,
                PxShapeFlags.SimulationShape | PxShapeFlags.SceneQueryShape | PxShapeFlags.Visualization);
            actor.AttachShape(shape);
        }
    }

    private static PxRigidBodyFlags ToPhysxRigidBodyFlags(PhysicsRigidBodyFlags flags)
    {
        PxRigidBodyFlags converted = 0;
        if (flags.HasFlag(PhysicsRigidBodyFlags.Kinematic))
            converted |= PxRigidBodyFlags.Kinematic;
        if (flags.HasFlag(PhysicsRigidBodyFlags.UseKinematicTargetForQueries))
            converted |= PxRigidBodyFlags.UseKinematicTargetForSceneQueries;
        if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcd))
            converted |= PxRigidBodyFlags.EnableCcd;
        if (flags.HasFlag(PhysicsRigidBodyFlags.EnableSpeculativeCcd))
            converted |= PxRigidBodyFlags.EnableSpeculativeCcd;
        if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcdMaxContactImpulse))
            converted |= PxRigidBodyFlags.EnableCcdMaxContactImpulse;
        if (flags.HasFlag(PhysicsRigidBodyFlags.EnableCcdFriction))
            converted |= PxRigidBodyFlags.EnableCcdFriction;
        return converted;
    }

    private static PxRigidDynamicLockFlags ToPhysxLockFlags(PhysicsLockFlags flags)
    {
        PxRigidDynamicLockFlags converted = 0;
        if (flags.HasFlag(PhysicsLockFlags.LinearX))
            converted |= PxRigidDynamicLockFlags.LockLinearX;
        if (flags.HasFlag(PhysicsLockFlags.LinearY))
            converted |= PxRigidDynamicLockFlags.LockLinearY;
        if (flags.HasFlag(PhysicsLockFlags.LinearZ))
            converted |= PxRigidDynamicLockFlags.LockLinearZ;
        if (flags.HasFlag(PhysicsLockFlags.AngularX))
            converted |= PxRigidDynamicLockFlags.LockAngularX;
        if (flags.HasFlag(PhysicsLockFlags.AngularY))
            converted |= PxRigidDynamicLockFlags.LockAngularY;
        if (flags.HasFlag(PhysicsLockFlags.AngularZ))
            converted |= PxRigidDynamicLockFlags.LockAngularZ;
        return converted;
    }
}
