using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.Compute;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Tests for <see cref="GPUPhysicsChainDispatcher"/>: singleton lifecycle,
/// component registration, data submission, and the full register → submit → unregister workflow.
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

        Should.NotThrow(() => dispatcher.Unregister(component));
    }

    #endregion

    #region Lifecycle Tests

    [Test]
    public void SetEnabled_TogglesWithoutThrowing()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        Should.NotThrow(() => dispatcher.SetEnabled(false));
        Should.NotThrow(() => dispatcher.SetEnabled(true));
    }

    [Test]
    public void Reset_ClearsActiveRequests()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;

        Should.NotThrow(() => dispatcher.Reset());
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
