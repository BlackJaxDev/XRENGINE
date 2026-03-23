using System;
using System.Numerics;
using System.Reflection;
using System.Collections;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Components.Animation;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Tests for <see cref="PhysicsChainComponent"/> (CPU version):
/// particle setup, weight management, virtual end bones, and full-parameter integration.
/// </summary>
[TestFixture]
public sealed class PhysicsChainComponentTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Func<PhysicsChainComponent, bool> RequiresGpuReadbackMethod =
        CreateOpenDelegate<Func<PhysicsChainComponent, bool>>("RequiresGpuReadback");

    private static readonly Action<PhysicsChainComponent> PrepareGpuDataMethod =
        CreateOpenDelegate<Action<PhysicsChainComponent>>("PrepareGPUData");

    private static readonly Action<PhysicsChainComponent> InitializeBuffersMethod =
        CreateOpenDelegate<Action<PhysicsChainComponent>>("InitializeBuffers");

    private static readonly Action<PhysicsChainComponent, float> UpdatePerTreeParamsMethod =
        CreateOpenDelegate<Action<PhysicsChainComponent, float>>("UpdatePerTreeParams");

    private static readonly Action<PhysicsChainComponent, XRMeshRenderer, Dictionary<Transform, int>, Dictionary<int, int>, Dictionary<int, Vector3>> TryAddGpuDrivenRendererStateMethod =
        CreateOpenDelegate<Action<PhysicsChainComponent, XRMeshRenderer, Dictionary<Transform, int>, Dictionary<int, int>, Dictionary<int, Vector3>>>("TryAddGpuDrivenRendererState");

    private static readonly Action<PhysicsChainComponent> ClearGpuDrivenRendererBindingsMethod =
        CreateOpenDelegate<Action<PhysicsChainComponent>>("ClearGpuDrivenRendererBindings");

    #region Test Helpers

    private static TDelegate CreateOpenDelegate<TDelegate>(string methodName) where TDelegate : Delegate
    {
        MethodInfo method = typeof(PhysicsChainComponent).GetMethod(methodName, InstanceFlags)
            ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");
        return method.CreateDelegate<TDelegate>();
    }

    private static T GetFieldValue<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, InstanceFlags)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found on {target.GetType().FullName}.");
        return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
    }

    private static void SetFieldValue<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, InstanceFlags)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found on {target.GetType().FullName}.");
        field.SetValue(target, value);
    }

    private static T GetPropertyValue<T>(object target, string propertyName)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName, InstanceFlags)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().FullName}.");
        return (T)(property.GetValue(target) ?? throw new InvalidOperationException($"Property '{propertyName}' was null."));
    }

    private static void SetPropertyValue<T>(object target, string propertyName, T value)
    {
        PropertyInfo property = target.GetType().GetProperty(propertyName, InstanceFlags)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {target.GetType().FullName}.");
        property.SetValue(target, value);
    }

    private static IList GetParticleTrees(PhysicsChainComponent component)
        => GetFieldValue<IList>(component, "_particleTrees");

    private static IList GetParticles(object particleTree)
        => GetPropertyValue<IList>(particleTree, "Particles");

    private static object GetParticle(PhysicsChainComponent component, int treeIndex, int particleIndex)
        => GetParticles(GetParticleTrees(component)[treeIndex]!)[particleIndex]!;

    private static GPUPhysicsChainDispatcher.GPUParticleData[] CreateReadbackData(PhysicsChainComponent component, Vector3 firstPosition)
    {
        IList particleTrees = GetParticleTrees(component);
        int particleCount = 0;
        foreach (object particleTree in particleTrees)
            particleCount += GetParticles(particleTree).Count;

        var readback = new GPUPhysicsChainDispatcher.GPUParticleData[particleCount];
        for (int i = 0; i < readback.Length; ++i)
        {
            Vector3 position = i == 0 ? firstPosition : new Vector3(i, -i, i * 0.5f);
            readback[i] = new GPUPhysicsChainDispatcher.GPUParticleData
            {
                Position = position,
                PrevPosition = position + Vector3.One,
                PreviousPhysicsPosition = position - Vector3.One,
                IsColliding = i % 2,
            };
        }

        return readback;
    }

    private static (SceneNode rootNode, Transform rootBone, Transform[] childBones) CreateBoneHierarchy(int boneCount = 5, float boneLength = 0.1f)
    {
        var rootNode = new SceneNode("PhysicsChainRoot");
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

    private static PhysicsChainComponent CreateComponent(SceneNode node, Transform root)
    {
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Root = root;
        component.Damping = 0.1f;
        component.Elasticity = 0.1f;
        component.Stiffness = 0.1f;
        component.Inert = 0.0f;
        component.Friction = 0.5f;
        component.Radius = 0.02f;
        component.Gravity = new Vector3(0, -9.8f, 0);
        component.BlendWeight = 1.0f;
        return component;
    }

    #endregion

    #region Default State

    [Test]
    public void Constructor_SetsExpectedDefaults()
    {
        var c = new PhysicsChainComponent();

        // Physics parameters
        c.Damping.ShouldBe(0.1f);
        c.Elasticity.ShouldBe(0.1f);
        c.Stiffness.ShouldBe(0.1f);
        c.Inert.ShouldBe(0.0f);
        c.Friction.ShouldBe(0.0f);
        c.Radius.ShouldBe(0.01f);
        c.BlendWeight.ShouldBe(1.0f);

        // Root / hierarchy
        c.Root.ShouldBeNull();
        c.Roots.ShouldBeNull();
        c.RootBone.ShouldBeNull();
        c.RootInertia.ShouldBe(0.0f);
        c.VelocitySmoothing.ShouldBe(0.0f);

        // Modes
        c.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.Default);
        c.InterpolationMode.ShouldBe(PhysicsChainComponent.EInterpolationMode.Discrete);
        c.UpdateRate.ShouldBeGreaterThan(0);
        c.Speed.ShouldBe(1.0f);
        c.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.None);

        // Distance disable
        c.DistantDisable.ShouldBeFalse();
        c.DistanceToObject.ShouldBeGreaterThan(0);
        c.ReferenceObject.ShouldBeNull();

        // Optional features
        c.Colliders.ShouldBeNull();
        c.DampingDistrib.ShouldBeNull();
        c.EndLength.ShouldBe(0.0f);
        c.EndOffset.ShouldBe(Vector3.Zero);
        c.Multithread.ShouldBeFalse();
    }

    [Test]
    public void UpdateRate_WhenSetNegative_ClampsWithoutRecursing()
    {
        var component = new PhysicsChainComponent();

        component.UpdateRate = -1.0f;

        component.UpdateRate.ShouldBe(0.0f);
    }

    #endregion

    #region Particle Setup

    [Test]
    public void SetupParticles_CreatesParticlesForBoneHierarchy()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(5);
        var component = CreateComponent(node, rootBone);

        component.SetupParticles();

        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void SetupParticles_UsesRootsListWhenProvided()
    {
        var (node, _, childBones) = CreateBoneHierarchy(3);
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Roots = [childBones[0], childBones[1]];

        component.SetupParticles();

        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void SetupParticles_ExcludesTransformsInExclusionsList()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Root = rootBone;
        component.Exclusions = [childBones[2]];

        component.SetupParticles();

        component.Weight.ShouldBe(1.0f);
    }

    #endregion

    #region Weight Management

    [Test]
    public void SetWeight_UpdatesWeight()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.SetupParticles();

        component.SetWeight(0.5f);
        component.Weight.ShouldBe(0.5f);

        component.SetWeight(1.0f);
        component.Weight.ShouldBe(1.0f);

        component.SetWeight(0.0f);
        component.Weight.ShouldBe(0.0f);
    }

    [Test]
    public void BlendWeight_SyncsWithSetWeight()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.SetupParticles();

        component.BlendWeight = 0.75f;
        component.SetWeight(component.BlendWeight);
        component.Weight.ShouldBe(0.75f);
    }

    #endregion

    #region Virtual End Bones

    [Test]
    public void EndLength_CreatesVirtualEndBone()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.EndLength = 0.05f;

        component.SetupParticles();

        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void EndOffset_CreatesVirtualEndBone()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.EndOffset = new Vector3(0, -0.05f, 0);

        component.SetupParticles();

        component.Weight.ShouldBe(1.0f);
    }

    #endregion

    #region Distribution Curves

    [Test]
    public void AllDistributionCurves_AcceptCurves()
    {
        var component = new PhysicsChainComponent();
        var curve = new AnimationCurve();

        component.DampingDistrib = curve;
        component.ElasticityDistrib = curve;
        component.StiffnessDistrib = curve;
        component.InertDistrib = curve;
        component.FrictionDistrib = curve;
        component.RadiusDistrib = curve;

        component.DampingDistrib.ShouldNotBeNull();
        component.ElasticityDistrib.ShouldNotBeNull();
        component.StiffnessDistrib.ShouldNotBeNull();
        component.InertDistrib.ShouldNotBeNull();
        component.FrictionDistrib.ShouldNotBeNull();
        component.RadiusDistrib.ShouldNotBeNull();
    }

    #endregion

    #region Integration Tests

    [Test]
    public void FullSetup_WithAllParameters_DoesNotThrow()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(10);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Root = rootBone;
        component.Damping = 0.2f;
        component.Elasticity = 0.15f;
        component.Stiffness = 0.25f;
        component.Inert = 0.1f;
        component.Friction = 0.3f;
        component.Radius = 0.03f;
        component.Gravity = new Vector3(0, -10f, 0);
        component.Force = new Vector3(1, 0, 0);
        component.BlendWeight = 0.9f;
        component.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
        component.UpdateRate = 60f;
        component.FreezeAxis = PhysicsChainComponent.EFreezeAxis.Y;
        component.EndLength = 0.02f;
        component.RootBone = characterRoot;
        component.RootInertia = 0.7f;
        component.VelocitySmoothing = 0.3f;
        component.DistantDisable = true;
        component.DistanceToObject = 50f;
        component.Multithread = true;

        Should.NotThrow(() => component.SetupParticles());
        component.Weight.ShouldBe(0.9f);
    }

    [Test]
    public void CombinedRootTrackingAndSmoothing_WorksTogether()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(5);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 0.8f;
        component.VelocitySmoothing = 0.4f;

        component.SetupParticles();

        component.RootBone.ShouldNotBeNull();
        component.RootInertia.ShouldBe(0.8f);
        component.VelocitySmoothing.ShouldBe(0.4f);
    }

    [Test]
    public void RequiresGpuReadback_FollowsCompatibilitySyncMode()
    {
        var component = new PhysicsChainComponent
        {
            GpuSyncToBones = false,
        };

        RequiresGpuReadbackMethod(component).ShouldBeFalse();

        component.GpuSyncToBones = true;

        RequiresGpuReadbackMethod(component).ShouldBeTrue();
    }

    [Test]
    public void ApplyReadbackData_IgnoresStaleGenerationAndSubmissionIds()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.UseGPU = true;
        component.GpuSyncToBones = true;
        component.SetupParticles();

        SetFieldValue(component, "_gpuExecutionGeneration", 4);
        SetFieldValue(component, "_lastAppliedGpuSubmissionId", 7L);
        SetFieldValue(component, "_hasPendingGpuBoneSync", false);

        object particle = GetParticle(component, 0, 0);
        Vector3 initialPosition = GetPropertyValue<Vector3>(particle, "Position");

        GPUPhysicsChainDispatcher.GPUParticleData[] staleGeneration = CreateReadbackData(component, new Vector3(10.0f, 0.0f, 0.0f));
        component.ApplyReadbackData(staleGeneration, generation: 3, submissionId: 8L);
        GetPropertyValue<Vector3>(particle, "Position").ShouldBe(initialPosition);

        GPUPhysicsChainDispatcher.GPUParticleData[] staleSubmission = CreateReadbackData(component, new Vector3(20.0f, 0.0f, 0.0f));
        component.ApplyReadbackData(staleSubmission, generation: 4, submissionId: 7L);
        GetPropertyValue<Vector3>(particle, "Position").ShouldBe(initialPosition);

        GPUPhysicsChainDispatcher.GPUParticleData[] freshReadback = CreateReadbackData(component, new Vector3(30.0f, 0.0f, 0.0f));
        component.ApplyReadbackData(freshReadback, generation: 4, submissionId: 8L);

        GetPropertyValue<Vector3>(particle, "Position").ShouldBe(new Vector3(30.0f, 0.0f, 0.0f));
        GetFieldValue<long>(component, "_lastAppliedGpuSubmissionId").ShouldBe(8L);
        GetFieldValue<bool>(component, "_hasPendingGpuBoneSync").ShouldBeTrue();
    }

    [Test]
    public void ApplyReadbackData_ZeroReadbackModeUpdatesParticlesWithoutSchedulingBoneSync()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.UseGPU = true;
        component.GpuSyncToBones = false;
        component.SetupParticles();

        SetFieldValue(component, "_gpuExecutionGeneration", 2);
        SetFieldValue(component, "_lastAppliedGpuSubmissionId", 0L);
        SetFieldValue(component, "_hasPendingGpuBoneSync", false);

        object particle = GetParticle(component, 0, 0);
        GPUPhysicsChainDispatcher.GPUParticleData[] readback = CreateReadbackData(component, new Vector3(5.0f, 6.0f, 7.0f));

        component.ApplyReadbackData(readback, generation: 2, submissionId: 1L);

        GetPropertyValue<Vector3>(particle, "Position").ShouldBe(new Vector3(5.0f, 6.0f, 7.0f));
        GetFieldValue<bool>(component, "_hasPendingGpuBoneSync").ShouldBeFalse();
    }

    [Test]
    public void PrepareGpuData_TracksSingleTransformDirtyRange()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(4);
        var component = CreateComponent(node, rootBone);
        component.UseGPU = true;
        component.SetupParticles();

        PrepareGpuDataMethod(component);
        PrepareGpuDataMethod(component);

        GetFieldValue<int>(component, "_preparedTransformDirtyLength").ShouldBe(0);

        object particle = GetParticle(component, 0, 1);
        Matrix4x4 updatedMatrix = Matrix4x4.CreateTranslation(new Vector3(0.25f, -0.5f, 0.75f));
        SetPropertyValue(particle, "TransformLocalToWorldMatrix", updatedMatrix);

        PrepareGpuDataMethod(component);

        GetFieldValue<int>(component, "_preparedTransformDirtyStart").ShouldBe(1);
        GetFieldValue<int>(component, "_preparedTransformDirtyLength").ShouldBe(1);
    }

    [Test]
    public void UpdatePerTreeParams_WarmPathDoesNotAllocateManagedState()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.UseGPU = true;
        component.SetupParticles();

        InitializeBuffersMethod(component);
        UpdatePerTreeParamsMethod(component, 0.016f);

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; ++i)
            UpdatePerTreeParamsMethod(component, 0.016f);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;

        allocatedBytes.ShouldBeLessThanOrEqualTo(128L);
    }

    [Test]
    public void TryAddGpuDrivenRendererState_RegistersGpuDrivenBonesWhenCpuSyncIsDisabled()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.UseGPU = true;
        component.GpuSyncToBones = false;

        XRMesh mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        mesh.UtilizedBones =
        [
            (rootBone, Matrix4x4.Identity),
            (childBones[0], Matrix4x4.Identity)
        ];

        var renderer = new XRMeshRenderer(mesh, null);
        var particleIndexByTransform = new Dictionary<Transform, int>
        {
            [rootBone] = 0,
            [childBones[0]] = 1,
        };
        var firstChildIndexByParticle = new Dictionary<int, int>
        {
            [0] = 1,
        };
        var restDirectionByParticle = new Dictionary<int, Vector3>
        {
            [0] = new(0.0f, -0.1f, 0.0f),
        };

        TryAddGpuDrivenRendererStateMethod(component, renderer, particleIndexByTransform, firstChildIndexByParticle, restDirectionByParticle);

        renderer.HasGpuDrivenBoneSource.ShouldBeTrue();

        ClearGpuDrivenRendererBindingsMethod(component);

        renderer.HasGpuDrivenBoneSource.ShouldBeFalse();
    }

    #endregion
}
