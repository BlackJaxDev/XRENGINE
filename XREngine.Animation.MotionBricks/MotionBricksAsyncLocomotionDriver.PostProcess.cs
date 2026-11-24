using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation.MotionBricks;

public sealed partial class MotionBricksAsyncLocomotionDriver
{
    /// <summary>
    /// Guards decoded standing SOMA trajectories against pelvis dives, forward torso stumbles,
    /// and arms flinging into the air during fast turns or run-to-walk deceleration. Requests
    /// upright target-pose conditioning on the next replan whenever severe instability appears.
    /// </summary>
    private void SanitizeStandingDecodedOutput(
        SomaInferenceOutputBuffer output,
        int validFrames,
        float fallbackYawRadians)
    {
        const int chestJointIndex = 3;
        const int neckJointIndex = 4;
        const int headJointIndex = 6;
        const int leftShoulderJointIndex = 10;
        const int leftForeArmJointIndex = 12;
        const int leftHandJointIndex = 13;
        const int rightShoulderJointIndex = 16;
        const int rightForeArmJointIndex = 18;
        const int rightHandJointIndex = 19;

        const float maxHipsTiltRadians = 0.26f;
        const float triggerHipsCosTilt = 0.955f;
        const float severeTumbleHipsCosTilt = 0.76f;
        const float triggerTorsoUpY = 0.88f;
        const float severeTorsoUpY = 0.74f;
        const float armHeightMarginMeters = 0.10f;

        float minHipsY = MathF.Max(0.45f, _calibration.HipsHeightMeters - 0.18f);
        float maxHipsY = _calibration.HipsHeightMeters + 0.12f;
        float severeDropHipsY = MathF.Max(0.35f, _calibration.HipsHeightMeters - 0.28f);
        SomaPoseTransform[] decodedPoses = output.DecodedLocalPoses;
        float cleanYaw = fallbackYawRadians;
        int instabilityFrameCount = 0;

        for (int frame = 0; frame < validFrames; frame++)
        {
            int frameBase = frame * JointCount;
            SomaPoseTransform root = decodedPoses[frameBase];
            float rawHipsY = root.Position.Y;
            Quaternion canonicalBodyRot = SomaPoseTransform.NormalizeOrIdentity(
                root.Rotation * _calibration.InverseRestWorldRotations[0]);
            Vector3 up = Vector3.Transform(Vector3.UnitY, canonicalBodyRot);

            if (up.Y >= 0.45f)
                cleanYaw = ExtractFacingYaw(root.Rotation, cleanYaw);

            bool excessiveHipsTilt = up.Y < triggerHipsCosTilt;
            bool outOfHeightBounds = rawHipsY < minHipsY || rawHipsY > maxHipsY;
            if (excessiveHipsTilt || outOfHeightBounds)
            {
                Quaternion clampedCanonicalRot = canonicalBodyRot;
                if (excessiveHipsTilt)
                {
                    Quaternion uprightBodyRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, cleanYaw);
                    float tiltAngle = MathF.Acos(Math.Clamp(up.Y, -1.0f, 1.0f));
                    float baseKeep = tiltAngle > 1.0e-4f
                        ? Math.Clamp(maxHipsTiltRadians / tiltAngle, 0.0f, 1.0f)
                        : 0.0f;
                    // Ramp smoothly toward upright during severe tumbles instead of snapping to zero
                    // at a hard threshold, preventing sudden 15-degree hips pitch/roll pops.
                    float severeScale = up.Y < severeTumbleHipsCosTilt
                        ? Math.Clamp((up.Y - 0.58f) / (severeTumbleHipsCosTilt - 0.58f), 0.0f, 1.0f)
                        : 1.0f;
                    float keepFraction = baseKeep * severeScale;
                    clampedCanonicalRot = SomaPoseTransform.NormalizeOrIdentity(
                        Quaternion.Slerp(uprightBodyRot, canonicalBodyRot, keepFraction));
                }

                Quaternion clampedRootRot = SomaPoseTransform.NormalizeOrIdentity(
                    clampedCanonicalRot * _calibration.RestWorldRotations[0]);
                float clampedY = Math.Clamp(rawHipsY, minHipsY, maxHipsY);
                decodedPoses[frameBase] = new SomaPoseTransform(
                    new Vector3(root.Position.X, clampedY, root.Position.Z),
                    clampedRootRot);
            }

            // Evaluate full-body geometry once into preallocated scratch storage to catch forward
            // torso stumbles and hands flying above chest height during sharp turns.
            ReadOnlySpan<SomaPoseTransform> frameLocalPoses = decodedPoses.AsSpan(frameBase, JointCount);
            SomaTensorCodec.ComputeWorldPose(frameLocalPoses, SomaPoseTransform.Identity, _workerWorldPoseScratch);

            Vector3 torsoVec = _workerWorldPoseScratch[chestJointIndex].Position - _workerWorldPoseScratch[0].Position;
            float torsoLenSq = torsoVec.LengthSquared();
            float torsoUpY = torsoLenSq > 1.0e-6f ? torsoVec.Y / MathF.Sqrt(torsoLenSq) : 1.0f;
            if (torsoUpY < triggerTorsoUpY)
            {
                float torsoRecovery = Math.Clamp((triggerTorsoUpY - torsoUpY) / 0.26f, 0.0f, 0.85f);
                for (int joint = 1; joint <= headJointIndex; joint++)
                {
                    decodedPoses[frameBase + joint] = SomaPoseTransform.Lerp(
                        decodedPoses[frameBase + joint],
                        _workerIdleFallbackPose[joint],
                        torsoRecovery);
                }
            }

            // Guard against stumble arms flinging above head/neck height without clipping normal
            // chest-level jogging and running arm pumps.
            float armHeightCeilingY = _workerWorldPoseScratch[neckJointIndex].Position.Y + armHeightMarginMeters;
            float leftArmPeakY = MathF.Max(
                _workerWorldPoseScratch[leftHandJointIndex].Position.Y,
                _workerWorldPoseScratch[leftForeArmJointIndex].Position.Y);
            float leftExcess = leftArmPeakY - armHeightCeilingY;
            if (leftExcess > 0.0f)
            {
                float armBlend = Math.Clamp(leftExcess / 0.18f, 0.0f, 0.85f);
                for (int joint = leftShoulderJointIndex; joint <= leftHandJointIndex; joint++)
                {
                    decodedPoses[frameBase + joint] = SomaPoseTransform.Lerp(
                        decodedPoses[frameBase + joint],
                        _workerIdleFallbackPose[joint],
                        armBlend);
                }
            }

            float rightArmPeakY = MathF.Max(
                _workerWorldPoseScratch[rightHandJointIndex].Position.Y,
                _workerWorldPoseScratch[rightForeArmJointIndex].Position.Y);
            float rightExcess = rightArmPeakY - armHeightCeilingY;
            if (rightExcess > 0.0f)
            {
                float armBlend = Math.Clamp(rightExcess / 0.18f, 0.0f, 0.85f);
                for (int joint = rightShoulderJointIndex; joint <= rightHandJointIndex; joint++)
                {
                    decodedPoses[frameBase + joint] = SomaPoseTransform.Lerp(
                        decodedPoses[frameBase + joint],
                        _workerIdleFallbackPose[joint],
                        armBlend);
                }
            }

            // If SOMA pitched into a stumble or fall, pull all local joints partway back toward
            // upright so the 30 Hz context buffer stays clean and never locks into perpetual falling.
            bool severeInstability = up.Y < severeTumbleHipsCosTilt
                || torsoUpY < severeTorsoUpY
                || rawHipsY < severeDropHipsY;
            if (severeInstability)
            {
                instabilityFrameCount++;
                for (int joint = 1; joint < JointCount; joint++)
                {
                    decodedPoses[frameBase + joint] = SomaPoseTransform.Lerp(
                        decodedPoses[frameBase + joint],
                        _workerIdleFallbackPose[joint],
                        0.55f);
                }
            }
        }

        if (instabilityFrameCount >= 2)
            Volatile.Write(ref _recoveryConditioningRemainingReplans, 2);
    }

    /// <summary>
    /// Applies a zero-phase 3-tap binomial filter across decoded 30 Hz frames on upper-body local
    /// joint rotations (<c>Spine1..RightHand</c>, joints 1..21) to remove neural diffusion tremor
    /// while leaving root and leg joints untouched for crisp foot locking.
    /// </summary>
    private void SmoothDecodedUpperBodyRotations(SomaInferenceOutputBuffer output, int validFrames)
    {
        const int upperBodyFirstJoint = 1;
        const int upperBodyJointCount = 21; // Joints 1..21 (Spine1 through RightHandMiddleEnd).

        if (validFrames < 3)
            return;

        SomaPoseTransform[] decodedPoses = output.DecodedLocalPoses;
        for (int frame = 1; frame < validFrames - 1; frame++)
        {
            int prevBase = (frame - 1) * JointCount;
            int currBase = frame * JointCount;
            int nextBase = (frame + 1) * JointCount;
            int dstBase = frame * upperBodyJointCount;

            for (int offset = 0; offset < upperBodyJointCount; offset++)
            {
                int joint = upperBodyFirstJoint + offset;
                Quaternion qPrev = decodedPoses[prevBase + joint].Rotation;
                Quaternion qCurr = decodedPoses[currBase + joint].Rotation;
                Quaternion qNext = decodedPoses[nextBase + joint].Rotation;
                Quaternion neighborMid = SomaPoseTransform.NormalizeOrIdentity(Quaternion.Slerp(qPrev, qNext, 0.5f));
                _workerSmoothRotScratch[dstBase + offset] = SomaPoseTransform.NormalizeOrIdentity(
                    Quaternion.Slerp(qCurr, neighborMid, 0.40f));
            }
        }

        for (int frame = 1; frame < validFrames - 1; frame++)
        {
            int currBase = frame * JointCount;
            int srcBase = frame * upperBodyJointCount;
            for (int offset = 0; offset < upperBodyJointCount; offset++)
            {
                int joint = upperBodyFirstJoint + offset;
                decodedPoses[currBase + joint] = new SomaPoseTransform(
                    decodedPoses[currBase + joint].Position,
                    _workerSmoothRotScratch[srcBase + offset]);
            }
        }
    }

    /// <summary>
    /// Smooths converted humanoid muscle channels across render frames to remove residual 30 Hz
    /// interpolation corners and upper-body micro-jitter without introducing foot slide.
    /// </summary>
    private void ApplyMuscleTemporalSmoothing(Span<float> muscles, float dt)
    {
        if (!_hasSmoothedMuscles || dt <= 0.0f)
        {
            muscles.CopyTo(_smoothedMuscles);
            _hasSmoothedMuscles = true;
            return;
        }

        float legAlpha = 1.0f - MathF.Exp(-55.0f * dt);
        float upperBodyAlpha = 1.0f - MathF.Exp(-22.0f * dt);
        float headNeckAlpha = 1.0f - MathF.Exp(-13.0f * dt);
        float wristAlpha = 1.0f - MathF.Exp(-13.0f * dt);

        int count = Math.Min(muscles.Length, _smoothedMuscles.Length);
        for (int i = 0; i < count; i++)
        {
            float alpha = i switch
            {
                >= (int)EHumanoidValue.NeckNodDownUp and <= (int)EHumanoidValue.HeadTurnLeftRight => headNeckAlpha,
                >= (int)EHumanoidValue.LeftUpperLegFrontBack and <= (int)EHumanoidValue.RightToesUpDown => legAlpha,
                (int)EHumanoidValue.LeftForearmTwistInOut
                or (int)EHumanoidValue.LeftHandDownUp
                or (int)EHumanoidValue.LeftHandInOut
                or (int)EHumanoidValue.RightForearmTwistInOut
                or (int)EHumanoidValue.RightHandDownUp
                or (int)EHumanoidValue.RightHandInOut => wristAlpha,
                _ => upperBodyAlpha,
            };

            float next = _smoothedMuscles[i] + (muscles[i] - _smoothedMuscles[i]) * alpha;
            _smoothedMuscles[i] = next;
            muscles[i] = next;
        }
    }

    /// <summary>
    /// Applies a zero-allocation standing posture, head-level, and feminine gait stylization pass:
    /// compensates SOMA's chin-up neck bias so the avatar looks forward and slightly downward along
    /// her path, dampens jerky upper-body twist and shrug noise, leads turns gently with the head,
    /// and applies feminine walking carriage that fades smoothly back during running.
    /// </summary>
    private void ApplyFeminineStylePostProcess(
        Span<float> muscles,
        in MotionBricksSteeringInput input,
        float blendScale)
    {
        float activeScale = Math.Clamp(blendScale, 0.0f, 1.0f);
        if (activeScale <= 1.0e-4f)
            return;

        float walkSpeed = MathF.Max(0.4f, input.WalkSpeedMetersPerSecond);
        float moveBlend = Math.Clamp(_planner.DesiredSpeed / walkSpeed, 0.0f, 1.0f) * activeScale;
        float runSpan = MathF.Max(0.5f, input.RunSpeedMetersPerSecond - walkSpeed);
        float runFraction = Math.Clamp((_planner.DesiredSpeed - walkSpeed) / runSpan, 0.0f, 1.0f);
        float mocapScale = _poseBank.HasMocapTargets ? 0.65f : 1.0f;
        float walkFocus = moveBlend * (1.0f - 0.75f * runFraction) * mocapScale;
        float upperBodyStyle = moveBlend * (1.0f - 0.70f * runFraction) * mocapScale;

        if (moveBlend > 1.0e-4f)
        {
            // Dampen excessive SOMA thoracic twist and shoulder shrug so the upper body glides naturally.
            float dampWeight = 1.0f - (0.24f * mocapScale) * moveBlend;
            muscles[(int)EHumanoidValue.ChestTwistLeftRight] *= dampWeight;
            muscles[(int)EHumanoidValue.UpperChestTwistLeftRight] *= dampWeight;
            muscles[(int)EHumanoidValue.LeftShoulderFrontBack] *= 1.0f - (0.30f * mocapScale) * moveBlend;
            muscles[(int)EHumanoidValue.RightShoulderFrontBack] *= 1.0f - (0.30f * mocapScale) * moveBlend;

            // Lock pelvic sway to SOMA's live alternating thigh swing so hip sway never drifts from footsteps.
            float stridePhase = Math.Clamp(
                muscles[(int)EHumanoidValue.RightUpperLegFrontBack] - muscles[(int)EHumanoidValue.LeftUpperLegFrontBack],
                -1.0f,
                1.0f);

            AddClampedMuscle(muscles, EHumanoidValue.LeftUpperLegInOut, -0.08f * walkFocus);
            AddClampedMuscle(muscles, EHumanoidValue.RightUpperLegInOut, -0.08f * walkFocus);

            AddClampedMuscle(muscles, EHumanoidValue.SpineLeftRight, stridePhase * 0.045f * walkFocus);
            AddClampedMuscle(muscles, EHumanoidValue.ChestLeftRight, -stridePhase * 0.028f * walkFocus);
            AddClampedMuscle(muscles, EHumanoidValue.UpperChestLeftRight, -stridePhase * 0.017f * walkFocus);
            AddClampedMuscle(muscles, EHumanoidValue.SpineTwistLeftRight, stridePhase * 0.045f * walkFocus);

            AddClampedMuscle(muscles, EHumanoidValue.SpineFrontBack, 0.025f * upperBodyStyle);
            AddClampedMuscle(muscles, EHumanoidValue.ChestFrontBack, 0.025f * upperBodyStyle);

            AddClampedMuscle(muscles, EHumanoidValue.LeftShoulderDownUp, -0.06f * upperBodyStyle);
            AddClampedMuscle(muscles, EHumanoidValue.RightShoulderDownUp, -0.06f * upperBodyStyle);
            AddClampedMuscle(muscles, EHumanoidValue.LeftArmDownUp, -0.085f * upperBodyStyle);
            AddClampedMuscle(muscles, EHumanoidValue.RightArmDownUp, -0.085f * upperBodyStyle);
            AddClampedMuscle(muscles, EHumanoidValue.LeftForearmStretch, -0.15f * walkFocus);
            AddClampedMuscle(muscles, EHumanoidValue.RightForearmStretch, -0.15f * walkFocus);

            // Attenuate noisy end-effector wrist spikes from SOMA diffusion while keeping hands
            // neutral and relaxed instead of drooping or kinking sideways during locomotion.
            DampAndClampMuscle(muscles, EHumanoidValue.LeftHandDownUp, 0.72f, -0.28f, 0.22f, moveBlend);
            DampAndClampMuscle(muscles, EHumanoidValue.RightHandDownUp, 0.72f, -0.28f, 0.22f, moveBlend);
            DampAndClampMuscle(muscles, EHumanoidValue.LeftHandInOut, 0.55f, -0.22f, 0.22f, moveBlend);
            DampAndClampMuscle(muscles, EHumanoidValue.RightHandInOut, 0.55f, -0.22f, 0.22f, moveBlend);
        }

        // Compensate SOMA's upward neck and head extension and any back arch so the avatar looks
        // level and slightly downward along her travel path instead of tilting her chin upward.
        float torsoBackArch = MathF.Max(
            0.0f,
            muscles[(int)EHumanoidValue.SpineFrontBack]
            + muscles[(int)EHumanoidValue.ChestFrontBack]
            + muscles[(int)EHumanoidValue.UpperChestFrontBack]);

        int neckNodIdx = (int)EHumanoidValue.NeckNodDownUp;
        int headNodIdx = (int)EHumanoidValue.HeadNodDownUp;
        float neckOffset = _poseBank.HasMocapTargets ? 0.07f : 0.13f;
        float headOffset = _poseBank.HasMocapTargets ? 0.09f : 0.17f;
        float nodKeep = _poseBank.HasMocapTargets ? 0.78f : 0.60f;
        float targetNeckNod = Math.Clamp(muscles[neckNodIdx] * nodKeep - neckOffset - torsoBackArch * 0.22f, -0.75f, 0.14f);
        float targetHeadNod = Math.Clamp(muscles[headNodIdx] * nodKeep - headOffset - torsoBackArch * 0.26f, -0.75f, 0.12f);
        muscles[neckNodIdx] = muscles[neckNodIdx] + (targetNeckNod - muscles[neckNodIdx]) * activeScale;
        muscles[headNodIdx] = muscles[headNodIdx] + (targetHeadNod - muscles[headNodIdx]) * activeScale;

        // Lead locomotion turns gently with neck and head yaw toward the upcoming travel heading.
        if (moveBlend > 0.05f)
        {
            float turnYawDelta = CriticalSpringDamper.WrapAngleRadians(
                _planner.DesiredVelocityYawRadians - _planner.CurrentFacingYawRadians);
            float headLeadMuscle = Math.Clamp(-turnYawDelta * 0.38f, -0.28f, 0.28f) * moveBlend;
            AddClampedMuscle(muscles, EHumanoidValue.NeckTurnLeftRight, headLeadMuscle * 0.45f);
            AddClampedMuscle(muscles, EHumanoidValue.HeadTurnLeftRight, headLeadMuscle * 0.55f);
        }
    }

    private static void AddClampedMuscle(Span<float> muscles, EHumanoidValue channel, float delta)
    {
        int index = (int)channel;
        muscles[index] = Math.Clamp(muscles[index] + delta, -1.0f, 1.0f);
    }

    private static void DampAndClampMuscle(
        Span<float> muscles,
        EHumanoidValue channel,
        float scale,
        float min,
        float max,
        float blendWeight)
    {
        int index = (int)channel;
        float current = muscles[index];
        float target = Math.Clamp(current * scale, min, max);
        muscles[index] = current + (target - current) * blendWeight;
    }
}
