using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Allocation-free encoder, decoder, and endpoint corrector for the NVIDIA MotionBricks
/// SOMA 30-joint tensor contract. Mirrors <c>motionbricks/model/soma/kernels.py</c>.
/// </summary>
public static class SomaTensorCodec
{
    private const int OutputPositionsOffset = SomaModelContract.GlobalRootFeatureCount;
    private const int OutputRotationsOffset = OutputPositionsOffset + SomaModelContract.LocalPosePositionFeatureCount;
    private const int OutputFootContactsOffset =
        SomaModelContract.GlobalMotionFeatureCount - SomaModelContract.FootContactCount;

    /// <summary>
    /// Converts a unit quaternion to the first two rotated basis vectors (<c>X</c> and <c>Y</c>)
    /// used by the SOMA 6D rotation representation.
    /// </summary>
    public static void RotationTo6D(Quaternion rotation, out Vector3 xAxis, out Vector3 yAxis)
    {
        Quaternion normalized = SomaPoseTransform.NormalizeOrIdentity(rotation);
        xAxis = Vector3.Transform(Vector3.UnitX, normalized);
        yAxis = Vector3.Transform(Vector3.UnitY, normalized);
    }

    /// <summary>
    /// Reconstructs an orthonormal unit quaternion from raw SOMA 6D rotation axes
    /// (<c>xRaw</c> and <c>yRaw</c>) via Gram-Schmidt orthogonalization.
    /// </summary>
    public static Quaternion RotationFrom6D(Vector3 xRaw, Vector3 yRaw)
    {
        float xLengthSq = xRaw.LengthSquared();
        if (!float.IsFinite(xLengthSq) || xLengthSq <= 1.0e-12f)
            return Quaternion.Identity;

        Vector3 xAxis = xRaw / MathF.Sqrt(xLengthSq);
        Vector3 zRaw = Vector3.Cross(xAxis, yRaw);
        float zLengthSq = zRaw.LengthSquared();
        if (!float.IsFinite(zLengthSq) || zLengthSq <= 1.0e-12f)
            return Quaternion.Identity;

        Vector3 zAxis = zRaw / MathF.Sqrt(zLengthSq);
        Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

        // System.Numerics uses row-vector multiplication (v * M), so rows 1..3 hold the
        // images of UnitX, UnitY, and UnitZ.
        var rotationMatrix = new Matrix4x4(
            xAxis.X, xAxis.Y, xAxis.Z, 0.0f,
            yAxis.X, yAxis.Y, yAxis.Z, 0.0f,
            zAxis.X, zAxis.Y, zAxis.Z, 0.0f,
            0.0f,    0.0f,    0.0f,    1.0f);

        return SomaPoseTransform.NormalizeOrIdentity(Quaternion.CreateFromRotationMatrix(rotationMatrix));
    }

    /// <summary>
    /// Computes the planar heading angle in radians from the right and left hip-joint positions,
    /// matching SOMA's <c>hips_pos</c> heading formula.
    /// </summary>
    public static float ComputeHeadingFromHipPositions(Vector3 rightLegPosition, Vector3 leftLegPosition)
    {
        var across = new Vector3(rightLegPosition.X - leftLegPosition.X, 0.0f, rightLegPosition.Z - leftLegPosition.Z);
        float lengthSquared = across.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
            return 0.0f;

        across /= MathF.Sqrt(lengthSquared);
        Vector3 forward = Vector3.Cross(Vector3.UnitY, across);
        return MathF.Atan2(forward.X, forward.Z);
    }

    /// <summary>
    /// Composes one 30-joint local pose into world transforms using the fixed SOMA hierarchy.
    /// </summary>
    public static void ComputeWorldPose(
        ReadOnlySpan<SomaPoseTransform> localPose,
        in SomaPoseTransform rootTransform,
        Span<SomaPoseTransform> worldPose)
    {
        if (localPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Local pose must contain at least {SomaModelContract.JointCount} joints.", nameof(localPose));
        if (worldPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"World pose destination must contain at least {SomaModelContract.JointCount} joints.", nameof(worldPose));

        worldPose[0] = SomaPoseTransform.Multiply(rootTransform, localPose[0]);
        int[] parents = SomaModelContract.ParentIndices;
        for (int joint = 1; joint < SomaModelContract.JointCount; joint++)
            worldPose[joint] = SomaPoseTransform.Multiply(worldPose[parents[joint]], localPose[joint]);
    }

    /// <summary>
    /// Computes the ground-plane alignment transform that moves the last context pose's hips XZ
    /// to the origin and rotates its heading to match <paramref name="desiredHeading"/>.
    /// </summary>
    public static SomaPoseTransform ComputeGuideAlignment(
        ReadOnlySpan<SomaPoseTransform> lastContextWorldPose,
        Vector2 desiredHeading)
    {
        if (lastContextWorldPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"World pose must contain at least {SomaModelContract.JointCount} joints.", nameof(lastContextWorldPose));

        Vector3 hipsPosition = lastContextWorldPose[SomaModelContract.RootJointIndex].Position;
        float currentAngle = ComputeHeadingFromHipPositions(
            lastContextWorldPose[SomaModelContract.RightLegJointIndex].Position,
            lastContextWorldPose[SomaModelContract.LeftLegJointIndex].Position);
        float desiredAngle = MathF.Atan2(desiredHeading.X, desiredHeading.Y);

        return new SomaPoseTransform(
            new Vector3(hipsPosition.X, 0.0f, hipsPosition.Z),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, currentAngle - desiredAngle));
    }

    /// <summary>
    /// Encodes 4 context poses and 1 to 4 target poses (in local joint space) into all seven
    /// SOMA ONNX input tensors without heap allocations.
    /// </summary>
    public static void EncodeInputs(
        ReadOnlySpan<SomaPoseTransform> contextLocalPoses,
        ReadOnlySpan<SomaPoseTransform> targetLocalPoses,
        int targetPoseCount,
        in SomaInferenceConfig config,
        in SomaPoseTransform runtimeToModel,
        SomaInferenceInputBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (contextLocalPoses.Length < SomaModelContract.ContextPoseCount * SomaModelContract.JointCount)
            throw new ArgumentException("Context poses must supply 4 poses x 30 joints.", nameof(contextLocalPoses));
        if (targetPoseCount < 1 || targetPoseCount > SomaModelContract.TargetPoseCapacity)
            throw new ArgumentOutOfRangeException(nameof(targetPoseCount), targetPoseCount, "Target pose count must be 1 to 4.");
        if (targetLocalPoses.Length < targetPoseCount * SomaModelContract.JointCount)
            throw new ArgumentException("Target poses span is smaller than targetPoseCount x 30 joints.", nameof(targetLocalPoses));

        SomaPoseTransform[] worldPoses = destination.GuideWorldPoses;
        const int jointCount = SomaModelContract.JointCount;

        // Compose context frames 0..3 into model-space world transforms.
        for (int frame = 0; frame < SomaModelContract.ContextPoseCount; frame++)
        {
            ReadOnlySpan<SomaPoseTransform> localFrame = contextLocalPoses.Slice(frame * jointCount, jointCount);
            Span<SomaPoseTransform> worldFrame = worldPoses.AsSpan(frame * jointCount, jointCount);
            ComputeWorldPose(localFrame, runtimeToModel, worldFrame);
        }

        // Compose target frames 0..3, padding unused slots with the last supplied target pose.
        for (int targetSlot = 0; targetSlot < SomaModelContract.TargetPoseCapacity; targetSlot++)
        {
            int sourceTargetIndex = Math.Min(targetSlot, targetPoseCount - 1);
            int frame = SomaModelContract.ContextPoseCount + targetSlot;
            ReadOnlySpan<SomaPoseTransform> localFrame = targetLocalPoses.Slice(sourceTargetIndex * jointCount, jointCount);
            Span<SomaPoseTransform> worldFrame = worldPoses.AsSpan(frame * jointCount, jointCount);
            ComputeWorldPose(localFrame, runtimeToModel, worldFrame);
        }

        // Align the last context frame to the origin XZ and configured heading.
        ReadOnlySpan<SomaPoseTransform> lastContextWorld = worldPoses.AsSpan(
            (SomaModelContract.ContextPoseCount - 1) * jointCount,
            jointCount);
        SomaPoseTransform alignment = ComputeGuideAlignment(lastContextWorld, config.HeadingAlignment);
        destination.GuideAlignment = alignment;

        Quaternion invAlignRot = Quaternion.Inverse(SomaPoseTransform.NormalizeOrIdentity(alignment.Rotation));
        float[] globalRoot = destination.GlobalRootValues;
        float[] localRoot = destination.LocalRootValues;
        float[] localPoses = destination.LocalPoses;

        const int finalFrame = SomaModelContract.ContextAndTargetPoseCount - 1;
        for (int frame = 0; frame < SomaModelContract.ContextAndTargetPoseCount; frame++)
        {
            int nextFrame = frame == SomaModelContract.ContextPoseCount - 1
                ? frame
                : Math.Min(frame + 1, finalFrame);

            int frameOffset = frame * jointCount;
            int nextFrameOffset = nextFrame * jointCount;

            Vector3 currentHips = AlignPosition(alignment.Position, invAlignRot, worldPoses[frameOffset + SomaModelContract.RootJointIndex].Position);
            Vector3 nextHips = AlignPosition(alignment.Position, invAlignRot, worldPoses[nextFrameOffset + SomaModelContract.RootJointIndex].Position);

            Vector3 currentRight = AlignPosition(alignment.Position, invAlignRot, worldPoses[frameOffset + SomaModelContract.RightLegJointIndex].Position);
            Vector3 currentLeft = AlignPosition(alignment.Position, invAlignRot, worldPoses[frameOffset + SomaModelContract.LeftLegJointIndex].Position);
            Vector3 nextRight = AlignPosition(alignment.Position, invAlignRot, worldPoses[nextFrameOffset + SomaModelContract.RightLegJointIndex].Position);
            Vector3 nextLeft = AlignPosition(alignment.Position, invAlignRot, worldPoses[nextFrameOffset + SomaModelContract.LeftLegJointIndex].Position);

            float currentHeading = ComputeHeadingFromHipPositions(currentRight, currentLeft);
            float nextHeading = ComputeHeadingFromHipPositions(nextRight, nextLeft);
            float headingDelta = MathF.Atan2(
                MathF.Sin(nextHeading - currentHeading),
                MathF.Cos(nextHeading - currentHeading));

            int gBase = frame * SomaModelContract.GlobalRootFeatureCount;
            globalRoot[gBase + 0] = currentHips.X;
            globalRoot[gBase + 1] = currentHips.Y;
            globalRoot[gBase + 2] = currentHips.Z;
            globalRoot[gBase + 3] = MathF.Cos(currentHeading);
            globalRoot[gBase + 4] = MathF.Sin(currentHeading);

            int lBase = frame * SomaModelContract.LocalRootFeatureCount;
            localRoot[lBase + 0] = headingDelta * SomaModelContract.SampleRateHz;
            localRoot[lBase + 1] = (nextHips.X - currentHips.X) * SomaModelContract.SampleRateHz;
            localRoot[lBase + 2] = (nextHips.Z - currentHips.Z) * SomaModelContract.SampleRateHz;
            localRoot[lBase + 3] = currentHips.Y;

            var groundRootPosition = new Vector3(currentHips.X, 0.0f, currentHips.Z);
            int pBase = frame * SomaModelContract.LocalPoseFeatureCount;
            for (int joint = 0; joint < jointCount; joint++)
            {
                SomaPoseTransform worldJoint = worldPoses[frameOffset + joint];
                Vector3 alignedPos = AlignPosition(alignment.Position, invAlignRot, worldJoint.Position);
                Quaternion alignedRot = SomaPoseTransform.NormalizeOrIdentity(invAlignRot * worldJoint.Rotation);

                if (joint > 0)
                {
                    int posOffset = pBase + (joint - 1) * 3;
                    Vector3 centered = alignedPos - groundRootPosition;
                    localPoses[posOffset + 0] = centered.X;
                    localPoses[posOffset + 1] = centered.Y;
                    localPoses[posOffset + 2] = centered.Z;
                }

                RotationTo6D(alignedRot, out Vector3 xAxis, out Vector3 yAxis);
                int rotOffset = pBase + SomaModelContract.LocalPosePositionFeatureCount + joint * 6;
                localPoses[rotOffset + 0] = xAxis.X;
                localPoses[rotOffset + 1] = xAxis.Y;
                localPoses[rotOffset + 2] = xAxis.Z;
                localPoses[rotOffset + 3] = yAxis.X;
                localPoses[rotOffset + 4] = yAxis.Y;
                localPoses[rotOffset + 5] = yAxis.Z;
            }
        }

        WriteConditionMasks(targetPoseCount, config.AllowedTokenBits, destination, config.ConditionTargetLocalPoses);
    }

    /// <summary>
    /// Writes the four conditioning mask tensors that specify which context/target slots
    /// are active and which token counts the model may select.
    /// </summary>
    public static void WriteConditionMasks(
        int targetPoseCount,
        int allowedTokenBits,
        SomaInferenceInputBuffer destination,
        bool conditionTargetLocalPoses = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (targetPoseCount < 1 || targetPoseCount > SomaModelContract.TargetPoseCapacity)
            throw new ArgumentOutOfRangeException(nameof(targetPoseCount));

        int activePoseCount = SomaModelContract.ContextPoseCount + targetPoseCount;
        int lastContextIndex = SomaModelContract.ContextPoseCount - 1;
        int lastTargetIndex = activePoseCount - 1;

        for (int i = 0; i < SomaModelContract.ContextAndTargetPoseCount; i++)
        {
            int active = i < activePoseCount ? 1 : 0;
            bool isContext = i < SomaModelContract.ContextPoseCount;
            destination.HasGlobalRootValues[i] = active;
            destination.HasLocalPoses[i] = isContext ? active : (conditionTargetLocalPoses ? active : 0);
            destination.HasLocalRootValues[i] = (i == lastContextIndex || i == lastTargetIndex) ? 0 : active;
        }

        for (int i = 0; i < SomaModelContract.AllowedTokenSlotCount; i++)
            destination.AllowedPredNumTokens[i] = (allowedTokenBits >> i) & 1;
    }

    /// <summary>
    /// Decodes <see cref="SomaInferenceOutputBuffer.PredGlobalMotions"/> and
    /// <see cref="SomaInferenceOutputBuffer.PredNumTokens"/> into 30 Hz local joint transforms
    /// and clamped foot contacts, dropping the 3 boundary frames at each end.
    /// </summary>
    public static int DecodeOutputs(
        SomaInferenceOutputBuffer output,
        in SomaPoseTransform guideAlignment,
        in SomaPoseTransform modelToRuntime,
        ReadOnlySpan<SomaPoseTransform> referenceLocalPoses)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (referenceLocalPoses.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Reference local poses must contain {SomaModelContract.JointCount} joints.", nameof(referenceLocalPoses));

        int tokenCount = output.PredNumTokens[0];
        int rawCount = Math.Clamp(tokenCount * SomaModelContract.PosesPerToken, 0, SomaModelContract.MaxRawFrameCount);
        int frameCount = Math.Max(0, rawCount - 2 * SomaModelContract.BoundaryFrameCount);
        output.ValidFrameCount = frameCount;

        if (frameCount == 0)
            return 0;

        float[] raw = output.PredGlobalMotions;
        SomaPoseTransform[] decodedPoses = output.DecodedLocalPoses;
        float[] contacts = output.FootContacts;
        int[] parents = SomaModelContract.ParentIndices;
        const int jointCount = SomaModelContract.JointCount;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int rawFrame = SomaModelContract.BoundaryFrameCount + frame;
            int rawBase = rawFrame * SomaModelContract.GlobalMotionFeatureCount;

            int contactDstBase = frame * SomaModelContract.FootContactCount;
            for (int c = 0; c < SomaModelContract.FootContactCount; c++)
                contacts[contactDstBase + c] = Math.Clamp(raw[rawBase + OutputFootContactsOffset + c], 0.0f, 1.0f);

            var hipsPosition = new Vector3(raw[rawBase + 0], raw[rawBase + 1], raw[rawBase + 2]);
            Quaternion hipsRotation = ReadRotation6D(raw, rawBase + OutputRotationsOffset);
            SomaPoseTransform hipsAligned = SomaPoseTransform.Multiply(
                guideAlignment,
                new SomaPoseTransform(hipsPosition, hipsRotation));
            int dstBase = frame * jointCount;
            decodedPoses[dstBase + 0] = SomaPoseTransform.Multiply(modelToRuntime, hipsAligned);

            for (int joint = 1; joint < jointCount; joint++)
            {
                Quaternion childGlobal = ReadRotation6D(raw, rawBase + OutputRotationsOffset + joint * 6);
                Quaternion parentGlobal = ReadRotation6D(raw, rawBase + OutputRotationsOffset + parents[joint] * 6);
                Quaternion localRotation = SomaPoseTransform.NormalizeOrIdentity(
                    Quaternion.Inverse(parentGlobal) * childGlobal);
                decodedPoses[dstBase + joint] = new SomaPoseTransform(
                    referenceLocalPoses[joint].Position,
                    localRotation);
            }
        }

        return frameCount;
    }

    /// <summary>
    /// Smoothly distributes start and end endpoint corrections across the decoded motion so
    /// frame 0 matches <paramref name="lastContextLocalPose"/> and the final frame matches
    /// <paramref name="firstTargetLocalPose"/>.
    /// </summary>
    public static void ApplyEndpointCorrection(
        ReadOnlySpan<SomaPoseTransform> lastContextLocalPose,
        ReadOnlySpan<SomaPoseTransform> firstTargetLocalPose,
        SomaInferenceOutputBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (lastContextLocalPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Last context pose must contain {SomaModelContract.JointCount} joints.", nameof(lastContextLocalPose));
        if (firstTargetLocalPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"First target pose must contain {SomaModelContract.JointCount} joints.", nameof(firstTargetLocalPose));

        int frameCount = output.ValidFrameCount;
        if (frameCount < 2)
            return;

        const int jointCount = SomaModelContract.JointCount;
        int lastFrameBase = (frameCount - 1) * jointCount;
        SomaPoseTransform[] poses = output.DecodedLocalPoses;
        SomaPoseTransform[] startDeltas = output.StartEndpointDeltas;
        SomaPoseTransform[] endDeltas = output.EndEndpointDeltas;

        startDeltas[0] = SomaPoseTransform.ComputeRootEndpointDelta(poses[0], lastContextLocalPose[0]);
        endDeltas[0] = SomaPoseTransform.ComputeRootEndpointDelta(poses[lastFrameBase], firstTargetLocalPose[0]);

        for (int joint = 1; joint < jointCount; joint++)
        {
            startDeltas[joint] = SomaPoseTransform.ComputeDelta(poses[joint], lastContextLocalPose[joint]);
            endDeltas[joint] = SomaPoseTransform.ComputeDelta(poses[lastFrameBase + joint], firstTargetLocalPose[joint]);
        }

        float invSpan = 1.0f / (frameCount - 1);
        for (int frame = 0; frame < frameCount; frame++)
        {
            float endWeight = frame * invSpan;
            float startWeight = 1.0f - endWeight;
            int frameBase = frame * jointCount;

            poses[frameBase] = SomaPoseTransform.ApplyRootEndpointCorrection(
                poses[frameBase],
                startDeltas[0],
                endDeltas[0],
                endWeight);

            for (int joint = 1; joint < jointCount; joint++)
            {
                SomaPoseTransform withStart = SomaPoseTransform.ApplyDelta(poses[frameBase + joint], startDeltas[joint], startWeight);
                poses[frameBase + joint] = SomaPoseTransform.ApplyDelta(withStart, endDeltas[joint], endWeight);
            }
        }
    }

    /// <summary>
    /// Removes the start-seam discontinuity for all 30 joints over <paramref name="decayFrames"/>
    /// frames and gently guides only the root transform (joint 0) toward
    /// <paramref name="firstTargetLocalPose"/> so turning and speed track steering crisply while
    /// keeping SOMA's generated limb motion completely unwarped.
    /// </summary>
    public static void ApplyStartAndRootEndpointCorrection(
        ReadOnlySpan<SomaPoseTransform> lastContextLocalPose,
        ReadOnlySpan<SomaPoseTransform> firstTargetLocalPose,
        SomaInferenceOutputBuffer output,
        int decayFrames = 6,
        float endRootWeightScale = 0.85f)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (lastContextLocalPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"Last context pose must contain {SomaModelContract.JointCount} joints.", nameof(lastContextLocalPose));
        if (firstTargetLocalPose.Length < SomaModelContract.JointCount)
            throw new ArgumentException($"First target pose must contain {SomaModelContract.JointCount} joints.", nameof(firstTargetLocalPose));

        int frameCount = output.ValidFrameCount;
        if (frameCount < 2)
            return;

        const int jointCount = SomaModelContract.JointCount;
        int lastFrameBase = (frameCount - 1) * jointCount;
        SomaPoseTransform[] poses = output.DecodedLocalPoses;
        SomaPoseTransform[] startDeltas = output.StartEndpointDeltas;

        startDeltas[0] = SomaPoseTransform.ComputeRootEndpointDelta(poses[0], lastContextLocalPose[0]);
        SomaPoseTransform endRootDelta = SomaPoseTransform.ComputeRootEndpointDelta(poses[lastFrameBase], firstTargetLocalPose[0]);
        for (int joint = 1; joint < jointCount; joint++)
            startDeltas[joint] = SomaPoseTransform.ComputeDelta(poses[joint], lastContextLocalPose[joint]);

        int startSpan = Math.Clamp(decayFrames, 2, frameCount);
        float invStartSpan = 1.0f / (startSpan - 1);
        float invFullSpan = 1.0f / (frameCount - 1);
        float clampedEndScale = Math.Clamp(endRootWeightScale, 0.0f, 1.0f);

        for (int frame = 0; frame < frameCount; frame++)
        {
            float startWeight = frame < startSpan ? 1.0f - frame * invStartSpan : 0.0f;
            float endWeight = (frame * invFullSpan) * clampedEndScale;
            int frameBase = frame * jointCount;

            Quaternion startStep = startWeight > 0.0f
                ? Quaternion.Slerp(Quaternion.Identity, SomaPoseTransform.NormalizeOrIdentity(startDeltas[0].Rotation), startWeight)
                : Quaternion.Identity;
            Quaternion endStep = endWeight > 0.0f
                ? Quaternion.Slerp(Quaternion.Identity, SomaPoseTransform.NormalizeOrIdentity(endRootDelta.Rotation), endWeight)
                : Quaternion.Identity;

            Vector3 pos = poses[frameBase].Position
                + startDeltas[0].Position * startWeight
                + endRootDelta.Position * endWeight;
            Quaternion rot = SomaPoseTransform.NormalizeOrIdentity(
                SomaPoseTransform.NormalizeOrIdentity(poses[frameBase].Rotation) * startStep * endStep);
            poses[frameBase] = new SomaPoseTransform(pos, rot);

            if (startWeight > 0.0f)
            {
                for (int joint = 1; joint < jointCount; joint++)
                    poses[frameBase + joint] = SomaPoseTransform.ApplyDelta(poses[frameBase + joint], startDeltas[joint], startWeight);
            }
        }
    }

    private static Vector3 AlignPosition(Vector3 alignmentPosition, Quaternion inverseAlignmentRotation, Vector3 worldPosition)
        => Vector3.Transform(worldPosition - alignmentPosition, inverseAlignmentRotation);

    private static Quaternion ReadRotation6D(float[] source, int offset)
    {
        var xRaw = new Vector3(source[offset + 0], source[offset + 1], source[offset + 2]);
        var yRaw = new Vector3(source[offset + 3], source[offset + 4], source[offset + 5]);
        return RotationFrom6D(xRaw, yRaw);
    }
}
