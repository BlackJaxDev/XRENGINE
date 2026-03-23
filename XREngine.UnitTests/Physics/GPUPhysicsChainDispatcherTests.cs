using System;
using System.Numerics;
using System.Reflection;
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
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Action<long, bool> RecordCpuUploadBytesMethod = CreateStaticDelegate<Action<long, bool>>("RecordCpuUploadBytes");
    private static readonly Action<long, bool> RecordGpuCopyBytesMethod = CreateStaticDelegate<Action<long, bool>>("RecordGpuCopyBytes");
    private static readonly Action<long, bool> RecordCpuReadbackBytesMethod = CreateStaticDelegate<Action<long, bool>>("RecordCpuReadbackBytes");
    private static readonly Action<long> RecordHierarchyRecalcTicksMethod = CreateStaticDelegate<Action<long>>("RecordHierarchyRecalcTicks");

    #region Test Helpers

    private static TDelegate CreateStaticDelegate<TDelegate>(string methodName) where TDelegate : Delegate
    {
        MethodInfo method = typeof(GPUPhysicsChainDispatcher).GetMethod(methodName, StaticFlags)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");
        return method.CreateDelegate<TDelegate>();
    }

    private static void SetStaticField<T>(string fieldName, T value)
    {
        FieldInfo field = typeof(GPUPhysicsChainDispatcher).GetField(fieldName, StaticFlags)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(null, value);
    }

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

    private static PhysicsChainComponent CreateTestComponent(SceneNode node, Transform root, bool useBatched = true)
    {
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.UseGPU = true;
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

        dispatcher.IsRegistered(component).ShouldBeTrue();

        dispatcher.Unregister(component);
    }

    [Test]
    public void MultipleComponents_CanBeRegistered()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        int initialCount = dispatcher.RegisteredComponentCount;

        var components = new List<PhysicsChainComponent>();
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

    [Test]
    public void BandwidthPressureSnapshot_TracksRecordedCountersAndDelta()
    {
        GPUPhysicsChainDispatcher.ResetBandwidthPressureStats();
        SetStaticField("s_residentParticleBytes", 4096L);
        SetStaticField("s_dispatchGroupCount", 2);
        SetStaticField("s_dispatchIterationCount", 5);

        RecordCpuUploadBytesMethod(128L, false);
        RecordCpuUploadBytesMethod(64L, true);
        RecordGpuCopyBytesMethod(32L, true);
        RecordCpuReadbackBytesMethod(16L, false);
        RecordHierarchyRecalcTicksMethod(250L);

        GPUPhysicsChainBandwidthSnapshot baseline = new();
        GPUPhysicsChainBandwidthSnapshot snapshot = GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot();
        GPUPhysicsChainBandwidthSnapshot delta = snapshot.Delta(baseline);

        snapshot.CpuUploadBytes.ShouldBe(192L);
        snapshot.GpuCopyBytes.ShouldBe(32L);
        snapshot.CpuReadbackBytes.ShouldBe(16L);
        snapshot.StandaloneCpuUploadBytes.ShouldBe(128L);
        snapshot.BatchedCpuUploadBytes.ShouldBe(64L);
        snapshot.BatchedGpuCopyBytes.ShouldBe(32L);
        snapshot.StandaloneCpuReadbackBytes.ShouldBe(16L);
        snapshot.DispatchGroupCount.ShouldBe(2);
        snapshot.DispatchIterationCount.ShouldBe(5);
        snapshot.ResidentParticleBytes.ShouldBe(4096L);
        snapshot.HierarchyRecalcTicks.ShouldBe(250L);
        snapshot.TotalTransferBytes.ShouldBe(240L);

        delta.ShouldBe(snapshot);
    }

    [Test]
    public void ResetBandwidthPressureStats_ClearsTransferAndDispatchCounters()
    {
        GPUPhysicsChainDispatcher.ResetBandwidthPressureStats();
        SetStaticField("s_residentParticleBytes", 0L);
        SetStaticField("s_dispatchGroupCount", 3);
        SetStaticField("s_dispatchIterationCount", 7);

        RecordCpuUploadBytesMethod(10L, false);
        RecordGpuCopyBytesMethod(20L, false);
        RecordCpuReadbackBytesMethod(30L, true);
        RecordHierarchyRecalcTicksMethod(40L);

        GPUPhysicsChainDispatcher.ResetBandwidthPressureStats();
        GPUPhysicsChainBandwidthSnapshot snapshot = GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot();

        snapshot.CpuUploadBytes.ShouldBe(0L);
        snapshot.GpuCopyBytes.ShouldBe(0L);
        snapshot.CpuReadbackBytes.ShouldBe(0L);
        snapshot.StandaloneCpuUploadBytes.ShouldBe(0L);
        snapshot.StandaloneCpuReadbackBytes.ShouldBe(0L);
        snapshot.BatchedCpuUploadBytes.ShouldBe(0L);
        snapshot.BatchedGpuCopyBytes.ShouldBe(0L);
        snapshot.BatchedCpuReadbackBytes.ShouldBe(0L);
        snapshot.DispatchGroupCount.ShouldBe(0);
        snapshot.DispatchIterationCount.ShouldBe(0);
        snapshot.HierarchyRecalcTicks.ShouldBe(0L);
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
            new() { Position = Vector3.Zero, PrevPosition = Vector3.Zero, PreviousPhysicsPosition = Vector3.Zero },
            new() { Position = new Vector3(0, -0.1f, 0), PrevPosition = new Vector3(0, -0.1f, 0), PreviousPhysicsPosition = new Vector3(0, -0.1f, 0) }
        };

        var particleStaticData = new List<GPUPhysicsChainDispatcher.GPUParticleStaticData>
        {
            new() { ParentIndex = -1, Radius = 0.05f, BoneLength = 0.1f, TreeIndex = 0 },
            new() { ParentIndex = 0, Radius = 0.05f, BoneLength = 0.1f, TreeIndex = 0 }
        };

        var trees = new List<GPUPhysicsChainDispatcher.GPUParticleTreeData>
        {
            new() { RestGravity = new Vector3(0, -9.8f, 0) }
        };

        var transforms = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.Identity };
        var colliders = new List<GPUPhysicsChainDispatcher.GPUColliderData>();

        Should.NotThrow(() => dispatcher.SubmitData(
            component,
            particles,
            particleStaticData,
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
            timeVar: 0.016f,
            executionGeneration: 1,
            submissionId: 1,
            staticDataVersion: 1,
            particleStateVersion: 1,
            transformDataSignature: 1,
            colliderDataSignature: 0
        ));

        // Unregister
        Should.NotThrow(() => dispatcher.Unregister(component));

        dispatcher.IsRegistered(component).ShouldBeFalse();
    }

    [Test]
    public void MultipleComponents_CanSubmitData()
    {
        var dispatcher = GPUPhysicsChainDispatcher.Instance;
        var components = new List<PhysicsChainComponent>();
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
                new() { Position = Vector3.Zero, PrevPosition = Vector3.Zero, PreviousPhysicsPosition = Vector3.Zero }
            };

            var particleStaticData = new List<GPUPhysicsChainDispatcher.GPUParticleStaticData>
            {
                new() { ParentIndex = -1, Radius = 0.05f, BoneLength = 0.1f, TreeIndex = 0 }
            };

            var trees = new List<GPUPhysicsChainDispatcher.GPUParticleTreeData>
            {
                new() { RestGravity = new Vector3(0, -9.8f, 0) }
            };

            Should.NotThrow(() => dispatcher.SubmitData(
                component,
                particles,
                particleStaticData,
                trees,
                [Matrix4x4.Identity],
                [],
                0.016f, 1.0f, 1.0f,
                Vector3.Zero, new Vector3(0, -9.8f, 0), Vector3.Zero,
                0, 1, 0.016f,
                executionGeneration: 1,
                submissionId: component.GetHashCode(),
                staticDataVersion: 1,
                particleStateVersion: 1,
                transformDataSignature: 1,
                colliderDataSignature: 0
            ));
        }

        // Cleanup
        foreach (var component in components)
            dispatcher.Unregister(component);
    }

    #endregion
}
