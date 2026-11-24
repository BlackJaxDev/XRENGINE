using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.Procedural;

[TestFixture]
public sealed class ProceduralSecondaryAndPhysicalAnimationTests
{
    private static readonly MethodInfo TryPreparePlaybackMethod =
        typeof(AnimStateMachineComponent).GetMethod("TryPrepareStateMachinePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo EvaluateAndApplyMethod =
        typeof(AnimStateMachineComponent).GetMethod("EvaluateAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ApplyMusclePoseMethod =
        typeof(HumanoidComponent).GetMethod("ApplyCurrentMusclePose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void MotionFactoryRegistry_AutoRegistersAllProceduralMotionTypes()
    {
        Type[] registered = MotionFactoryRegistry.GetRegisteredTypes();
        registered.ShouldContain(typeof(HumanoidLookAtAimMotion));
        registered.ShouldContain(typeof(ProceduralSecondaryMotion));
        registered.ShouldContain(typeof(ProceduralStrideMotion));
        registered.ShouldContain(typeof(MotionWarpingMotion));

        MotionFactoryRegistry.TryCreate(typeof(HumanoidLookAtAimMotion), out MotionBase? lookAt).ShouldBeTrue();
        lookAt.ShouldBeOfType<HumanoidLookAtAimMotion>();

        MotionFactoryRegistry.TryCreate(typeof(ProceduralSecondaryMotion), out MotionBase? secondary).ShouldBeTrue();
        secondary.ShouldBeOfType<ProceduralSecondaryMotion>();

        MotionFactoryRegistry.TryCreate(typeof(ProceduralStrideMotion), out MotionBase? stride).ShouldBeTrue();
        stride.ShouldBeOfType<ProceduralStrideMotion>();

        MotionFactoryRegistry.TryCreate(typeof(MotionWarpingMotion), out MotionBase? warp).ShouldBeTrue();
        warp.ShouldBeOfType<MotionWarpingMotion>();
    }

    [Test]
    public void HumanoidLookAtAimMotion_DistributesYawPitchAcrossSpineNeckHeadAndOptionalArms()
    {
        SceneNode root = CreateMinimalHumanoidRig(out HumanoidComponent humanoid);
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;

        var lookAt = new HumanoidLookAtAimMotion { Name = "LookAt" };
        var state = new AnimState(lookAt, "LookAt");
        animator.StateMachine.Layers.Add(new AnimLayer(state)
        {
            InitialState = state,
            ApplyType = AnimLayer.EApplyType.Override,
            Weight = 1.0f,
        });
        animator.StateMachine.Variables["AimYaw"] = new AnimFloat("AimYaw", 0.8f);
        animator.StateMachine.Variables["AimPitch"] = new AnimFloat("AimPitch", 0.4f);
        animator.StateMachine.Variables["AimWeight"] = new AnimFloat("AimWeight", 1.0f);
        animator.StateMachine.Variables["ArmAimWeight"] = new AnimFloat("ArmAimWeight", 0.0f);

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        long dt60 = Stopwatch.Frequency / 60;
        for (int i = 0; i < 40; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
        }

        humanoid.TryGetMuscleValue(EHumanoidValue.HeadTurnLeftRight, out float headYaw).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineTwistLeftRight, out float spineYaw).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.RightArmDownUp, out float rightArmZeroWeight).ShouldBeTrue();
        headYaw.ShouldBeGreaterThan(0.05f);
        spineYaw.ShouldBeGreaterThan(0.05f);
        MathF.Abs(rightArmZeroWeight).ShouldBeLessThan(1.0e-4f);

        // Enable arm aim offset and verify arm channels activate without affecting legs.
        animator.SetFloat("ArmAimWeight", 1.0f);
        EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
        ApplyMusclePoseMethod.Invoke(humanoid, null);

        humanoid.TryGetMuscleValue(EHumanoidValue.RightArmDownUp, out float rightArmAimed).ShouldBeTrue();
        rightArmAimed.ShouldBeGreaterThan(0.05f);

        // Steady-state evaluation allocates zero bytes.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++)
        {
            lookAt.Tick(1.0f / 60.0f);
            lookAt.GetAnimationValues(null, animator.StateMachine.Variables, 1.0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0L);
    }

    [Test]
    public void HumanoidLookTargetDriverComponent_WrapsYawClampsPitchAndAttenuatesBehindAvatar()
    {
        var root = new SceneNode("AvatarRoot", new Transform());
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.StateMachine.Variables["AimYaw"] = new AnimFloat("AimYaw", 0.0f);
        animator.StateMachine.Variables["AimPitch"] = new AnimFloat("AimPitch", 0.0f);
        animator.StateMachine.Variables["AimWeight"] = new AnimFloat("AimWeight", 1.0f);

        var driver = root.AddComponent<HumanoidLookTargetDriverComponent>()!;
        driver.Animator = animator;
        driver.AvatarFacingTransform = root.Transform;
        driver.UseExplicitWorldPosition = true;
        driver.MaxPitchDegrees = 45.0f;

        // Target directly right and steep upward -> yaw ~ +90 deg, pitch clamped to +45 deg.
        driver.TargetWorldPosition = new Vector3(1.0f, 10.0f, 0.0f);
        driver.StepTargetUpdate(0.0f);
        animator.StateMachine.Variables["AimYaw"].FloatValue.ShouldBe(MathF.PI * 0.5f, 0.05f);
        animator.StateMachine.Variables["AimPitch"].FloatValue.ShouldBe(45.0f * (MathF.PI / 180.0f), 0.02f);
        animator.StateMachine.Variables["AimWeight"].FloatValue.ShouldBe(1.0f, 0.02f);

        // Target directly behind (-Z) -> weight attenuates smoothly to 0.
        driver.TargetWorldPosition = new Vector3(0.0f, 0.0f, -5.0f);
        for (int i = 0; i < 60; i++)
            driver.StepTargetUpdate(1.0f / 60.0f);
        animator.StateMachine.Variables["AimWeight"].FloatValue.ShouldBeLessThan(0.15f);
    }

    [Test]
    public void ProceduralSecondaryMotion_RestZeroWhenBreathingDisabledAndBoundedUnderAccelerationSpikes()
    {
        SceneNode root = CreateMinimalHumanoidRig(out HumanoidComponent humanoid);
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;

        var secondary = new ProceduralSecondaryMotion
        {
            Name = "Secondary",
            EnableBreathing = false,
            MaxLeanAmplitude = 0.20f,
            MaxBankAmplitude = 0.18f,
        };
        var state = new AnimState(secondary, "Secondary");
        animator.StateMachine.Layers.Add(new AnimLayer(state)
        {
            InitialState = state,
            ApplyType = AnimLayer.EApplyType.Override,
            Weight = 1.0f,
        });
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["Exertion"] = new AnimFloat("Exertion", 0.0f);

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        long dt60 = Stopwatch.Frequency / 60;
        EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
        ApplyMusclePoseMethod.Invoke(humanoid, null);

        // At rest with breathing disabled, all secondary outputs remain exact zero.
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float restSpine).ShouldBeTrue();
        humanoid.TryGetMuscleValue(EHumanoidValue.SpineLeftRight, out float restBank).ShouldBeTrue();
        restSpine.ShouldBe(0.0f, 1.0e-5f);
        restBank.ShouldBe(0.0f, 1.0e-5f);

        // Spike acceleration and turn rate sharply; values stay strictly clamped within bounds.
        animator.SetFloat("MoveY", 1.0f);
        animator.SetFloat("DesiredFacingYaw", 2.5f);
        for (int i = 0; i < 15; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            humanoid.TryGetMuscleValue(EHumanoidValue.SpineFrontBack, out float lean).ShouldBeTrue();
            humanoid.TryGetMuscleValue(EHumanoidValue.SpineLeftRight, out float bank).ShouldBeTrue();
            MathF.Abs(lean).ShouldBeLessThanOrEqualTo(0.201f);
            MathF.Abs(bank).ShouldBeLessThanOrEqualTo(0.181f);
        }

        // Zero per-frame allocations during steady-state evaluation.
        secondary.EnableBreathing = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++)
        {
            secondary.Tick(1.0f / 60.0f);
            secondary.GetAnimationValues(null, animator.StateMachine.Variables, 1.0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0L);
    }

    [Test]
    public void HumanoidPhysicalAnimationComponent_RealJoltScene_DeflectsOnHitImpulseAndRecoversToTargetPose()
    {
        var joltScene = new JoltScene();
        joltScene.Initialize();
        try
        {
            var root = new SceneNode("PhysicalAvatar", new Transform());
            var chestNode = root.NewChildWithTransform<Transform>(out Transform chestTransform, "Chest");
            chestTransform.Translation = new Vector3(0.0f, 1.2f, 0.0f);
            chestTransform.RecalculateMatrices(true);

            var legNode = root.NewChildWithTransform<Transform>(out Transform legTransform, "LeftLeg");
            legTransform.Translation = new Vector3(-0.15f, 0.5f, 0.0f);
            legTransform.RecalculateMatrices(true);

            var chestBody = joltScene.CreateDynamicRigidBody(
                new IPhysicsGeometry.Sphere(0.14f),
                (chestTransform.WorldTranslation, Quaternion.Identity),
                Vector3.Zero,
                Quaternion.Identity,
                LayerMask.Everything)!;
            chestBody.GravityEnabled = false;

            var legBody = joltScene.CreateDynamicRigidBody(
                new IPhysicsGeometry.Sphere(0.10f),
                (legTransform.WorldTranslation, Quaternion.Identity),
                Vector3.Zero,
                Quaternion.Identity,
                LayerMask.Everything)!;
            legBody.GravityEnabled = false;

            var physAnim = root.AddComponent<HumanoidPhysicalAnimationComponent>()!;
            physAnim.WritePhysicsPoseToBones = false;
            physAnim.BindDynamicBody("Chest", EHumanoidBodyMask.Body, chestTransform, chestBody);
            physAnim.BindDynamicBody("LeftLeg", EHumanoidBodyMask.LeftLeg, legTransform, legBody);

            // Hit upper body only; chest deflects in -Z while leg remains unaffected.
            physAnim.TriggerHitReaction(
                EHumanoidBodyMask.UpperBody,
                worldLinearImpulse: new Vector3(0.0f, 0.0f, -6.0f),
                complianceDurationSeconds: 0.35f,
                complianceWeightScale: 0.15f);

            const float dt = 1.0f / 60.0f;
            float maxDeflectionZ = 0.0f;
            for (int i = 0; i < 12; i++)
            {
                physAnim.StepPhysicalAnimation(dt);
                joltScene.StepSimulation();
                maxDeflectionZ = MathF.Max(maxDeflectionZ, MathF.Abs(chestBody.Transform.position.Z));
            }

            maxDeflectionZ.ShouldBeGreaterThan(0.10f);
            MathF.Abs(legBody.Transform.position.Z).ShouldBeLessThan(0.02f);

            // Continue stepping so compliance decays and PD springs pull chest back toward target Z=0.
            for (int i = 0; i < 90; i++)
            {
                physAnim.StepPhysicalAnimation(dt);
                joltScene.StepSimulation();
            }

            MathF.Abs(chestBody.Transform.position.Z).ShouldBeLessThan(maxDeflectionZ * 0.35f);

            // Ragdoll entry + smooth get-up recovery ramp.
            physAnim.EnterRagdoll();
            physAnim.IsRagdoll.ShouldBeTrue();
            physAnim.MasterBlendWeight.ShouldBe(0.0f);
            chestBody.GravityEnabled.ShouldBeTrue();

            physAnim.BeginGetUpRecovery(0.30f);
            physAnim.IsRagdoll.ShouldBeFalse();
            for (int i = 0; i < 25; i++)
                physAnim.StepPhysicalAnimation(dt);
            physAnim.MasterBlendWeight.ShouldBe(1.0f, 1.0e-3f);

            // Profile MemoryPack round-trip.
            byte[] bytes = MemoryPackSerializer.Serialize(physAnim.RegionProfiles);
            var restored = MemoryPackSerializer.Deserialize<List<HumanoidPhysicalRegionProfile>>(bytes);
            restored.ShouldNotBeNull();
            restored.Count.ShouldBe(physAnim.RegionProfiles.Count);
        }
        finally
        {
            joltScene.Destroy();
        }
    }

    private static SceneNode CreateMinimalHumanoidRig(out HumanoidComponent humanoid)
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(translation: new(0.0f, 1.0f, 0.0f)));
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.4f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.4f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.35f, 0.0f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.25f, 0.0f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));

        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        SaveBindPoseRecursive(root);

        humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetFromNode();
        return root;
    }

    private static void SaveBindPoseRecursive(SceneNode node)
    {
        node.Transform.SaveBindState();
        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is not null)
                SaveBindPoseRecursive(child.SceneNode);
        }
    }
}
