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
/// Tests for <see cref="GPUPhysicsChainComponent"/>: default state,
/// GPU/CPU feature parity, and full-parameter integration.
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
        component.UseBatchedDispatcher = false;
        return component;
    }

    #endregion

    #region Default State

    [Test]
    public void Constructor_SetsExpectedDefaults()
    {
        var c = new GPUPhysicsChainComponent();

        // Physics parameters
        c.Damping.ShouldBe(0.1f);
        c.Elasticity.ShouldBe(0.1f);
        c.Stiffness.ShouldBe(0.1f);
        c.Inert.ShouldBe(0.0f);
        c.Friction.ShouldBe(0.5f);
        c.Radius.ShouldBe(0.2f);
        c.BlendWeight.ShouldBe(1.0f);
        c.Gravity.ShouldBe(Vector3.Zero);
        c.Force.ShouldBe(Vector3.Zero);

        // Root / hierarchy
        c.Root.ShouldBeNull();
        c.Roots.ShouldBeNull();
        c.RootBone.ShouldBeNull();
        c.RootInertia.ShouldBe(0.0f);
        c.VelocitySmoothing.ShouldBe(0.0f);

        // Modes
        c.UseBatchedDispatcher.ShouldBeTrue();
        c.UpdateMode.ShouldBe(EUpdateMode.Default);
        c.UpdateRate.ShouldBe(0);
        c.FreezeAxis.ShouldBe(EFreezeAxis.None);

        // Distance disable
        c.DistantDisable.ShouldBeFalse();
        c.DistanceToObject.ShouldBe(20.0f);
        c.ReferenceObject.ShouldBeNull();

        // Optional features
        c.Colliders.ShouldBeNull();
        c.DampingDistrib.ShouldBeNull();
        c.EndLength.ShouldBe(0.0f);
        c.EndOffset.ShouldBe(Vector3.Zero);
    }

    #endregion

    #region GPU/CPU Feature Parity

    [Test]
    public void AllPhysicsParameters_MatchCPUComponent()
    {
        var gpu = new GPUPhysicsChainComponent();
        var cpu = new PhysicsChainComponent();

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
    public void RootBoneTracking_MatchesCPUBehavior()
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

    #region Integration Tests

    [Test]
    public void FullSetup_WithAllParameters_DoesNotThrow()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(10);
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

        component.Root.ShouldBe(rootBone);
        component.RootBone.ShouldBe(characterRoot);
        component.RootInertia.ShouldBe(0.7f);
        component.VelocitySmoothing.ShouldBe(0.3f);
    }

    [Test]
    public void StandaloneAndBatchedModes_ToggleCorrectly()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(5);
        var component = CreateComponent(node, rootBone);

        component.UseBatchedDispatcher = false;
        component.UseBatchedDispatcher.ShouldBeFalse();

        component.UseBatchedDispatcher = true;
        component.UseBatchedDispatcher.ShouldBeTrue();
    }

    [Test]
    public void CombinedRootTrackingAndSmoothing_ConfiguresCorrectly()
    {
        var (node, rootBone, _) = CreateBoneHierarchy(5);
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
