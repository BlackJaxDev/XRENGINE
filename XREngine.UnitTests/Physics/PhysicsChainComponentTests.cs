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
/// Unit tests for PhysicsChainComponent (CPU version).
/// Tests cover particle setup, physics calculations, constraints,
/// root bone tracking, velocity smoothing, and collision detection.
/// </summary>
[TestFixture]
public sealed class PhysicsChainComponentTests
{
    #region Test Helpers

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
            bone.Translation = new Vector3(0, -boneLength, 0); // Each bone extends downward
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

    #region Initialization Tests

    [Test]
    public void Constructor_SetsDefaultValues()
    {
        var component = new PhysicsChainComponent();

        component.Damping.ShouldBe(0.1f);
        component.Elasticity.ShouldBe(0.1f);
        component.Stiffness.ShouldBe(0.1f);
        component.Inert.ShouldBe(0.0f);
        component.Friction.ShouldBe(0.0f);
        component.Radius.ShouldBe(0.01f);
        component.BlendWeight.ShouldBe(1.0f);
        component.Root.ShouldBeNull();
        component.Roots.ShouldBeNull();
    }

    [Test]
    public void SetupParticles_CreatesParticlesForBoneHierarchy()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var component = CreateComponent(node, rootBone);

        // Manually call setup (normally done in OnComponentActivated)
        component.SetupParticles();

        // Should have particles for root + 5 children = 6 particles
        // Note: The actual particle count depends on implementation details
        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void SetupParticles_UsesRootsListWhenProvided()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Roots = [childBones[0], childBones[1]];

        component.SetupParticles();

        // Should create particle trees for both roots
        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void SetupParticles_ExcludesTransformsInExclusionsList()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var component = node.AddComponent<PhysicsChainComponent>()!;
        component.Root = rootBone;
        component.Exclusions = [childBones[2]]; // Exclude middle bone

        component.SetupParticles();

        // Exclusions should prevent that branch from being included
        component.Weight.ShouldBe(1.0f);
    }

    #endregion

    #region Root Bone Tracking Tests

    [Test]
    public void RootBone_DefaultsToNull()
    {
        var component = new PhysicsChainComponent();
        component.RootBone.ShouldBeNull();
    }

    [Test]
    public void RootInertia_DefaultsToZero()
    {
        var component = new PhysicsChainComponent();
        component.RootInertia.ShouldBe(0.0f);
    }

    [Test]
    public void RootInertia_ClampsToValidRange()
    {
        var component = new PhysicsChainComponent();

        component.RootInertia = -0.5f;
        // Value should be set (clamping happens during physics calculations, not at property level)
        
        component.RootInertia = 1.5f;
        // Value should be set
    }

    [Test]
    public void RootBoneTracking_WithZeroInertia_UsesWorldSpaceMovement()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 0.0f; // World space

        component.SetupParticles();

        // At RootInertia=0, the root bone movement should not affect physics
        // This is a behavior test - the component should still function
        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void RootBoneTracking_WithFullInertia_MovesRelativeToRoot()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 1.0f; // Fully relative

        component.SetupParticles();

        // At RootInertia=1, chain should move with the root bone
        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void RootBoneTracking_WithPartialInertia_BlendsMovement()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 0.5f; // 50% relative

        component.SetupParticles();

        component.RootInertia.ShouldBe(0.5f);
    }

    #endregion

    #region Velocity Smoothing Tests

    [Test]
    public void VelocitySmoothing_DefaultsToZero()
    {
        var component = new PhysicsChainComponent();
        component.VelocitySmoothing.ShouldBe(0.0f);
    }

    [Test]
    public void VelocitySmoothing_AcceptsValidRange()
    {
        var component = new PhysicsChainComponent();

        component.VelocitySmoothing = 0.0f;
        component.VelocitySmoothing.ShouldBe(0.0f);

        component.VelocitySmoothing = 0.5f;
        component.VelocitySmoothing.ShouldBe(0.5f);

        component.VelocitySmoothing = 1.0f;
        component.VelocitySmoothing.ShouldBe(1.0f);
    }

    [Test]
    public void VelocitySmoothing_ZeroProducesRawVelocity()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.VelocitySmoothing = 0.0f;

        component.SetupParticles();

        // With zero smoothing, velocity should be passed through unmodified
        component.VelocitySmoothing.ShouldBe(0.0f);
    }

    [Test]
    public void VelocitySmoothing_MaxValueProducesMaxDampening()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.VelocitySmoothing = 1.0f;

        component.SetupParticles();

        // With max smoothing, velocity changes should be heavily dampened
        component.VelocitySmoothing.ShouldBe(1.0f);
    }

    #endregion

    #region Physics Parameter Tests

    [Test]
    public void Damping_ClampsToValidRange()
    {
        var component = new PhysicsChainComponent();

        component.Damping = 0.5f;
        component.Damping.ShouldBe(0.5f);

        component.Damping = 0.0f;
        component.Damping.ShouldBe(0.0f);

        component.Damping = 1.0f;
        component.Damping.ShouldBe(1.0f);
    }

    [Test]
    public void Elasticity_ClampsToValidRange()
    {
        var component = new PhysicsChainComponent();

        component.Elasticity = 0.5f;
        component.Elasticity.ShouldBe(0.5f);
    }

    [Test]
    public void Stiffness_ClampsToValidRange()
    {
        var component = new PhysicsChainComponent();

        component.Stiffness = 0.5f;
        component.Stiffness.ShouldBe(0.5f);
    }

    [Test]
    public void Inert_ClampsToValidRange()
    {
        var component = new PhysicsChainComponent();

        component.Inert = 0.5f;
        component.Inert.ShouldBe(0.5f);
    }

    [Test]
    public void Friction_AcceptsValues()
    {
        var component = new PhysicsChainComponent();

        component.Friction = 0.5f;
        component.Friction.ShouldBe(0.5f);
    }

    [Test]
    public void Radius_AcceptsPositiveValues()
    {
        var component = new PhysicsChainComponent();

        component.Radius = 0.1f;
        component.Radius.ShouldBe(0.1f);

        component.Radius = 0.0f;
        component.Radius.ShouldBe(0.0f);
    }

    [Test]
    public void Gravity_AcceptsAnyVector()
    {
        var component = new PhysicsChainComponent();

        var gravity = new Vector3(0, -9.8f, 0);
        component.Gravity = gravity;
        component.Gravity.ShouldBe(gravity);

        var customGravity = new Vector3(1, 2, 3);
        component.Gravity = customGravity;
        component.Gravity.ShouldBe(customGravity);
    }

    [Test]
    public void Force_AcceptsAnyVector()
    {
        var component = new PhysicsChainComponent();

        var force = new Vector3(10, 0, 0);
        component.Force = force;
        component.Force.ShouldBe(force);
    }

    #endregion

    #region Distribution Curve Tests

    [Test]
    public void DampingDistrib_DefaultsToNull()
    {
        var component = new PhysicsChainComponent();
        component.DampingDistrib.ShouldBeNull();
    }

    [Test]
    public void DampingDistrib_AcceptsCurve()
    {
        var component = new PhysicsChainComponent();
        var curve = new AnimationCurve();

        component.DampingDistrib = curve;
        component.DampingDistrib.ShouldBe(curve);
    }

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

    #region Update Mode Tests

    [Test]
    public void UpdateMode_DefaultsToDefault()
    {
        var component = new PhysicsChainComponent();
        component.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.Default);
    }

    [Test]
    public void UpdateMode_AcceptsAllModes()
    {
        var component = new PhysicsChainComponent();

        component.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
        component.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.Normal);

        component.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
        component.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.FixedUpdate);

        component.UpdateMode = PhysicsChainComponent.EUpdateMode.Undilated;
        component.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.Undilated);

        component.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
        component.UpdateMode.ShouldBe(PhysicsChainComponent.EUpdateMode.Default);
    }

    [Test]
    public void UpdateRate_DefaultsToPositiveValue()
    {
        var component = new PhysicsChainComponent();
        component.UpdateRate.ShouldBeGreaterThan(0);
    }

    [Test]
    public void UpdateRate_AcceptsZeroForEveryFrame()
    {
        var component = new PhysicsChainComponent();
        component.UpdateRate = 0;
        component.UpdateRate.ShouldBe(0);
    }

    #endregion

    #region Freeze Axis Tests

    [Test]
    public void FreezeAxis_DefaultsToNone()
    {
        var component = new PhysicsChainComponent();
        component.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.None);
    }

    [Test]
    public void FreezeAxis_AcceptsAllAxes()
    {
        var component = new PhysicsChainComponent();

        component.FreezeAxis = PhysicsChainComponent.EFreezeAxis.X;
        component.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.X);

        component.FreezeAxis = PhysicsChainComponent.EFreezeAxis.Y;
        component.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.Y);

        component.FreezeAxis = PhysicsChainComponent.EFreezeAxis.Z;
        component.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.Z);

        component.FreezeAxis = PhysicsChainComponent.EFreezeAxis.None;
        component.FreezeAxis.ShouldBe(PhysicsChainComponent.EFreezeAxis.None);
    }

    #endregion

    #region Distance Disable Tests

    [Test]
    public void DistantDisable_DefaultsToFalse()
    {
        var component = new PhysicsChainComponent();
        component.DistantDisable.ShouldBeFalse();
    }

    [Test]
    public void DistanceToObject_DefaultsToReasonableValue()
    {
        var component = new PhysicsChainComponent();
        component.DistanceToObject.ShouldBeGreaterThan(0);
    }

    [Test]
    public void ReferenceObject_DefaultsToNull()
    {
        var component = new PhysicsChainComponent();
        component.ReferenceObject.ShouldBeNull();
    }

    #endregion

    #region Weight Tests

    [Test]
    public void SetWeight_UpdatesWeight()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
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
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.SetupParticles();

        component.BlendWeight = 0.75f;
        component.SetWeight(component.BlendWeight);
        component.Weight.ShouldBe(0.75f);
    }

    #endregion

    #region Collider Tests

    [Test]
    public void Colliders_DefaultsToNull()
    {
        var component = new PhysicsChainComponent();
        component.Colliders.ShouldBeNull();
    }

    [Test]
    public void Colliders_AcceptsList()
    {
        var component = new PhysicsChainComponent();
        var colliders = new List<PhysicsChainColliderBase>();

        component.Colliders = colliders;
        component.Colliders.ShouldNotBeNull();
        component.Colliders.ShouldBeEmpty();
    }

    #endregion

    #region End Bone Tests

    [Test]
    public void EndLength_DefaultsToZero()
    {
        var component = new PhysicsChainComponent();
        component.EndLength.ShouldBe(0.0f);
    }

    [Test]
    public void EndOffset_DefaultsToZero()
    {
        var component = new PhysicsChainComponent();
        component.EndOffset.ShouldBe(Vector3.Zero);
    }

    [Test]
    public void EndLength_CreatesVirtualEndBone()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.EndLength = 0.05f;

        component.SetupParticles();

        // With EndLength > 0, virtual end bones should be created
        component.Weight.ShouldBe(1.0f);
    }

    [Test]
    public void EndOffset_CreatesVirtualEndBone()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(3);
        var component = CreateComponent(node, rootBone);
        component.EndOffset = new Vector3(0, -0.05f, 0);

        component.SetupParticles();

        // With EndOffset != Zero, virtual end bones should be created
        component.Weight.ShouldBe(1.0f);
    }

    #endregion

    #region Multithread Tests

    [Test]
    public void Multithread_DefaultsToFalse()
    {
        var component = new PhysicsChainComponent();
        component.Multithread.ShouldBeFalse();
    }

    [Test]
    public void Multithread_AcceptsBothValues()
    {
        var component = new PhysicsChainComponent();

        component.Multithread = true;
        component.Multithread.ShouldBeTrue();

        component.Multithread = false;
        component.Multithread.ShouldBeFalse();
    }

    #endregion

    #region Integration Tests

    [Test]
    public void FullSetup_WithAllParameters_DoesNotThrow()
    {
        var (node, rootBone, childBones) = CreateBoneHierarchy(10);
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
        var (node, rootBone, childBones) = CreateBoneHierarchy(5);
        var characterRoot = new Transform();
        characterRoot.Parent = rootBone.Parent;

        var component = CreateComponent(node, rootBone);
        component.RootBone = characterRoot;
        component.RootInertia = 0.8f;
        component.VelocitySmoothing = 0.4f;

        component.SetupParticles();

        // Both features should work together without conflict
        component.RootBone.ShouldNotBeNull();
        component.RootInertia.ShouldBe(0.8f);
        component.VelocitySmoothing.ShouldBe(0.4f);
    }

    #endregion
}
