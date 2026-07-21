using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainSleepAndQualityTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestCase(PhysicsChainQualityTier.Strict, 72.0f, 72.0f, 1, true, PhysicsChainOutputCadence.EverySimulationStep)]
    [TestCase(PhysicsChainQualityTier.Hz30, 72.0f, 30.0f, 1, true, PhysicsChainOutputCadence.EverySimulationStep)]
    [TestCase(PhysicsChainQualityTier.Hz15, 72.0f, 15.0f, 1, true, PhysicsChainOutputCadence.EverySimulationStep)]
    [TestCase(PhysicsChainQualityTier.Hz7_5, 72.0f, 7.5f, 1, true, PhysicsChainOutputCadence.EverySimulationStep)]
    [TestCase(PhysicsChainQualityTier.Sleep, 72.0f, 0.0f, 0, false, PhysicsChainOutputCadence.Hold)]
    public void QualityPolicy_ResolvesNamedTierWithoutMagicCadenceValues(
        PhysicsChainQualityTier tier,
        float authoredRate,
        float expectedRate,
        int expectedSubsteps,
        bool expectedCollision,
        PhysicsChainOutputCadence expectedOutputCadence)
    {
        PhysicsChainQualityPolicy policy = PhysicsChainQualityPolicy.Resolve(tier, authoredRate);

        policy.Tier.ShouldBe(tier);
        policy.SimulationRateHz.ShouldBe(expectedRate);
        policy.SolverSubstepCount.ShouldBe(expectedSubsteps);
        policy.ConstraintIterationCount.ShouldBe(expectedSubsteps);
        policy.CollisionEnabled.ShouldBe(expectedCollision);
        policy.PaletteCadence.ShouldBe(expectedOutputCadence);
        policy.BoundsCadence.ShouldBe(expectedOutputCadence);
    }

    [TestCase(0, 1, false)]
    [TestCase(29, 30, false)]
    [TestCase(30, 30, true)]
    [TestCase(31, 30, true)]
    [TestCase(1, 0, true)]
    public void AutomaticSleep_EntersAtDeterministicMinimumQuietFrame(
        int quietFrames,
        int requiredQuietFrames,
        bool expected)
        => PhysicsChainComponent.ShouldEnterAutomaticSleep(quietFrames, requiredQuietFrames).ShouldBe(expected);

    [Test]
    public void ActivityErrorUsesMaximumNormalizedChannelAndEnterThreshold()
    {
        var thresholds = new PhysicsChainActivityThresholds(2.0f, 4.0f, 8.0f, 16.0f, 3.0f);
        var signals = new PhysicsChainActivitySignals(
            MaximumParticleVelocitySquared: 1.0f,
            MaximumConstraintErrorSquared: 4.0f,
            RootAccelerationSquared: 64.0f,
            ExternalForceMagnitudeSquared: 64.0f,
            ColliderChanged: false,
            RecentlyVisibleOrUsed: false);

        float errorSquared = PhysicsChainActivityEvaluation.ComputeNormalizedErrorSquared(signals, thresholds);

        errorSquared.ShouldBe(1.0f);
        PhysicsChainActivityEvaluation.IsQuiet(errorSquared).ShouldBeTrue();
    }

    [TestCase(1.01f, 2.0f, true)]
    [TestCase(3.99f, 2.0f, true)]
    [TestCase(4.0f, 2.0f, false)]
    public void SleepHysteresisRetainsSleepUntilWakeThreshold(
        float normalizedErrorSquared,
        float wakeMultiplier,
        bool expected)
        => PhysicsChainComponent.ShouldRetainSleep(normalizedErrorSquared, wakeMultiplier).ShouldBe(expected);

    [Test]
    public void RecentUseAndColliderChangeProduceWakeLevelActivity()
    {
        var thresholds = new PhysicsChainActivityThresholds(1.0f, 1.0f, 1.0f, 1.0f, 2.0f);
        var recentUse = new PhysicsChainActivitySignals(0.0f, 0.0f, 0.0f, 0.0f, false, true);
        var colliderChange = new PhysicsChainActivitySignals(0.0f, 0.0f, 0.0f, 0.0f, true, false);

        PhysicsChainActivityEvaluation.ComputeNormalizedErrorSquared(recentUse, thresholds).ShouldBe(4.0f);
        PhysicsChainActivityEvaluation.ComputeNormalizedErrorSquared(colliderChange, thresholds).ShouldBe(4.0f);
    }

    [Test]
    public void GameplaySignalsWakeWithDocumentedReasons()
    {
        var component = new PhysicsChainComponent();
        SetField(component, "_isRuntimeSleeping", true);
        component.NotifyExternalEvent();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.ForceOrEventInput);

        SetField(component, "_isRuntimeSleeping", true);
        component.NotifyVisibleOrUsed();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.VisibilityOrUse);
    }

    [Test]
    public void ExplicitWake_RecordsReasonAndCountsOnlySleepingTransition()
    {
        var component = new PhysicsChainComponent();
        SetField(component, "_isRuntimeSleeping", true);
        SetField(component, "_quietSimulationFrames", 12);

        component.Wake(PhysicsChainWakeReason.ExplicitRequest);

        component.IsRuntimeSleeping.ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.ExplicitRequest);
        component.WakeCount.ShouldBe(1UL);

        component.Wake(PhysicsChainWakeReason.ExplicitRequest);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void AuthoredForceChange_WakesSleepingCpuChainWithSpecificReason()
    {
        var component = new PhysicsChainComponent();
        SetField(component, "_isRuntimeSleeping", true);

        component.Force = Vector3.UnitX;

        component.IsRuntimeSleeping.ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.ExternalForceChanged);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void AuthoredSolverParameterChange_WakesSleepingCpuChain()
    {
        var component = new PhysicsChainComponent();
        SetField(component, "_isRuntimeSleeping", true);

        component.Damping = 0.25f;

        component.IsRuntimeSleeping.ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.AuthoredParameterChanged);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void ColliderConfigurationMutation_WakesSleepingCpuChain()
    {
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        var collider = new PhysicsChainSphereCollider();
        component.Colliders = [collider];

        int baselineSignature = Invoke<int>(component, "ComputeSleepColliderSignature");
        SetField(component, "_sleepColliderSignature", baselineSignature);
        SetField(component, "_sleepColliderShapeSignature", Invoke<int>(component, "ComputeSleepColliderShapeSignature"));
        SetField(component, "_sleepColliderPoseSignature", Invoke<int>(component, "ComputeSleepColliderPoseSignature"));
        SetField(component, "_sleepConfiguredRootSignature", Invoke<int>(component, "ComputeSleepConfiguredRootSignature"));
        SetField(component, "_sleepRootPosition", component.Transform.WorldTranslation);
        SetField(component, "_isRuntimeSleeping", true);
        collider.Radius = 2.0f;

        Invoke<bool>(component, "ShouldRemainSleeping").ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.ColliderShapeChanged);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void RootMovement_WakesSleepingCpuChain()
    {
        var node = new SceneNode("PhysicsChain");
        PhysicsChainComponent component = node.AddComponent<PhysicsChainComponent>()!;
        Transform transform = node.GetTransformAs<Transform>(true)!;
        SetField(component, "_sleepColliderSignature", Invoke<int>(component, "ComputeSleepColliderSignature"));
        SetField(component, "_sleepColliderShapeSignature", Invoke<int>(component, "ComputeSleepColliderShapeSignature"));
        SetField(component, "_sleepColliderPoseSignature", Invoke<int>(component, "ComputeSleepColliderPoseSignature"));
        SetField(component, "_sleepConfiguredRootSignature", Invoke<int>(component, "ComputeSleepConfiguredRootSignature"));
        SetField(component, "_sleepRootPosition", component.Transform.WorldTranslation);
        SetField(component, "_isRuntimeSleeping", true);

        transform.Translation += Vector3.UnitX;
        transform.RecalculateMatrices();

        Invoke<bool>(component, "ShouldRemainSleeping").ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.RootTeleport);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void ConfiguredRootMovement_WakesSleepingCpuChain()
    {
        var componentNode = new SceneNode("PhysicsChain");
        var configuredRootNode = new SceneNode("ConfiguredRoot");
        PhysicsChainComponent component = componentNode.AddComponent<PhysicsChainComponent>()!;
        Transform configuredRoot = configuredRootNode.GetTransformAs<Transform>(true)!;
        component.Root = configuredRoot;
        SetField(component, "_sleepColliderSignature", Invoke<int>(component, "ComputeSleepColliderSignature"));
        SetField(component, "_sleepColliderShapeSignature", Invoke<int>(component, "ComputeSleepColliderShapeSignature"));
        SetField(component, "_sleepColliderPoseSignature", Invoke<int>(component, "ComputeSleepColliderPoseSignature"));
        SetField(component, "_sleepConfiguredRootSignature", Invoke<int>(component, "ComputeSleepConfiguredRootSignature"));
        SetField(component, "_sleepRootPosition", component.Transform.WorldTranslation);
        SetField(component, "_isRuntimeSleeping", true);

        configuredRoot.Translation += Vector3.UnitY;
        configuredRoot.RecalculateMatrices();

        Invoke<bool>(component, "ShouldRemainSleeping").ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.RootMovement);
        component.WakeCount.ShouldBe(1UL);
    }

    [Test]
    public void ColliderPoseMutation_WakesSleepingCpuChain()
    {
        var componentNode = new SceneNode("PhysicsChain");
        var colliderNode = new SceneNode("Collider");
        PhysicsChainComponent component = componentNode.AddComponent<PhysicsChainComponent>()!;
        PhysicsChainSphereCollider collider = colliderNode.AddComponent<PhysicsChainSphereCollider>()!;
        component.Colliders = [collider];
        SetField(component, "_sleepColliderShapeSignature", Invoke<int>(component, "ComputeSleepColliderShapeSignature"));
        SetField(component, "_sleepColliderPoseSignature", Invoke<int>(component, "ComputeSleepColliderPoseSignature"));
        SetField(component, "_sleepConfiguredRootSignature", Invoke<int>(component, "ComputeSleepConfiguredRootSignature"));
        SetField(component, "_sleepRootPosition", component.Transform.WorldTranslation);
        SetField(component, "_sleepLastRootPosition", component.Transform.WorldTranslation);
        SetField(component, "_isRuntimeSleeping", true);

        Transform colliderTransform = colliderNode.GetTransformAs<Transform>(true)!;
        colliderTransform.Translation += Vector3.UnitZ;
        colliderTransform.RecalculateMatrices();

        Invoke<bool>(component, "ShouldRemainSleeping").ShouldBeFalse();
        component.LastWakeReason.ShouldBe(PhysicsChainWakeReason.ColliderPoseChanged);
    }

    private static void SetField<T>(PhysicsChainComponent component, string fieldName, T value)
    {
        FieldInfo field = typeof(PhysicsChainComponent).GetField(fieldName, InstanceFlags)
            ?? throw new MissingFieldException(typeof(PhysicsChainComponent).FullName, fieldName);
        field.SetValue(component, value);
    }

    private static T Invoke<T>(PhysicsChainComponent component, string methodName)
    {
        MethodInfo method = typeof(PhysicsChainComponent).GetMethod(methodName, InstanceFlags)
            ?? throw new MissingMethodException(typeof(PhysicsChainComponent).FullName, methodName);
        return (T)method.Invoke(component, null)!;
    }
}
