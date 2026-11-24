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
using XREngine.Components.Movement;
using XREngine.Components.VR;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation.Procedural;

[TestFixture]
public sealed class ProceduralLocomotionAndTerrainIKTests
{
    private static readonly MethodInfo TryPreparePlaybackMethod =
        typeof(AnimStateMachineComponent).GetMethod("TryPrepareStateMachinePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo EvaluateAndApplyMethod =
        typeof(AnimStateMachineComponent).GetMethod("EvaluateAndApply", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ApplyMusclePoseMethod =
        typeof(HumanoidComponent).GetMethod("ApplyCurrentMusclePose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void ProceduralStrideMotion_DrivesWalkMicroShuffleTurnInPlaceAndStancesZeroAlloc()
    {
        SceneNode root = CreateLeggedHumanoidRig(out HumanoidComponent humanoid);
        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;

        var stride = new ProceduralStrideMotion { Name = "ProceduralStride" };
        var state = new AnimState(stride, "Stride");
        animator.StateMachine.Layers.Add(new AnimLayer(state) { InitialState = state });
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["StrafeMode"] = new AnimBool("StrafeMode", false);
        animator.StateMachine.Variables["LocomotionStyle"] = new AnimInt("LocomotionStyle", (int)ELocomotionStyle.Standing);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", true);

        bool prepared = (bool)TryPreparePlaybackMethod.Invoke(animator, null)!;
        prepared.ShouldBeTrue(animator.PlaybackCapabilityDiagnostic);

        long dt60 = Stopwatch.Frequency / 60;

        // 1. Slow micro-step shuffle (~0.15 m/s) triggers alternating steps without foot crossover.
        animator.SetFloat("MoveY", 0.12f);
        for (int i = 0; i < 120; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
            stride.RightFootWorldPosition.X.ShouldBeGreaterThan(stride.LeftFootWorldPosition.X - 0.02f);
        }

        (stride.LeftStepCount + stride.RightStepCount).ShouldBeGreaterThanOrEqualTo(2);
        stride.CurrentBodyPosition.Z.ShouldBeGreaterThan(0.25f);

        // 2. Turn in place triggers pivoting foot steps even with zero planar input.
        int stepsBeforeTurn = stride.LeftStepCount + stride.RightStepCount;
        animator.SetFloat("MoveY", 0.0f);
        animator.SetBool("StrafeMode", true);
        animator.SetFloat("DesiredFacingYaw", 1.35f);
        for (int i = 0; i < 60; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
        }
        (stride.LeftStepCount + stride.RightStepCount).ShouldBeGreaterThan(stepsBeforeTurn);

        // 3. Stance change to Crouched lowers body hip height cleanly.
        float standingY = stride.CurrentBodyPosition.Y;
        animator.SetInt("LocomotionStyle", (int)ELocomotionStyle.Crouched);
        for (int i = 0; i < 30; i++)
        {
            EvaluateAndApplyMethod.Invoke(animator, [dt60, humanoid]);
            ApplyMusclePoseMethod.Invoke(humanoid, null);
        }
        stride.CurrentBodyPosition.Y.ShouldBeLessThan(standingY - 0.15f);

        // 4. Steady-state evaluation allocates zero bytes.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++)
        {
            stride.Tick(1.0f / 60.0f);
            stride.GetAnimationValues(null, animator.StateMachine.Variables, 1.0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0L);
    }

    [Test]
    public void MotionWarpingMotion_ConvergesExactlyToRuntimeTargetAndFallsBackWhenUnbound()
    {
        var warp = new MotionWarpingMotion
        {
            Name = "VaultWarp",
            WarpWindows =
            [
                new MotionWarpWindow
                {
                    TargetName = "LedgeTop",
                    StartNormalizedTime = 0.20f,
                    EndNormalizedTime = 0.80f,
                    WarpTranslationXZ = true,
                    WarpTranslationY = true,
                    WarpYaw = true,
                    WarpWeight = 1.0f,
                }
            ],
        };

        var startPos = new Vector3(0.0f, 0.0f, 0.0f);
        warp.SetStartWorldRootPose(startPos, 0.0f);

        // Without a bound target, pose remains unmodified at startPos.
        warp.EvaluateWarpedRootPose(0.80f, out Vector3 unboundPos, out _);
        Vector3.Distance(unboundPos, startPos).ShouldBeLessThan(1.0e-5f);

        // Bind a runtime ledge target displaced +0.45 m in X, +0.95 m in Y, +0.50 m in Z, yaw +0.6 rad.
        var targetPos = new Vector3(0.45f, 0.95f, 0.50f);
        Quaternion targetRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.60f);
        warp.SetWarpTarget(new MotionWarpTarget("LedgeTop", targetPos, targetRot));

        // Pre-window (t=0.10) preserves unwarped start pose.
        warp.EvaluateWarpedRootPose(0.10f, out Vector3 prePos, out _);
        Vector3.Distance(prePos, startPos).ShouldBeLessThan(1.0e-5f);

        // Window end (t=0.80) converges exactly onto target position and rotation.
        warp.EvaluateWarpedRootPose(0.80f, out Vector3 endPos, out Quaternion endRot);
        Vector3.Distance(endPos, targetPos).ShouldBeLessThan(1.0e-4f);
        MathF.Abs(Quaternion.Dot(Quaternion.Normalize(endRot), Quaternion.Normalize(targetRot))).ShouldBeGreaterThan(0.9999f);

        // Window MemoryPack serialization round-trip.
        byte[] bytes = MemoryPackSerializer.Serialize(warp.WarpWindows);
        var restored = MemoryPackSerializer.Deserialize<List<MotionWarpWindow>>(bytes);
        restored.ShouldNotBeNull();
        restored.Count.ShouldBe(1);
        restored[0].TargetName.ShouldBe("LedgeTop");
        restored[0].EndNormalizedTime.ShouldBe(0.80f, 1.0e-5f);
    }

    [Test]
    public void PhysicsTraversalProber_RealJoltScene_DetectsStaticBoxLedgeHeightDepthAndLanding()
    {
        var joltScene = new JoltScene();
        joltScene.Initialize();
        try
        {
            // Floor at Y = 0.0.
            joltScene.CreateStaticRigidBody(
                new IPhysicsGeometry.Box(new Vector3(10.0f, 0.5f, 10.0f)),
                (new Vector3(0.0f, -0.5f, 0.0f), Quaternion.Identity),
                Vector3.Zero,
                Quaternion.Identity,
                LayerMask.Everything).ShouldNotBeNull();

            // Vaultable wall box: front face at Z = 0.60, back face at Z = 1.20 (depth 0.60m), top at Y = 1.00m.
            joltScene.CreateStaticRigidBody(
                new IPhysicsGeometry.Box(new Vector3(3.0f, 0.50f, 0.30f)),
                (new Vector3(0.0f, 0.50f, 0.90f), Quaternion.Identity),
                Vector3.Zero,
                Quaternion.Identity,
                LayerMask.Everything).ShouldNotBeNull();

            var prober = new PhysicsTraversalProber();
            TraversalLedgeProbeResult result = prober.ProbeLedge(
                joltScene,
                characterFootPosition: Vector3.Zero,
                forwardDirectionXZ: Vector3.UnitZ,
                maxReachDistance: 1.5f);

            result.Found.ShouldBeTrue();
            result.LedgeHeightMeters.ShouldBe(1.00f, 0.06f);
            result.LedgeDepthMeters.ShouldBeGreaterThan(0.25f);
            result.HasLandingClearance.ShouldBeTrue();
            result.LandingPosition.Y.ShouldBe(0.0f, 0.08f);

            MotionWarpTarget warpTarget = result.ToLedgeWarpTarget();
            warpTarget.HasTarget.ShouldBeTrue();
            warpTarget.Position.Y.ShouldBe(1.00f, 0.06f);
        }
        finally
        {
            joltScene.Destroy();
        }
    }

    [Test]
    public void HumanoidFootPlacementIK_DropsPelvisAndAlignsAnklesOnSlope()
    {
        SceneNode root = CreateLeggedHumanoidRig(out HumanoidComponent humanoid);
        var footIK = root.AddComponent<HumanoidFootPlacementIKComponent>()!;
        footIK.Humanoid = humanoid;
        footIK.FootSoleHeightOffset = 0.05f;
        footIK.EnableFootLocking = false;

        // Sloped surface: Y = 0.25 * X (left foot at X=-0.20 => ground Y=-0.05; right foot at X=+0.20 => ground Y=+0.05).
        var slopeNormal = Vector3.Normalize(new Vector3(-0.25f, 1.0f, 0.0f));
        footIK.GroundProberOverride = new SlopedSurfaceProber(slopeX: 0.25f, baseY: -0.10f, normal: slopeNormal);

        float initialHipsY = humanoid.Hips.Node!.Transform.WorldTranslation.Y;
        footIK.StepFootPlacement(0.0f);

        footIK.LastAppliedPelvisDropMeters.ShouldBeGreaterThan(0.05f);
        humanoid.Hips.Node.Transform.WorldTranslation.Y.ShouldBeLessThan(initialHipsY - 0.05f);

        Vector3 leftFootPos = humanoid.Left.Foot.Node!.Transform.WorldTranslation;
        Vector3 rightFootPos = humanoid.Right.Foot.Node!.Transform.WorldTranslation;
        float expectedLeftY = -0.10f + 0.25f * leftFootPos.X + footIK.FootSoleHeightOffset;
        float expectedRightY = -0.10f + 0.25f * rightFootPos.X + footIK.FootSoleHeightOffset;

        MathF.Abs(leftFootPos.Y - expectedLeftY).ShouldBeLessThan(0.01f);
        MathF.Abs(rightFootPos.Y - expectedRightY).ShouldBeLessThan(0.01f);

        // Ankle rotated toward slope normal.
        Vector3 footUp = Vector3.Transform(Vector3.UnitY, humanoid.Left.Foot.Node.Transform.WorldRotation);
        Vector3.Dot(Vector3.Normalize(footUp), slopeNormal).ShouldBeGreaterThan(0.95f);

        // Zero allocations in steady state.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            footIK.StepFootPlacement(1.0f / 60.0f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.ShouldBe(0L);
    }

    [Test]
    public void HumanoidFootPlacementIK_LocksStanceFootAgainstHorizontalSlide()
    {
        SceneNode root = CreateLeggedHumanoidRig(out HumanoidComponent humanoid);
        var footIK = root.AddComponent<HumanoidFootPlacementIKComponent>()!;
        footIK.Humanoid = humanoid;
        footIK.FootSoleHeightOffset = 0.05f;
        footIK.EnableFootLocking = true;
        footIK.GroundProberOverride = new SlopedSurfaceProber(slopeX: 0.0f, baseY: 0.0f, normal: Vector3.UnitY);

        // Position hips so feet rest on Y = 0.05 (sole offset).
        var hipsTfm = (Transform)humanoid.Hips.Node!.Transform;
        hipsTfm.Translation = new Vector3(0.0f, 1.0f, 0.0f);
        hipsTfm.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);

        footIK.StepFootPlacement(1.0f / 60.0f);
        footIK.LeftFootLocked.ShouldBeTrue();
        Vector3 lockedLeft = footIK.LastLeftFootTarget;

        // Drift hips slightly forward (~0.24 m/s micro-slide); locked left foot stays anchored horizontally.
        hipsTfm.Translation = new Vector3(0.0f, 1.0f, 0.004f);
        hipsTfm.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        footIK.StepFootPlacement(1.0f / 60.0f);

        footIK.LeftFootLocked.ShouldBeTrue();
        MathF.Abs(footIK.LastLeftFootTarget.Z - lockedLeft.Z).ShouldBeLessThan(0.002f);

        // Lifting the hips/foot clear off the ground releases the stance lock cleanly.
        hipsTfm.Translation = new Vector3(0.0f, 1.25f, 0.004f);
        hipsTfm.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
        footIK.StepFootPlacement(1.0f / 60.0f);
        footIK.LeftFootLocked.ShouldBeFalse();
    }

    [Test]
    public void VrLowerBodyLocomotionBridge_DerivesSteeringStanceAndDrivesProceduralStrideWithLowerBodyMask()
    {
        SceneNode root = CreateLeggedHumanoidRig(out HumanoidComponent humanoid);
        var headNode = root.NewChildWithTransform<Transform>(out Transform headTransform, "HmdHead");
        headTransform.Translation = new Vector3(0.0f, 1.65f, 0.0f);
        headTransform.RecalculateMatrices(true);

        var animator = root.AddComponent<AnimStateMachineComponent>()!;
        animator.Humanoid = humanoid;
        var stride = new ProceduralStrideMotion { Name = "VrLowerBodyStride" };
        var state = new AnimState(stride, "Stride");
        animator.StateMachine.Layers.Add(new AnimLayer(state) { InitialState = state });
        animator.StateMachine.Variables["MoveX"] = new AnimFloat("MoveX", 0.0f);
        animator.StateMachine.Variables["MoveY"] = new AnimFloat("MoveY", 0.0f);
        animator.StateMachine.Variables["DesiredFacingYaw"] = new AnimFloat("DesiredFacingYaw", 0.0f);
        animator.StateMachine.Variables["StrafeMode"] = new AnimBool("StrafeMode", true);
        animator.StateMachine.Variables["LocomotionStyle"] = new AnimInt("LocomotionStyle", 0);
        animator.StateMachine.Variables["IsGrounded"] = new AnimBool("IsGrounded", true);

        var rootBridge = root.AddComponent<CharacterRootMotionBridgeComponent>()!;
        rootBridge.Animator = animator;
        rootBridge.BindEndpoints();
        rootBridge.ConfigureLayerAvatarMask(0, EHumanoidBodyMask.LowerBody);
        animator.StateMachine.Layers[0].AvatarMask.ShouldBe(EHumanoidBodyMask.LowerBody);

        var vrBridge = root.AddComponent<VrLowerBodyLocomotionBridgeComponent>()!;
        vrBridge.HeadTransform = headTransform;
        vrBridge.CalibratedStandingHeadHeight = 1.65f;
        vrBridge.SteeringUpdated = new XREvent<SteeringSnapshot>
        {
            PersistentCalls =
            [
                new XRPersistentCall
                {
                    NodeId = root.ID,
                    TargetObjectId = rootBridge.ID,
                    MethodName = nameof(CharacterRootMotionBridgeComponent.ApplySteeringSnapshot),
                    ParameterTypeNames = [typeof(SteeringSnapshot).AssemblyQualifiedName!],
                }
            ],
        };

        // Initial standing frame.
        SteeringSnapshot stand = vrBridge.StepVrSteering(1.0f / 60.0f);
        stand.LocomotionStyle.ShouldBe(ELocomotionStyle.Standing);

        // Move head forward + crouch down -> steering emits positive Z movement and Crouched style.
        headTransform.Translation = new Vector3(0.0f, 1.10f, 0.05f);
        headTransform.RecalculateMatrices(true);
        SteeringSnapshot crouchMove = vrBridge.StepVrSteering(1.0f / 60.0f);
        crouchMove.LocomotionStyle.ShouldBe(ELocomotionStyle.Crouched);
        crouchMove.DesiredWorldMoveXZ.Y.ShouldBeGreaterThan(0.10f);
        animator.StateMachine.Variables["LocomotionStyle"].IntValue.ShouldBe((int)ELocomotionStyle.Crouched);
        animator.StateMachine.Variables["MoveY"].FloatValue.ShouldBeGreaterThan(0.10f);

        // Lower head to floor -> Prone style.
        headTransform.Translation = new Vector3(0.0f, 0.50f, 0.05f);
        headTransform.RecalculateMatrices(true);
        SteeringSnapshot prone = vrBridge.StepVrSteering(1.0f / 60.0f);
        prone.LocomotionStyle.ShouldBe(ELocomotionStyle.Prone);
    }

    private sealed class SlopedSurfaceProber(float slopeX, float baseY, Vector3 normal) : IFootGroundProber
    {
        public bool TryProbeGround(
            Vector3 footWorldPosition,
            float probeUpMeters,
            float probeDownMeters,
            out Vector3 groundPoint,
            out Vector3 groundNormal)
        {
            float y = baseY + slopeX * footWorldPosition.X;
            groundPoint = new Vector3(footWorldPosition.X, y, footWorldPosition.Z);
            groundNormal = normal;
            return true;
        }
    }

    private static SceneNode CreateLeggedHumanoidRig(out HumanoidComponent humanoid)
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(translation: new(0.0f, 1.0f, 0.0f)));
        var spine = new SceneNode(hips, "Spine", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        var chest = new SceneNode(spine, "Chest", new Transform(translation: new(0.0f, 0.3f, 0.0f)));
        _ = new SceneNode(chest, "Head", new Transform(translation: new(0.0f, 0.25f, 0.0f)));

        var leftLeg = new SceneNode(hips, "LeftLeg", new Transform(translation: new(-0.2f, -0.1f, 0.0f)));
        var leftKnee = new SceneNode(leftLeg, "LeftKnee", new Transform(translation: new(0.0f, -0.45f, 0.04f)));
        _ = new SceneNode(leftKnee, "LeftFoot", new Transform(translation: new(0.0f, -0.40f, -0.04f)));
        var rightLeg = new SceneNode(hips, "RightLeg", new Transform(translation: new(0.2f, -0.1f, 0.0f)));
        var rightKnee = new SceneNode(rightLeg, "RightKnee", new Transform(translation: new(0.0f, -0.45f, 0.04f)));
        _ = new SceneNode(rightKnee, "RightFoot", new Transform(translation: new(0.0f, -0.40f, -0.04f)));

        var leftShoulder = new SceneNode(chest, "LeftShoulder", new Transform(translation: new(-0.25f, 0.1f, 0.0f)));
        var leftArm = new SceneNode(leftShoulder, "LeftArm", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        var leftElbow = new SceneNode(leftArm, "LeftElbow", new Transform(translation: new(-0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(leftElbow, "LeftHand", new Transform(translation: new(-0.2f, 0.0f, 0.0f)));

        var rightShoulder = new SceneNode(chest, "RightShoulder", new Transform(translation: new(0.25f, 0.1f, 0.0f)));
        var rightArm = new SceneNode(rightShoulder, "RightArm", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        var rightElbow = new SceneNode(rightArm, "RightElbow", new Transform(translation: new(0.3f, 0.0f, 0.0f)));
        _ = new SceneNode(rightElbow, "RightHand", new Transform(translation: new(0.2f, 0.0f, 0.0f)));

        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
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
