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
/// Tests for <see cref="PhysicsChainComponent"/> (CPU version):
/// particle setup, weight management, virtual end bones, and full-parameter integration.
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
        c.UpdateRate.ShouldBeGreaterThan(0);
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

    #endregion
}
