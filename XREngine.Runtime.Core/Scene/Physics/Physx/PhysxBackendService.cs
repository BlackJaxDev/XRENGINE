using MagicPhysX;
using System.Numerics;
using XREngine.Components.Physics;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine.Scene.Physics.Physx;

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
    public PhysicsCharacterControllerCapabilities CharacterControllerCapabilities
        => PhysicsCharacterControllerCapabilities.DisplacementInput
            | PhysicsCharacterControllerCapabilities.VelocityInput
            | PhysicsCharacterControllerCapabilities.ArbitraryUp
            | PhysicsCharacterControllerCapabilities.MovingGround
            | PhysicsCharacterControllerCapabilities.DynamicBodyInteraction
            | PhysicsCharacterControllerCapabilities.CharacterVsCharacter
            | PhysicsCharacterControllerCapabilities.QueryVisibility
            | PhysicsCharacterControllerCapabilities.Materials
            | PhysicsCharacterControllerCapabilities.InvisibleWalls
            | PhysicsCharacterControllerCapabilities.ConstrainedClimbing
            | PhysicsCharacterControllerCapabilities.MaximumJumpHeight
            | PhysicsCharacterControllerCapabilities.ScaleCoefficient
            | PhysicsCharacterControllerCapabilities.VolumeGrowth
            | PhysicsCharacterControllerCapabilities.SteepSlopeSliding
            | PhysicsCharacterControllerCapabilities.CollisionFiltering;

    private sealed class CharacterControllerAdapter(
        PhysxCapsuleController controller,
        PhysxControllerActorProxy actorProxy) : IAbstractCharacterController, IPhysxCharacterControllerExtension, ICharacterControllerCollisionSettings
    {
        private CharacterMotionInputModel _motionInputModel = CharacterMotionInputModel.Velocity;
        private LayerMask _collisionLayerMask = LayerMask.Everything;

        public PhysxCapsuleController NativeController => controller;

        public Vector3 Position { get => controller.Position; set => controller.Position = value; }
        public Vector3 FootPosition { get => controller.FootPosition; set => controller.FootPosition = value; }
        public Vector3 UpDirection { get => controller.UpDirection; set => controller.UpDirection = value; }
        public float Radius
        {
            get => controller.Radius;
            set
            {
                Vector3 foot = controller.FootPosition;
                float totalHeight = TotalHeight;
                controller.Radius = MathF.Max(0.001f, value);
                controller.Height = MathF.Max(0.0f, totalHeight - 2.0f * controller.Radius);
                controller.FootPosition = foot;
            }
        }
        public float TotalHeight => controller.Height + 2.0f * controller.Radius;
        public float SlopeLimit { get => controller.SlopeLimit; set => controller.SlopeLimit = value; }
        public float StepOffset { get => controller.StepOffset; set => controller.StepOffset = value; }
        public float ContactOffset { get => controller.ContactOffset; set => controller.ContactOffset = value; }
        public CharacterMotionInputModel MotionInputModel
        {
            get => _motionInputModel;
            set => _motionInputModel = value;
        }
        public PhysicsCharacterControllerCapabilities Capabilities
            => PhysicsCharacterControllerCapabilities.DisplacementInput
                | PhysicsCharacterControllerCapabilities.VelocityInput
                | PhysicsCharacterControllerCapabilities.ArbitraryUp
                | PhysicsCharacterControllerCapabilities.MovingGround
                | PhysicsCharacterControllerCapabilities.DynamicBodyInteraction
                | PhysicsCharacterControllerCapabilities.CharacterVsCharacter
                | PhysicsCharacterControllerCapabilities.QueryVisibility
                | PhysicsCharacterControllerCapabilities.Materials
                | PhysicsCharacterControllerCapabilities.InvisibleWalls
                | PhysicsCharacterControllerCapabilities.ConstrainedClimbing
                | PhysicsCharacterControllerCapabilities.MaximumJumpHeight
                | PhysicsCharacterControllerCapabilities.ScaleCoefficient
                | PhysicsCharacterControllerCapabilities.VolumeGrowth
                | PhysicsCharacterControllerCapabilities.SteepSlopeSliding
                | PhysicsCharacterControllerCapabilities.CollisionFiltering;
        public LayerMask CollisionLayerMask
        {
            get => _collisionLayerMask;
            set
            {
                _collisionLayerMask = value;
                ConfigureControllerFiltering(controller, value);
            }
        }
        public bool SlideOnSteepSlopes
        {
            get => controller.NonWalkableMode == PxControllerNonWalkableMode.PreventClimbingAndForceSliding;
            set => controller.NonWalkableMode = value
                ? PxControllerNonWalkableMode.PreventClimbingAndForceSliding
                : PxControllerNonWalkableMode.PreventClimbing;
        }
        public CharacterSupportState SupportState => controller.SupportState;
        public bool IsGrounded => controller.IsGrounded;
        public bool CollidingUp => controller.CollidingUp;
        public bool CollidingDown => controller.CollidingDown;
        public bool CollidingSides => controller.CollidingSides;
        public Vector3 GroundNormal => controller.GroundNormal;
        public Vector3 GroundVelocity => controller.GroundVelocity;
        public IAbstractRigidPhysicsActor? GroundActor => controller.GroundActor;
        public CharacterMotionCommand LastMotionCommand => controller.LastMotionCommand;
        public Vector3 RequestedVelocity => controller.RequestedVelocity;
        public Vector3 EffectiveVelocity => controller.EffectiveVelocity;
        public Vector3 LinearVelocity => controller.EffectiveVelocity;
        public Vector3 AngularVelocity => actorProxy.AngularVelocity;
        public bool IsSleeping => actorProxy.IsSleeping;
        public (Vector3 position, Quaternion rotation) Transform => actorProxy.Transform;

        public void SubmitMotion(in CharacterMotionCommand command)
            => controller.SubmitMotion(command);

        public void Move(Vector3 value, float minDist, float elapsedTime)
            => SubmitMotion(new CharacterMotionCommand(
                value,
                MotionInputModel,
                minDist,
                elapsedTime));

        public void Resize(float totalHeight)
            => controller.Resize(MathF.Max(0.0f, totalHeight - 2.0f * controller.Radius));

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
        PhysicsCharacterControllerCreateInfoValidator.Validate(in createInfo);
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
                MathF.Max(0.0f, createInfo.TotalHeight - 2.0f * createInfo.Radius),
                createInfo.ConstrainedClimbing
                    ? PxCapsuleClimbingMode.Constrained
                    : PxCapsuleClimbingMode.Easy);
        }
        finally
        {
            material.Release();
        }
        var actorProxy = new PhysxControllerActorProxy(controller.ControllerPtr);
        return new CharacterControllerAdapter(controller, actorProxy)
        {
            MotionInputModel = createInfo.MotionInputModel,
            CollisionLayerMask = createInfo.CollisionLayerMask,
            SlideOnSteepSlopes = createInfo.SlideOnSteepSlopes,
        };
    }

    private static unsafe void ConfigureControllerFiltering(
        PhysxCapsuleController controller,
        LayerMask layerMask)
    {
        PxRigidActor* actor = (PxRigidActor*)controller.ControllerPtr->GetActor();
        if (actor is null)
            return;

        int shapeCount = (int)actor->GetNbShapes();
        if (shapeCount == 0)
            return;

        PxFilterData filterData = default;
        filterData.word0 = unchecked((uint)layerMask.Value);
        filterData.word1 = uint.MaxValue;

        PxShape** shapes = stackalloc PxShape*[(int)shapeCount];
        actor->GetShapes(shapes, (uint)shapeCount, 0);
        for (int index = 0; index < shapeCount; index++)
        {
            PxShape* shape = shapes[index];
            if (shape is null)
                continue;
            shape->SetSimulationFilterDataMut(&filterData);
            shape->SetQueryFilterDataMut(&filterData);
        }
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
