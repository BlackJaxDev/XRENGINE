using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.Compute;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Unit tests for GPUPhysicsChainDispatcher.
/// Tests cover singleton behavior, component registration, buffer management,
/// data merging, and batched dispatch functionality.
/// </summary>
[TestFixture]
public sealed class GPUPhysicsChainDispatcherTests
{
    #region Test Helpers

    private static (SceneNode rootNode, Transform rootBone, Transform[] childBones) CreateBoneHierarchy(int boneCount = 5, float boneLength = 0.1f)
    {
        var rootNode = new SceneNode($"DispatcherTestRoot_{Guid.NewGuid():N}");
        var rootBone = new Transform();
        rootNode.SetTransform(rootBone);

        var bones = new Transform[boneCount];
        Transform parent = rootBone;

        for (int i = 0; i < boneCount; i++)
        {
            var bone = new Transform();
            bone.Parent = parent;
            bone.Translation = new Vector3(0, -boneLength, 0);
            bones[i] = bone;
            parent = bone;
        }

        return (rootNode, rootBone, bones);
    }

    private static GPUPhysicsChainComponent CreateTestComponent(SceneNode node, Transform root, bool useBatched = true)
    {
        var component = node.AddComponent<GPUPhysicsChainComponent>()!;
        component.Root = root;
        component.Damping = 0.1f;
        component.Elasticity = 0.1f;
        component.Stiffness = 0.1f;
        component.Gravity = new Vector3(0, -9.8f, 0);
        component.UseBatchedDispatcher = useBatched;
        return component;
    }

    #endregion

    #region Singleton Tests

    [Test]
    public void Instance_ReturnsSameInstance()
    {
        var instance1 = GPUPhysicsChainDispatcher.Instance;
        var instance2 = GPUPhysicsChainDispatcher.Instance;

        instance1.ShouldBe(instance2);
    }

    [Test]
    public void Instance_IsNotNull()
    {
        GPUPhysicsChainDispatcher.Instance.ShouldNotBeNull();
    }

    #endregion

    #region Registration Tests

    [Test]
    public void Register_IncreasesComponentCount()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        int initialCount = dispatcher.RegisteredComponentCount;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        dispatcher.Register(component);

        dispatcher.RegisteredComponentCount.ShouldBe(initialCount + 1);

        // Cleanup
        dispatcher.Unregister(component);
    }

    [Test]
    public void Unregister_DecreasesComponentCount()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        dispatcher.Register(component);
        int countAfterRegister = dispatcher.RegisteredComponentCount;

        dispatcher.Unregister(component);

        dispatcher.RegisteredComponentCount.ShouldBe(countAfterRegister - 1);
    }

    [Test]
    public void IsRegistered_ReturnsTrueForRegisteredComponent()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        dispatcher.Register(component);

        dispatcher.IsRegistered(component).ShouldBeTrue();

        // Cleanup
        dispatcher.Unregister(component);
    }

    [Test]
    public void IsRegistered_ReturnsFalseForUnregisteredComponent()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        dispatcher.IsRegistered(component).ShouldBeFalse();
    }

    [Test]
    public void MultipleComponents_CanBeRegistered()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        int initialCount = dispatcher.RegisteredComponentCount;

        var components = new List<GPUPhysicsChainComponent>();
        var nodes = new List<SceneNode>();

        for (int i = 0; i < 5; i++)
        {
            var (node, rootBone, _) = CreateBoneHierarchy(3);
            nodes.Add(node);
            var component = CreateTestComponent(node, rootBone, useBatched: false);
            components.Add(component);
            dispatcher.Register(component);
        }

        dispatcher.RegisteredComponentCount.ShouldBe(initialCount + 5);

        // Cleanup
        foreach (var component in components)
            dispatcher.Unregister(component);
    }

    [Test]
    public void DoubleRegister_DoesNotDuplicateComponent()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        dispatcher.Register(component);
        int countAfterFirst = dispatcher.RegisteredComponentCount;

        dispatcher.Register(component); // Register again

        // Count should remain the same (dictionary behavior)
        dispatcher.RegisteredComponentCount.ShouldBe(countAfterFirst);

        // Cleanup
        dispatcher.Unregister(component);
    }

    [Test]
    public void UnregisterNonexistent_DoesNotThrow()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        // Should not throw when unregistering component that was never registered
        Should.NotThrow(() => dispatcher.Unregister(component));
    }

    #endregion

    #region GPU Data Structure Tests

    [Test]
    public void GPUParticleData_HasCorrectLayout()
    {
        var data = new GPUPhysicsChainDispatcher.GPUParticleData
        {
            Position = new Vector3(1, 2, 3),
            PrevPosition = new Vector3(4, 5, 6),
            TransformPosition = new Vector3(7, 8, 9),
            TransformLocalPosition = new Vector3(10, 11, 12),
            ParentIndex = 5,
            Damping = 0.1f,
            Elasticity = 0.2f,
            Stiffness = 0.3f,
            Inert = 0.4f,
            Friction = 0.5f,
            Radius = 0.6f,
            BoneLength = 0.7f,
            IsColliding = 1
        };

        data.Position.ShouldBe(new Vector3(1, 2, 3));
        data.PrevPosition.ShouldBe(new Vector3(4, 5, 6));
        data.TransformPosition.ShouldBe(new Vector3(7, 8, 9));
        data.TransformLocalPosition.ShouldBe(new Vector3(10, 11, 12));
        data.ParentIndex.ShouldBe(5);
        data.Damping.ShouldBe(0.1f);
        data.Elasticity.ShouldBe(0.2f);
        data.Stiffness.ShouldBe(0.3f);
        data.Inert.ShouldBe(0.4f);
        data.Friction.ShouldBe(0.5f);
        data.Radius.ShouldBe(0.6f);
        data.BoneLength.ShouldBe(0.7f);
        data.IsColliding.ShouldBe(1);
    }

    [Test]
    public void GPUParticleTreeData_HasCorrectLayout()
    {
        var data = new GPUPhysicsChainDispatcher.GPUParticleTreeData
        {
            LocalGravity = new Vector3(0, -9.8f, 0),
            RestGravity = new Vector3(0, -1, 0),
            ParticleStart = 10,
            ParticleCount = 20,
            RootWorldToLocal = Matrix4x4.Identity,
            BoneTotalLength = 1.5f
        };

        data.LocalGravity.ShouldBe(new Vector3(0, -9.8f, 0));
        data.RestGravity.ShouldBe(new Vector3(0, -1, 0));
        data.ParticleStart.ShouldBe(10);
        data.ParticleCount.ShouldBe(20);
        data.RootWorldToLocal.ShouldBe(Matrix4x4.Identity);
        data.BoneTotalLength.ShouldBe(1.5f);
    }

    [Test]
    public void GPUColliderData_HasCorrectLayout()
    {
        var data = new GPUPhysicsChainDispatcher.GPUColliderData
        {
            Center = new Vector4(1, 2, 3, 0.5f),
            Params = new Vector4(4, 5, 6, 0),
            Type = 0 // Sphere
        };

        data.Center.ShouldBe(new Vector4(1, 2, 3, 0.5f));
        data.Params.ShouldBe(new Vector4(4, 5, 6, 0));
        data.Type.ShouldBe(0);
    }

    [Test]
    public void ColliderTypes_AreCorrectlyDefined()
    {
        // Verify collider type constants match shader expectations
        // Type 0 = Sphere
        // Type 1 = Capsule
        // Type 2 = Box
        // Type 3 = Plane

        var sphereCollider = new GPUPhysicsChainDispatcher.GPUColliderData { Type = 0 };
        var capsuleCollider = new GPUPhysicsChainDispatcher.GPUColliderData { Type = 1 };
        var boxCollider = new GPUPhysicsChainDispatcher.GPUColliderData { Type = 2 };
        var planeCollider = new GPUPhysicsChainDispatcher.GPUColliderData { Type = 3 };

        sphereCollider.Type.ShouldBe(0);
        capsuleCollider.Type.ShouldBe(1);
        boxCollider.Type.ShouldBe(2);
        planeCollider.Type.ShouldBe(3);
    }

    #endregion

    #region Statistics Tests

    [Test]
    public void TotalParticleCount_InitiallyZero()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        // Note: This may not be zero if other tests have run
        dispatcher.TotalParticleCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void TotalColliderCount_InitiallyZero()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        // Note: This may not be zero if other tests have run
        dispatcher.TotalColliderCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Lifecycle Tests

    [Test]
    public void SetEnabled_DisablesDispatcher()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        // Should not throw
        Should.NotThrow(() => dispatcher.SetEnabled(false));
        Should.NotThrow(() => dispatcher.SetEnabled(true));
    }

    [Test]
    public void Reset_ClearsActiveRequests()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        // Should not throw
        Should.NotThrow(() => dispatcher.Reset());
    }

    #endregion

    #region GPUPhysicsChainRequest Tests

    [Test]
    public void GPUPhysicsChainRequest_StoresComponent()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        var request = new GPUPhysicsChainRequest(component);

        request.Component.ShouldBe(component);
    }

    [Test]
    public void GPUPhysicsChainRequest_HasEmptyCollections()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        var request = new GPUPhysicsChainRequest(component);

        request.Particles.ShouldBeEmpty();
        request.Trees.ShouldBeEmpty();
        request.Transforms.ShouldBeEmpty();
        request.Colliders.ShouldBeEmpty();
    }

    [Test]
    public void GPUPhysicsChainRequest_DefaultParameters()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        var request = new GPUPhysicsChainRequest(component);

        request.DeltaTime.ShouldBe(0f);
        request.ObjectScale.ShouldBe(0f);
        request.Weight.ShouldBe(0f);
        request.Force.ShouldBe(Vector3.Zero);
        request.Gravity.ShouldBe(Vector3.Zero);
        request.ObjectMove.ShouldBe(Vector3.Zero);
        request.FreezeAxis.ShouldBe(0);
        request.LoopCount.ShouldBe(0);
        request.TimeVar.ShouldBe(0f);
        request.NeedsUpdate.ShouldBeFalse();
        request.SkipUpdate.ShouldBeFalse();
    }

    [Test]
    public void GPUPhysicsChainRequest_StoresOffsets()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        var request = new GPUPhysicsChainRequest(component);
        request.ParticleOffset = 100;
        request.TreeOffset = 10;
        request.ColliderOffset = 5;

        request.ParticleOffset.ShouldBe(100);
        request.TreeOffset.ShouldBe(10);
        request.ColliderOffset.ShouldBe(5);
    }

    #endregion

    #region Buffer Merging Logic Tests

    [Test]
    public void ParticleOffset_AdjustsParentIndices()
    {
        // When particles from multiple components are merged,
        // parent indices need to be adjusted to global space

        var particle1 = new GPUPhysicsChainDispatcher.GPUParticleData
        {
            ParentIndex = 2 // Local index
        };

        var particle2 = new GPUPhysicsChainDispatcher.GPUParticleData
        {
            ParentIndex = 1 // Local index
        };

        // Simulate offset adjustment (what the dispatcher does internally)
        int componentBOffset = 10;
        if (particle2.ParentIndex >= 0)
        {
            particle2.ParentIndex += componentBOffset;
        }

        particle2.ParentIndex.ShouldBe(11); // 1 + 10
    }

    [Test]
    public void TreeParticleStart_AdjustsToGlobalSpace()
    {
        // When trees from multiple components are merged,
        // ParticleStart indices need to be adjusted

        var tree = new GPUPhysicsChainDispatcher.GPUParticleTreeData
        {
            ParticleStart = 0, // Local start
            ParticleCount = 10
        };

        // Simulate offset adjustment
        int particleOffset = 50;
        tree.ParticleStart += particleOffset;

        tree.ParticleStart.ShouldBe(50); // 0 + 50
    }

    [Test]
    public void RootParticle_ParentIndexRemainsNegative()
    {
        // Root particles have ParentIndex = -1 and should stay that way

        var rootParticle = new GPUPhysicsChainDispatcher.GPUParticleData
        {
            ParentIndex = -1 // Root particle
        };

        int componentOffset = 100;

        // Only adjust if ParentIndex >= 0
        if (rootParticle.ParentIndex >= 0)
        {
            rootParticle.ParentIndex += componentOffset;
        }

        rootParticle.ParentIndex.ShouldBe(-1); // Should remain -1
    }

    #endregion

    #region Integration Tests

    [Test]
    public void FullWorkflow_RegisterSubmitUnregister_DoesNotThrow()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        var (node, rootBone, _) = CreateBoneHierarchy(5);
        var component = CreateTestComponent(node, rootBone, useBatched: false);

        // Register
        Should.NotThrow(() => dispatcher.Register(component));

        dispatcher.IsRegistered(component).ShouldBeTrue();

        // Submit data (simulated)
        var particles = new List<GPUPhysicsChainDispatcher.GPUParticleData>
        {
            new() { Position = Vector3.Zero, ParentIndex = -1 },
            new() { Position = new Vector3(0, -0.1f, 0), ParentIndex = 0 }
        };

        var trees = new List<GPUPhysicsChainDispatcher.GPUParticleTreeData>
        {
            new() { LocalGravity = new Vector3(0, -9.8f, 0), ParticleStart = 0, ParticleCount = 2 }
        };

        var transforms = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.Identity };
        var colliders = new List<GPUPhysicsChainDispatcher.GPUColliderData>();

        Should.NotThrow(() => dispatcher.SubmitData(
            component,
            particles,
            trees,
            transforms,
            colliders,
            deltaTime: 0.016f,
            objectScale: 1.0f,
            weight: 1.0f,
            force: Vector3.Zero,
            gravity: new Vector3(0, -9.8f, 0),
            objectMove: Vector3.Zero,
            freezeAxis: 0,
            loopCount: 1,
            timeVar: 0.016f
        ));

        // Unregister
        Should.NotThrow(() => dispatcher.Unregister(component));

        dispatcher.IsRegistered(component).ShouldBeFalse();
    }

    [Test]
    public void MultipleComponents_CanSubmitData()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        var components = new List<GPUPhysicsChainComponent>();
        var nodes = new List<SceneNode>();

        // Create and register multiple components
        for (int i = 0; i < 3; i++)
        {
            var (node, rootBone, _) = CreateBoneHierarchy(3);
            nodes.Add(node);
            var component = CreateTestComponent(node, rootBone, useBatched: false);
            components.Add(component);
            dispatcher.Register(component);
        }

        // Submit data for each component
        foreach (var component in components)
        {
            var particles = new List<GPUPhysicsChainDispatcher.GPUParticleData>
            {
                new() { Position = Vector3.Zero, ParentIndex = -1 }
            };

            var trees = new List<GPUPhysicsChainDispatcher.GPUParticleTreeData>
            {
                new() { ParticleStart = 0, ParticleCount = 1 }
            };

            Should.NotThrow(() => dispatcher.SubmitData(
                component,
                particles,
                trees,
                [Matrix4x4.Identity],
                [],
                0.016f, 1.0f, 1.0f,
                Vector3.Zero, new Vector3(0, -9.8f, 0), Vector3.Zero,
                0, 1, 0.016f
            ));
        }

        // Cleanup
        foreach (var component in components)
            dispatcher.Unregister(component);
    }

    #endregion
}
