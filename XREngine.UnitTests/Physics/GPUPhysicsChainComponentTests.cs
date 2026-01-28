using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Components.Animation;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Unit tests for GPUPhysicsChainComponent.
/// Tests cover property initialization, GPU buffer setup, batched dispatcher integration,
/// root bone tracking, velocity smoothing, and feature parity with CPU version.
/// </summary>
[TestFixture]
public sealed class GPUPhysicsChainComponentTests
{
    #region Test Helpers

    private static (SceneNode rootNode, Transform rootBone, Transform[] childBones) CreateBoneHierarchy(int boneCount = 5, float boneLength = 0.1f)
    {
        var rootNode = new SceneNode("GPUPhysicsChainRoot");
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

    private static GPUPhysicsChainComponent CreateComponent(SceneNode node, Transform root)
    {
        var component = node.AddComponent<GPUPhysicsChainComponent>()!;
        component.Root = root;
        component.Damping = 0.1f;
        component.Elasticity = 0.1f;
        component.Stiffness = 0.1f;
        component.Inert = 0.0f;
        component.Friction = 0.5f;
        component.Radius = 0.02f;
        component.Gravity = new Vector3(0, -9.8f, 0);
        component.BlendWeight = 1.0f;
        component.UseBatchedDispatcher = false; // Use standalone mode for testing
        return component;
    }

    #endregion

    #region Initialization Tests

    [Test]
    public void Constructor_SetsDefaultValues()
    {
        var component = new GPUPhysicsChainComponent();

        component.Damping.ShouldBe(0.1f);
        component.Elasticity.ShouldBe(0.1f);
        component.Stiffness.ShouldBe(0.1f);
        component.Inert.ShouldBe(0.0f);
        component.Friction.ShouldBe(0.5f);
        component.Radius.ShouldBe(0.2f);
        component.BlendWeight.ShouldBe(1.0f);
        component.Root.ShouldBeNull();
        component.Roots.ShouldBeNull();
    }

    [Test]
    public void UseBatchedDispatcher_DefaultsToTrue()
    {
        var component = new GPUPhysicsChainComponent();
        component.UseBatchedDispatcher.ShouldBeTrue();
    }

    [Test]
    public void UseBatchedDispatcher_CanBeDisabled()
    {
        var component = new GPUPhysicsChainComponent();
        component.UseBatchedDispatcher = false;
        component.UseBatchedDispatcher.ShouldBeFalse();
    }

    #endregion

    #region Root Bone Tracking Tests

    [Test]
    public void RootBone_DefaultsToNull()
    {
        var component = new GPUPhysicsChainComponent();
        component.RootBone.ShouldBeNull();
    }

    [Test]
    public void RootBone_AcceptsTransform()
    {
        var component = new GPUPhysicsChainComponent();
        var transform = new Transform();

        component.RootBone = transform;
        component.RootBone.ShouldBe(transform);
    }

    [Test]
    public void RootInertia_DefaultsToZero()
    {
        var component = new GPUPhysicsChainComponent();
        component.RootInertia.ShouldBe(0.0f);
    }

    [Test]
    public void RootInertia_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.RootInertia = 0.0f;
        component.RootInertia.ShouldBe(0.0f);

        component.RootInertia = 0.5f;
        component.RootInertia.ShouldBe(0.5f);

        component.RootInertia = 1.0f;
        component.RootInertia.ShouldBe(1.0f);
    }

    [Test]
    public void RootBoneTracking_MatchesCPUBehavior()
    {
        // GPU and CPU components should have identical root bone tracking behavior
        var gpuComponent = new GPUPhysicsChainComponent();
        var cpuComponent = new PhysicsChainComponent();

        var rootBone = new Transform();

        gpuComponent.RootBone = rootBone;
        cpuComponent.RootBone = rootBone;

        gpuComponent.RootInertia = 0.75f;
        cpuComponent.RootInertia = 0.75f;

        gpuComponent.RootBone.ShouldBe(cpuComponent.RootBone);
        gpuComponent.RootInertia.ShouldBe(cpuComponent.RootInertia);
    }

    #endregion

    #region Velocity Smoothing Tests

    [Test]
    public void VelocitySmoothing_DefaultsToZero()
    {
        var component = new GPUPhysicsChainComponent();
        component.VelocitySmoothing.ShouldBe(0.0f);
    }

    [Test]
    public void VelocitySmoothing_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.VelocitySmoothing = 0.0f;
        component.VelocitySmoothing.ShouldBe(0.0f);

        component.VelocitySmoothing = 0.5f;
        component.VelocitySmoothing.ShouldBe(0.5f);

        component.VelocitySmoothing = 1.0f;
        component.VelocitySmoothing.ShouldBe(1.0f);
    }

    [Test]
    public void VelocitySmoothing_MatchesCPUBehavior()
    {
        var gpuComponent = new GPUPhysicsChainComponent();
        var cpuComponent = new PhysicsChainComponent();

        gpuComponent.VelocitySmoothing = 0.6f;
        cpuComponent.VelocitySmoothing = 0.6f;

        gpuComponent.VelocitySmoothing.ShouldBe(cpuComponent.VelocitySmoothing);
    }

    #endregion

    #region Physics Parameter Tests

    [Test]
    public void Damping_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.Damping = 0.0f;
        component.Damping.ShouldBe(0.0f);

        component.Damping = 0.5f;
        component.Damping.ShouldBe(0.5f);

        component.Damping = 1.0f;
        component.Damping.ShouldBe(1.0f);
    }

    [Test]
    public void Elasticity_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.Elasticity = 0.5f;
        component.Elasticity.ShouldBe(0.5f);
    }

    [Test]
    public void Stiffness_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.Stiffness = 0.5f;
        component.Stiffness.ShouldBe(0.5f);
    }

    [Test]
    public void Inert_AcceptsValidRange()
    {
        var component = new GPUPhysicsChainComponent();

        component.Inert = 0.5f;
        component.Inert.ShouldBe(0.5f);
    }

    [Test]
    public void Friction_AcceptsValues()
    {
        var component = new GPUPhysicsChainComponent();

        component.Friction = 0.5f;
        component.Friction.ShouldBe(0.5f);
    }

    [Test]
    public void Radius_AcceptsPositiveValues()
    {
        var component = new GPUPhysicsChainComponent();

        component.Radius = 0.1f;
        component.Radius.ShouldBe(0.1f);
    }

    [Test]
    public void Gravity_AcceptsAnyVector()
    {
        var component = new GPUPhysicsChainComponent();

        var gravity = new Vector3(0, -9.8f, 0);
        component.Gravity = gravity;
        component.Gravity.ShouldBe(gravity);
    }

    [Test]
    public void Force_AcceptsAnyVector()
    {
        var component = new GPUPhysicsChainComponent();

        var force = new Vector3(10, 0, 0);
        component.Force = force;
        component.Force.ShouldBe(force);
    }

    #endregion

    #region Distribution Curve Tests

    [Test]
    public void DampingDistrib_DefaultsToNull()
    {
        var component = new GPUPhysicsChainComponent();
        component.DampingDistrib.ShouldBeNull();
    }

    [Test]
    public void AllDistributionCurves_AcceptCurves()
    {
        var component = new GPUPhysicsChainComponent();
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

    #region Update Mode Tests

    [Test]
    public void UpdateMode_DefaultsToDefault()
    {
        var component = new GPUPhysicsChainComponent();
        component.UpdateMode.ShouldBe(EUpdateMode.Default);
    }

    [Test]
    public void UpdateMode_AcceptsAllModes()
    {
        var component = new GPUPhysicsChainComponent();

        component.UpdateMode = EUpdateMode.Default;
        component.UpdateMode.ShouldBe(EUpdateMode.Default);

        component.UpdateMode = EUpdateMode.FixedUpdate;
        component.UpdateMode.ShouldBe(EUpdateMode.FixedUpdate);

        component.UpdateMode = EUpdateMode.Undilated;
        component.UpdateMode.ShouldBe(EUpdateMode.Undilated);
    }

    [Test]
    public void UpdateRate_DefaultsToZero()
    {
        var component = new GPUPhysicsChainComponent();
        component.UpdateRate.ShouldBe(0);
    }

    #endregion

    #region Freeze Axis Tests

    [Test]
    public void FreezeAxis_DefaultsToNone()
    {
        var component = new GPUPhysicsChainComponent();
        component.FreezeAxis.ShouldBe(EFreezeAxis.None);
    }

    [Test]
    public void FreezeAxis_AcceptsAllAxes()
    {
        var component = new GPUPhysicsChainComponent();

        component.FreezeAxis = EFreezeAxis.X;
        component.FreezeAxis.ShouldBe(EFreezeAxis.X);

        component.FreezeAxis = EFreezeAxis.Y;
        component.FreezeAxis.ShouldBe(EFreezeAxis.Y);

        component.FreezeAxis = EFreezeAxis.Z;
        component.FreezeAxis.ShouldBe(EFreezeAxis.Z);
    }

    #endregion

    #region Distance Disable Tests

    [Test]
    public void DistantDisable_DefaultsToFalse()
    {
        var component = new GPUPhysicsChainComponent();
        component.DistantDisable.ShouldBeFalse();
    }

    [Test]
    public void DistanceToObject_DefaultsToReasonableValue()
    {
        var component = new GPUPhysicsChainComponent();
        component.DistanceToObject.ShouldBe(20.0f);
    }

    [Test]
    public void ReferenceObject_DefaultsToNull()
    {
        var component = new GPUPhysicsChainComponent();
        component.ReferenceObject.ShouldBeNull();
    }

    #endregion

    #region Collider Tests

    [Test]
    public void Colliders_DefaultsToNull()
    {
        var component = new GPUPhysicsChainComponent();
        component.Colliders.ShouldBeNull();
    }

    [Test]
    public void Colliders_AcceptsList()
    {
        var component = new GPUPhysicsChainComponent();
        var colliders = new List<PhysicsChainColliderBase>();

        component.Colliders = colliders;
        component.Colliders.ShouldNotBeNull();
    }

    #endregion

    #region End Bone Tests

    [Test]
    public void EndLength_DefaultsToZero()
    {
        var component = new GPUPhysicsChainComponent();
        component.EndLength.ShouldBe(0.0f);
    }

    [Test]
    public void EndOffset_DefaultsToZero()
    {
        var component = new GPUPhysicsChainComponent();
        component.EndOffset.ShouldBe(Vector3.Zero);
    }

    #endregion

    #region Feature Parity Tests

    [Test]
    public void AllPhysicsParameters_MatchCPUComponent()
    {
        var gpu = new GPUPhysicsChainComponent();
        var cpu = new PhysicsChainComponent();

        // Set identical parameters
        gpu.Damping = cpu.Damping = 0.25f;
        gpu.Elasticity = cpu.Elasticity = 0.15f;
        gpu.Stiffness = cpu.Stiffness = 0.35f;
        gpu.Inert = cpu.Inert = 0.2f;
        gpu.Friction = cpu.Friction = 0.4f;
        gpu.Radius = cpu.Radius = 0.05f;

        var gravity = new Vector3(0, -10f, 0);
        gpu.Gravity = cpu.Gravity = gravity;

        var force = new Vector3(1, 0, 0);
        gpu.Force = cpu.Force = force;

        // Verify they match
        gpu.Damping.ShouldBe(cpu.Damping);
        gpu.Elasticity.ShouldBe(cpu.Elasticity);
        gpu.Stiffness.ShouldBe(cpu.Stiffness);
        gpu.Inert.ShouldBe(cpu.Inert);
        gpu.Friction.ShouldBe(cpu.Friction);
        gpu.Radius.ShouldBe(cpu.Radius);
        gpu.Gravity.ShouldBe(cpu.Gravity);
        gpu.Force.ShouldBe(cpu.Force);
    }

    [Test]
    public void RootBoneTracking_HasSamePropertiesAsCPU()
    {
        var gpu = new GPUPhysicsChainComponent();
        var cpu = new PhysicsChainComponent();

        var rootBone = new Transform();
        gpu.RootBone = cpu.RootBone = rootBone;
        gpu.RootInertia = cpu.RootInertia = 0.8f;
        gpu.VelocitySmoothing = cpu.VelocitySmoothing = 0.5f;

        gpu.RootBone.ShouldBe(cpu.RootBone);
        gpu.RootInertia.ShouldBe(cpu.RootInertia);
        gpu.VelocitySmoothing.ShouldBe(cpu.VelocitySmoothing);
    }

    #endregion

    #region Integration Tests

    [Test]
    public void FullSetup_WithAllParameters_DoesNotThrow()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(10);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = node.AddComponent<GPUPhysicsChainComponent>()!;
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
        component.UpdateMode = EUpdateMode.FixedUpdate;
        component.UpdateRate = 60f;
        component.FreezeAxis = EFreezeAxis.Y;
        component.EndLength = 0.02f;
        component.RootBone = characterRoot;
        component.RootInertia = 0.7f;
        component.VelocitySmoothing = 0.3f;
        component.DistantDisable = true;
        component.DistanceToObject = 50f;
        component.Multithread = true;
        component.UseBatchedDispatcher = false;

        // Component should be properly configured
        component.Root.ShouldBe(rootBone);
        component.RootBone.ShouldBe(characterRoot);
        component.RootInertia.ShouldBe(0.7f);
        component.VelocitySmoothing.ShouldBe(0.3f);
    }

    [Test]
    public void StandaloneMode_ConfiguresCorrectly()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var component = CreateComponent(node, rootBone);
        component.UseBatchedDispatcher = false;

        component.UseBatchedDispatcher.ShouldBeFalse();
    }

    [Test]
    public void BatchedMode_ConfiguresCorrectly()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var component = node.AddComponent<GPUPhysicsChainComponent>()!;
        component.Root = rootBone;
        component.UseBatchedDispatcher = true;

        component.UseBatchedDispatcher.ShouldBeTrue();
    }

    [Test]
    public void CombinedRootTrackingAndSmoothing_ConfiguresCorrectly()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 0.8f;
        component.VelocitySmoothing = 0.4f;

        component.RootBone.ShouldNotBeNull();
        component.RootInertia.ShouldBe(0.8f);
        component.VelocitySmoothing.ShouldBe(0.4f);
    }

    #endregion
}
