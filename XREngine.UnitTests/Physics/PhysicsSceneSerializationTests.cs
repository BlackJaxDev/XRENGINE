using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Movement;
using XREngine.Components.Physics;
using XREngine.Core.Files;
using XREngine.Networking;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap.Builders;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;
using XREngine.Scene.Physics.Physx;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsSceneSerializationTests
{
    [Test]
    [NonParallelizable]
    public void PhysxControllerManager_IsRecreatedAfterSceneDestroy()
    {
        _ = Engine.Profiler;
        IRuntimePhysicsServices previousPhysicsServices = RuntimePhysicsServices.Current;
        RuntimePhysicsServices.Current = new FixedStepRuntimePhysicsServices();
        var scene = new PhysxScene();

        try
        {
            ControllerManager? firstManager = null;
            ControllerManager? secondManager = null;
            IAbstractCharacterController? firstController = null;
            IAbstractCharacterController? secondController = null;

            Engine.InvokePhysicsThreadTask(() =>
            {
                scene.Initialize();
                firstController = CreateTestCharacterController(scene);
                firstManager = scene.GetExistingControllerManager();

                scene.Destroy();
                scene.GetExistingControllerManager().ShouldBeNull();

                scene.Initialize();
                secondController = CreateTestCharacterController(scene);
                secondManager = scene.GetExistingControllerManager();
            });

            firstController.ShouldNotBeNull();
            secondController.ShouldNotBeNull();
            firstManager.ShouldNotBeNull();
            secondManager.ShouldNotBeNull();
            secondManager.ShouldNotBeSameAs(firstManager);
        }
        finally
        {
            Engine.InvokePhysicsThreadTask(scene.Destroy);
            RuntimePhysicsServices.Current = previousPhysicsServices;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task PhysxDynamicBox_RestsOnFloorAndResetsToPlayStartPose()
    {
        _ = Engine.Profiler;
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        IRuntimePhysicsServices previousPhysicsServices = RuntimePhysicsServices.Current;
        RuntimePhysicsServices.Current = new FixedStepRuntimePhysicsServices();
        XRWorld? world = null;
        XRWorldInstance? worldInstance = null;

        try
        {
            SceneNode root = new("Root");
            SceneNode floorNode = new(root, "Floor");
            floorNode.SetTransform<RigidBodyTransform>()
                .SetPositionAndRotation(new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity);
            StaticRigidBodyComponent floor = floorNode.AddComponent<StaticRigidBodyComponent>()!;
            floor.Geometry = new IPhysicsGeometry.Box(new Vector3(10.0f, 0.5f, 10.0f));
            floor.CollisionGroup = 1;
            floor.GroupsMask = new PhysicsGroupsMask(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

            SceneNode boxNode = new(root, "Box");
            boxNode.SetTransform<RigidBodyTransform>()
                .SetPositionAndRotation(new Vector3(0.0f, 3.0f, 0.0f), Quaternion.Identity);
            DynamicRigidBodyComponent box = boxNode.AddComponent<DynamicRigidBodyComponent>()!;
            box.Geometry = new IPhysicsGeometry.Box(new Vector3(0.5f));
            box.CollisionGroup = 1;
            box.GroupsMask = new PhysicsGroupsMask(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

            var scene = new XRScene("Physics Scene", root);
            world = new XRWorld("Physics World", scene);
            world.Settings.PhysicsResetMinYDist = 1.0f;
            worldInstance = new XRWorldInstance(world, new VisualScene3D(), new PhysxScene())
            {
                PhysicsEnabled = true,
            };
            XRWorldInstance.WorldInstances.Add(world, worldInstance);

            await worldInstance.BeginPlay();
            worldInstance.PhysicsEnabled = true;

            PhysxStaticRigidBody floorActor = floor.RigidBody.ShouldBeOfType<PhysxStaticRigidBody>();
            PhysxDynamicRigidBody boxActor = box.RigidBody.ShouldBeOfType<PhysxDynamicRigidBody>();
            floorActor.Scene.ShouldBeSameAs(worldInstance.PhysicsScene);
            boxActor.Scene.ShouldBeSameAs(worldInstance.PhysicsScene);
            floorActor.GetShapes().ShouldAllBe(static shape => shape.SimulationShape);
            boxActor.GetShapes().ShouldAllBe(static shape => shape.SimulationShape);
            floorActor.Transform.position.ShouldBe(new Vector3(0.0f, -0.5f, 0.0f));
            boxActor.Transform.position.ShouldBe(new Vector3(0.0f, 3.0f, 0.0f));
            box.MaxContactImpulse.ShouldBe(float.MaxValue);

            Vector3 resetPosition = default;
            Quaternion resetRotation = default;
            Vector3 resetLinearVelocity = default;
            Vector3 resetAngularVelocity = default;
            float settledY = float.NaN;
            Engine.InvokePhysicsThreadTask(() =>
            {
                boxActor.SetTransform(new Vector3(0.0f, -2.0f, 0.0f), Quaternion.Identity, wake: true);
                boxActor.SetLinearVelocity(new Vector3(1.0f, -4.0f, 2.0f));
                boxActor.SetAngularVelocity(new Vector3(0.5f, 1.0f, 1.5f));
                worldInstance.EnqueuePhysicsResetFromMinYPlane(boxActor);
                worldInstance.FixedUpdate();

                (resetPosition, resetRotation) = boxActor.Transform;
                resetLinearVelocity = boxActor.LinearVelocity;
                resetAngularVelocity = boxActor.AngularVelocity;

                for (int step = 0; step < 180; step++)
                    worldInstance.FixedUpdate();

                settledY = boxActor.Transform.position.Y;
            });

            Vector3.Distance(resetPosition, new Vector3(0.0f, 3.0f, 0.0f)).ShouldBeLessThan(0.001f);
            resetRotation.ShouldBe(Quaternion.Identity);
            resetLinearVelocity.Length().ShouldBeLessThan(0.001f);
            resetAngularVelocity.Length().ShouldBeLessThan(0.001f);
            settledY.ShouldBeInRange(0.49f, 0.55f);
        }
        finally
        {
            if (worldInstance is not null &&
                worldInstance.PlayState != XRWorldInstance.EPlayState.Stopped)
                worldInstance.EndPlay();

            if (worldInstance is not null)
                worldInstance.TargetWorld = null;
            if (world is not null)
                XRWorldInstance.WorldInstances.Remove(world);

            RuntimeShaderServices.Current = previousShaderServices;
            RuntimePhysicsServices.Current = previousPhysicsServices;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task PhysxLocomotionController_ResetsToPlayStartPoseAndClearsMotion()
    {
        _ = Engine.Profiler;
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        IRuntimePhysicsServices previousPhysicsServices = RuntimePhysicsServices.Current;
        RuntimePhysicsServices.Current = new FixedStepRuntimePhysicsServices();
        IRuntimeThreadServices previousThreadServices = RuntimeThreadServices.Current;
        var threadServices = new DeferredRuntimeThreadServices();
        RuntimeThreadServices.Current = threadServices;
        XRWorld? world = null;
        XRWorldInstance? worldInstance = null;

        try
        {
            Vector3 activationPosition = new(1.0f, 2.0f, 3.0f);
            Vector3 spawnPosition = new(2.0f, 3.0f, 4.0f);
            SceneNode root = new("Root");
            SceneNode pawnNode = new(root, "Locomotion Pawn");
            RigidBodyTransform pawnTransform = pawnNode.SetTransform<RigidBodyTransform>();
            pawnTransform.SetPositionAndRotation(activationPosition, Quaternion.Identity);
            CharacterMovement3DComponent movement =
                pawnNode.AddComponent<CharacterMovement3DComponent>()!;

            var scene = new XRScene("Physics Scene", root);
            world = new XRWorld("Physics World", scene);
            world.Settings.PhysicsResetMinYDist = 1.0f;
            worldInstance = new XRWorldInstance(world, new VisualScene3D(), new PhysxScene())
            {
                PhysicsEnabled = true,
            };
            XRWorldInstance.WorldInstances.Add(world, worldInstance);

            // Components can activate in Edit mode. Move the authored pawn before Play to
            // prove reset uses the Play-start pose rather than the earlier activation pose.
            pawnTransform.SetWorldTranslation(spawnPosition);
            await worldInstance.BeginPlay();
            threadServices.DrainPhysicsThread();
            threadServices.DrainUpdateThread();
            worldInstance.PhysicsEnabled = true;

            IAbstractCharacterController controller = movement.CharacterController.ShouldNotBeNull();
            movement.Velocity = new Vector3(3.0f, -20.0f, 4.0f);
            controller.SubmitMotion(new CharacterMotionCommand(
                movement.Velocity,
                CharacterMotionInputModel.Velocity,
                0.00001f,
                1.0f / 60.0f));
            controller.Position = new Vector3(2.0f, -2.0f, 4.0f);

            worldInstance.FixedUpdate();

            Vector3.Distance(controller.Position, spawnPosition).ShouldBeLessThan(0.001f);
            movement.Velocity.Length().ShouldBeLessThan(0.001f);
            movement.LastVelocity.Length().ShouldBeLessThan(0.001f);
            movement.Acceleration.Length().ShouldBeLessThan(0.001f);
            controller.RequestedVelocity.Length().ShouldBeLessThan(0.001f);
            controller.EffectiveVelocity.Length().ShouldBeLessThan(0.001f);
            controller.LastMotionCommand.ShouldBe(default);
        }
        finally
        {
            if (worldInstance is not null &&
                worldInstance.PlayState != XRWorldInstance.EPlayState.Stopped)
                worldInstance.EndPlay();

            if (worldInstance is not null)
                worldInstance.TargetWorld = null;
            if (world is not null)
                XRWorldInstance.WorldInstances.Remove(world);

            RuntimeThreadServices.Current = previousThreadServices;
            RuntimeShaderServices.Current = previousShaderServices;
            RuntimePhysicsServices.Current = previousPhysicsServices;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task PhysxController_RemainsSupportedDuringSustainedFloorTraversal()
    {
        _ = Engine.Profiler;
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        IRuntimePhysicsServices previousPhysicsServices = RuntimePhysicsServices.Current;
        RuntimePhysicsServices.Current = new FixedStepRuntimePhysicsServices();
        IRuntimeThreadServices previousThreadServices = RuntimeThreadServices.Current;
        var threadServices = new DeferredRuntimeThreadServices();
        RuntimeThreadServices.Current = threadServices;
        XRWorld? world = null;
        XRWorldInstance? worldInstance = null;
        IAbstractCharacterController? controller = null;

        try
        {
            SceneNode root = new("Root");
            SceneNode floorNode = new(root, "Floor");
            floorNode.SetTransform<RigidBodyTransform>()
                .SetPositionAndRotation(new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity);
            StaticRigidBodyComponent floor = floorNode.AddComponent<StaticRigidBodyComponent>()!;
            floor.Geometry = new IPhysicsGeometry.Box(new Vector3(50.0f, 0.5f, 50.0f));
            floor.CollisionGroup = 1;
            floor.GroupsMask =
                new PhysicsGroupsMask(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

            var scene = new XRScene("Physics Scene", root);
            world = new XRWorld("Physics World", scene);
            var physicsScene = new PhysxScene();
            worldInstance = new XRWorldInstance(world, new VisualScene3D(), physicsScene)
            {
                PhysicsEnabled = true,
            };
            XRWorldInstance.WorldInstances.Add(world, worldInstance);

            await worldInstance.BeginPlay();
            threadServices.DrainPhysicsThread();
            threadServices.DrainUpdateThread();
            worldInstance.PhysicsEnabled = true;

            controller = physicsScene.BackendService.CreateCharacterController(
                new PhysicsCharacterControllerCreateInfo(
                    new Vector3(-10.0f, 0.82f, 0.0f),
                    Vector3.UnitY,
                    0.3f,
                    1.6f,
                    0.70710677f,
                    0.02f,
                    0.3f,
                    1.0f,
                    null));
            controller.ShouldNotBeNull();

            float minimumY = float.PositiveInfinity;
            float maximumY = float.NegativeInfinity;
            int unsupportedSteps = 0;
            for (int step = 0; step < 600; step++)
            {
                controller.SubmitMotion(new CharacterMotionCommand(
                    new Vector3(4.0f, -0.9f, 0.0f),
                    CharacterMotionInputModel.Velocity,
                    0.00001f,
                    1.0f / 60.0f));
                worldInstance.FixedUpdate();

                Vector3 position = controller.Position;
                minimumY = MathF.Min(minimumY, position.Y);
                maximumY = MathF.Max(maximumY, position.Y);
                if (step > 0 && !controller.IsGrounded)
                    unsupportedSteps++;
            }

            controller.Position.X.ShouldBeGreaterThan(29.0f);
            minimumY.ShouldBeGreaterThan(0.79f);
            maximumY.ShouldBeLessThan(0.85f);
            unsupportedSteps.ShouldBe(0);

            controller.RequestRelease();
            worldInstance.FixedUpdate();
            controller = null;
        }
        finally
        {
            controller?.RequestRelease();

            if (worldInstance is not null &&
                worldInstance.PlayState != XRWorldInstance.EPlayState.Stopped)
                worldInstance.EndPlay();

            if (worldInstance is not null)
                worldInstance.TargetWorld = null;
            if (world is not null)
                XRWorldInstance.WorldInstances.Remove(world);

            RuntimeThreadServices.Current = previousThreadServices;
            RuntimeShaderServices.Current = previousShaderServices;
            RuntimePhysicsServices.Current = previousPhysicsServices;
        }
    }

    [Test]
    [NonParallelizable]
    public async Task PhysxLocomotionController_ReconcilesGroundVelocityWithoutFallingThrough()
    {
        _ = Engine.Profiler;
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        IRuntimePhysicsServices previousPhysicsServices = RuntimePhysicsServices.Current;
        RuntimePhysicsServices.Current = new FixedStepRuntimePhysicsServices();
        IRuntimeThreadServices previousThreadServices = RuntimeThreadServices.Current;
        var threadServices = new DeferredRuntimeThreadServices();
        RuntimeThreadServices.Current = threadServices;
        XRWorld? world = null;
        XRWorldInstance? worldInstance = null;

        try
        {
            SceneNode root = new("Root");
            SceneNode floorNode = new(root, "Floor");
            floorNode.SetTransform<RigidBodyTransform>()
                .SetPositionAndRotation(new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity);
            StaticRigidBodyComponent floor = floorNode.AddComponent<StaticRigidBodyComponent>()!;
            floor.Geometry = new IPhysicsGeometry.Box(new Vector3(50.0f, 0.5f, 50.0f));
            floor.CollisionGroup = 1;
            floor.GroupsMask =
                new PhysicsGroupsMask(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

            SceneNode pawnNode = new(root, "Locomotion Pawn");
            pawnNode.SetTransform<RigidBodyTransform>()
                .SetPositionAndRotation(new Vector3(0.0f, 2.0f, 0.0f), Quaternion.Identity);
            CharacterMovement3DComponent movement =
                pawnNode.AddComponent<CharacterMovement3DComponent>()!;
            movement.TickInputWithPhysics = true;

            var scene = new XRScene("Physics Scene", root);
            world = new XRWorld("Physics World", scene);
            world.Settings.PhysicsResetMinYDist = 0.0f;
            worldInstance = new XRWorldInstance(world, new VisualScene3D(), new PhysxScene())
            {
                PhysicsEnabled = true,
            };
            XRWorldInstance.WorldInstances.Add(world, worldInstance);

            await worldInstance.BeginPlay();
            threadServices.DrainPhysicsThread();
            threadServices.DrainUpdateThread();
            worldInstance.PhysicsEnabled = true;

            IAbstractCharacterController controller = movement.CharacterController.ShouldNotBeNull();
            float minimumY = float.PositiveInfinity;
            for (int step = 0; step < 600; step++)
            {
                worldInstance.FixedUpdate();
                minimumY = MathF.Min(minimumY, controller.Position.Y);
            }

            controller.IsGrounded.ShouldBeTrue();
            controller.Position.Y.ShouldBeInRange(0.79f, 0.85f);
            minimumY.ShouldBeGreaterThan(0.79f);
            movement.Velocity.Y.ShouldBeInRange(-1.0f, 0.0f);
            controller.EffectiveVelocity.Y.ShouldBeInRange(-0.001f, 0.001f);
        }
        finally
        {
            if (worldInstance is not null &&
                worldInstance.PlayState != XRWorldInstance.EPlayState.Stopped)
                worldInstance.EndPlay();

            if (worldInstance is not null)
                worldInstance.TargetWorld = null;
            if (world is not null)
                XRWorldInstance.WorldInstances.Remove(world);

            RuntimeThreadServices.Current = previousThreadServices;
            RuntimeShaderServices.Current = previousShaderServices;
            RuntimePhysicsServices.Current = previousPhysicsServices;
        }
    }

    [Test]
    public void RigidBodyTransform_NativeActorLinkIsExcludedFromPersistentFormats()
    {
        PropertyInfo property = typeof(RigidBodyTransform)
            .GetProperty(nameof(RigidBodyTransform.RigidBody))!;

        property.GetCustomAttribute<RuntimeOnlyAttribute>().ShouldNotBeNull();
        property.GetCustomAttribute<YamlIgnoreAttribute>().ShouldNotBeNull();
        property.GetCustomAttribute<MemoryPackIgnoreAttribute>().ShouldNotBeNull();
    }

    [Test]
    [NonParallelizable]
    public async Task ActivePhysxPhysicsTestingWorld_SnapshotAvoidsMemoryPackFallbackAndNativeActors()
    {
        // Initialize engine-owned globals before substituting the shader loader so teardown restores
        // the real service instead of leaving an initialized Engine with a null global service.
        _ = Engine.Profiler;
        IRuntimeShaderServices? previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
        XRWorld? world = null;
        XRWorldInstance? worldInstance = null;

        try
        {
            SceneNode root = new("Root");
            BootstrapPhysicsTestWorldBuilder.AddPlayground(root, sphereCount: 0);
            root.NewChild("Directional Light").AddComponent<DirectionalLightComponent>();
            var scene = new XRScene("Physics Testing Scene", root);
            var gameMode = new LocomotionGameMode
            {
                SpawnPositionOverride = new Vector3(0.0f, 2.0f, 9.0f),
                SpawnRotationOverride = Quaternion.Identity,
            };
            world = new XRWorld("Physics Testing World", gameMode, scene);
            var physicsScene = new PhysxScene();
            worldInstance = new XRWorldInstance(world, new VisualScene3D(), physicsScene);
            XRWorldInstance.WorldInstances.Add(world, worldInstance);

            await worldInstance.BeginPlay();

            DynamicRigidBodyComponent[] activeBodies = EnumerateHierarchy(root)
                .SelectMany(static node => node.Components)
                .OfType<DynamicRigidBodyComponent>()
                .ToArray();
            activeBodies.ShouldNotBeEmpty();
            activeBodies.ShouldAllBe(static body => body.RigidBody is PhysxDynamicRigidBody);

            int memoryPackExceptionCount = 0;
            var memoryPackExceptionMessages = new ConcurrentQueue<string>();
            EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
            {
                if (args.Exception is MemoryPackSerializationException)
                {
                    Interlocked.Increment(ref memoryPackExceptionCount);
                    memoryPackExceptionMessages.Enqueue(args.Exception.Message);
                }
            };

            AppDomain.CurrentDomain.FirstChanceException += handler;
            WorldStateSnapshot? snapshot;
            try
            {
                snapshot = WorldStateSnapshot.Capture(world);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= handler;
            }

            snapshot.ShouldNotBeNull();
            snapshot!.IsValid.ShouldBeTrue();
            snapshot.SerializedScenes.Count.ShouldBe(1);
            snapshot.SerializedGameMode.ShouldNotBeNull();
            LocomotionGameMode restoredGameMode = SnapshotBinarySerializer
                .Deserialize<GameMode>(snapshot.SerializedGameMode!)
                .ShouldBeOfType<LocomotionGameMode>();
            restoredGameMode.SpawnPositionOverride.ShouldBe(new Vector3(0.0f, 2.0f, 9.0f));
            restoredGameMode.SpawnRotationOverride.ShouldBe(Quaternion.Identity);
            restoredGameMode.PlayerUserInterfaceClass.ShouldBe(typeof(UICanvasComponent));
            memoryPackExceptionCount.ShouldBe(
                0,
                string.Join(
                    Environment.NewLine,
                    memoryPackExceptionMessages.Distinct(StringComparer.Ordinal)));

            byte[] scenePayload = snapshot.SerializedScenes.Values.Single();
            XRScene restoredScene = SnapshotBinarySerializer
                .Deserialize<XRScene>(scenePayload)
                .ShouldNotBeNull();
            SceneNode[] restoredNodes = restoredScene.RootNodes
                .SelectMany(EnumerateHierarchy)
                .ToArray();

            restoredNodes
                .Select(static node => node.Transform)
                .OfType<RigidBodyTransform>()
                .ShouldAllBe(static transform => transform.RigidBody == null);
            restoredNodes
                .SelectMany(static node => node.Components)
                .OfType<DynamicRigidBodyComponent>()
                .ShouldAllBe(static body => body.RigidBody == null);
            restoredNodes
                .SelectMany(static node => node.Components)
                .OfType<StaticRigidBodyComponent>()
                .ShouldAllBe(static body => body.RigidBody == null);

            StaticRigidBodyComponent restoredFloor = restoredNodes
                .Single(static node => node.Name == "Physics Floor")
                .GetComponent<StaticRigidBodyComponent>()!;
            restoredFloor.Geometry.ShouldBeOfType<IPhysicsGeometry.Box>()
                .HalfExtents.ShouldBe(new Vector3(30.0f, 0.5f, 30.0f));

            DynamicRigidBodyComponent restoredBox = restoredNodes
                .Single(static node => node.Name == "Box Stack 00")
                .GetComponent<DynamicRigidBodyComponent>()!;
            restoredBox.Geometry.ShouldBeOfType<IPhysicsGeometry.Box>()
                .HalfExtents.ShouldBe(new Vector3(0.5f));
        }
        finally
        {
            if (worldInstance is not null &&
                worldInstance.PlayState != XRWorldInstance.EPlayState.Stopped)
                worldInstance.EndPlay();

            if (worldInstance is not null)
                worldInstance.TargetWorld = null;
            if (world is not null)
                XRWorldInstance.WorldInstances.Remove(world);

            RuntimeShaderServices.Current = previousShaderServices;
        }
    }

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

    private static IAbstractCharacterController? CreateTestCharacterController(PhysxScene scene)
        => scene.BackendService.CreateCharacterController(
            new PhysicsCharacterControllerCreateInfo(
                new Vector3(0.0f, 3.0f, 0.0f),
                Vector3.UnitY,
                0.3f,
                1.6f,
                0.70710677f,
                0.02f,
                0.3f,
                1.0f,
                null));

    private sealed class FixedStepRuntimePhysicsServices : IRuntimePhysicsServices
    {
        private readonly PhysicsVisualizeSettings _visualizeSettings = new();

        public float FixedDeltaSeconds => 1.0f / 60.0f;
        public PhysicsVisualizeSettings VisualizeSettings => _visualizeSettings;
        public bool JoltDebugRenderDiagnostics => false;

        public void RenderPoint(Vector3 position, XREngine.Data.Colors.ColorF4 color) { }
        public void RenderLine(Vector3 start, Vector3 end, XREngine.Data.Colors.ColorF4 color) { }
        public void RenderSphere(Vector3 center, float radius, bool solid, XREngine.Data.Colors.ColorF4 color) { }
        public void RenderCapsule(Vector3 start, Vector3 end, float radius, bool solid, XREngine.Data.Colors.ColorF4 color) { }
    }

    private sealed class DeferredRuntimeThreadServices : IRuntimeThreadServices
    {
        private readonly ConcurrentQueue<Action> _physicsActions = new();
        private readonly ConcurrentQueue<Action> _updateActions = new();

        public bool InvokeOnAppThread(
            Action action,
            string? reason = null,
            bool executeNowIfAlreadyAppThread = false)
        {
            action();
            return true;
        }

        public void EnqueueUpdateThread(Action action)
            => _updateActions.Enqueue(action);

        public void EnqueuePhysicsThread(Action action)
            => _physicsActions.Enqueue(action);

        public void DrainPhysicsThread()
        {
            while (_physicsActions.TryDequeue(out Action? action))
                action();
        }

        public void DrainUpdateThread()
        {
            while (_updateActions.TryDequeue(out Action? action))
                action();
        }
    }
}
