using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation.MotionBricks;

namespace XREngine.UnitTests.Animation.MotionBricks;

public sealed class SomaTensorCodecTests
{
    [Test]
    public void Contract_HasConsistent30JointTopologyAndTensorSizes()
    {
        SomaModelContract.JointNames.Length.ShouldBe(SomaModelContract.JointCount);
        SomaModelContract.ParentIndices.Length.ShouldBe(SomaModelContract.JointCount);
        SomaModelContract.JointNames[0].ShouldBe("Hips");
        SomaModelContract.ParentIndices[0].ShouldBe(-1);
        SomaModelContract.JointNames[SomaModelContract.LeftLegJointIndex].ShouldBe("LeftLeg");
        SomaModelContract.JointNames[SomaModelContract.RightLegJointIndex].ShouldBe("RightLeg");

        for (int joint = 1; joint < SomaModelContract.JointCount; joint++)
        {
            int parent = SomaModelContract.ParentIndices[joint];
            parent.ShouldBeGreaterThanOrEqualTo(0);
            parent.ShouldBeLessThan(joint);
        }

        SomaModelContract.LocalPoseFeatureCount.ShouldBe(267);
        SomaModelContract.GlobalMotionFeatureCount.ShouldBe(366);
        SomaModelContract.GlobalRootTensorElementCount.ShouldBe(40);
        SomaModelContract.LocalRootTensorElementCount.ShouldBe(32);
        SomaModelContract.LocalPosesTensorElementCount.ShouldBe(2136);
        SomaModelContract.PredGlobalMotionsTensorElementCount.ShouldBe(64 * 366);
        SomaModelContract.ValidFrameCount(6).ShouldBe(18);
        SomaModelContract.ValidFrameCount(16).ShouldBe(58);
        Should.Throw<ArgumentOutOfRangeException>(() => SomaModelContract.ValidFrameCount(5));
        Should.Throw<ArgumentOutOfRangeException>(() => SomaModelContract.ValidFrameCount(17));
    }

    [Test]
    public void Rotation6D_RoundTripsArbitraryOrientationsAndOrthonormalizesInputs()
    {
        Quaternion[] testRotations =
        [
            Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.73f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(1.25f, -0.45f, 0.88f)),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-2.7f, 0.91f, -1.6f)),
        ];

        foreach (Quaternion expected in testRotations)
        {
            SomaTensorCodec.RotationTo6D(expected, out Vector3 xAxis, out Vector3 yAxis);

            // Perturb slightly in scale and shear to verify Gram-Schmidt orthonormalization.
            Vector3 noisyX = xAxis * 1.35f;
            Vector3 noisyY = yAxis * 0.8f + xAxis * 0.25f;
            Quaternion recovered = SomaTensorCodec.RotationFrom6D(noisyX, noisyY);

            Vector3.Distance(Vector3.Transform(Vector3.UnitX, recovered), xAxis).ShouldBeLessThan(1.0e-5f);
            Vector3.Distance(Vector3.Transform(Vector3.UnitY, recovered), yAxis).ShouldBeLessThan(1.0e-5f);
            Vector3.Distance(
                Vector3.Transform(Vector3.UnitZ, recovered),
                Vector3.Transform(Vector3.UnitZ, expected)).ShouldBeLessThan(1.0e-5f);
        }
    }

    [Test]
    public void ComputeGuideAlignment_CentersHipsXZPreservesHeightAndAlignsHeading()
    {
        SomaPoseTransform[] localPose = CreateRestLocalPose(
            hipsPosition: new Vector3(4.5f, 0.98f, -2.25f),
            hipsYawRadians: 0.65f);
        var worldPose = new SomaPoseTransform[SomaModelContract.JointCount];
        SomaTensorCodec.ComputeWorldPose(localPose, SomaPoseTransform.Identity, worldPose);

        var desiredHeading = new Vector2(1.0f, 0.0f);
        SomaPoseTransform alignment = SomaTensorCodec.ComputeGuideAlignment(worldPose, desiredHeading);

        alignment.Position.X.ShouldBe(4.5f, 1.0e-5f);
        alignment.Position.Y.ShouldBe(0.0f, 1.0e-6f);
        alignment.Position.Z.ShouldBe(-2.25f, 1.0e-5f);

        SomaPoseTransform invAlignment = SomaPoseTransform.Inverse(alignment);
        Vector3 alignedHips = invAlignment.TransformPoint(worldPose[0].Position);
        alignedHips.X.ShouldBe(0.0f, 1.0e-5f);
        alignedHips.Y.ShouldBe(0.98f, 1.0e-5f);
        alignedHips.Z.ShouldBe(0.0f, 1.0e-5f);

        Vector3 alignedRight = invAlignment.TransformPoint(worldPose[SomaModelContract.RightLegJointIndex].Position);
        Vector3 alignedLeft = invAlignment.TransformPoint(worldPose[SomaModelContract.LeftLegJointIndex].Position);
        float alignedHeading = SomaTensorCodec.ComputeHeadingFromHipPositions(alignedRight, alignedLeft);
        float expectedHeading = MathF.Atan2(desiredHeading.X, desiredHeading.Y);
        MathF.Abs(alignedHeading - expectedHeading).ShouldBeLessThan(1.0e-4f);
    }

    [Test]
    public void EncodeInputs_SetsConditionMasksPaddingAndVelocityDerivativesCorrectly()
    {
        var contextPoses = new SomaPoseTransform[SomaModelContract.ContextPoseCount * SomaModelContract.JointCount];
        for (int frame = 0; frame < SomaModelContract.ContextPoseCount; frame++)
        {
            SomaPoseTransform[] pose = CreateRestLocalPose(
                hipsPosition: new Vector3(frame * 0.1f, 1.0f, 0.0f),
                hipsYawRadians: 0.0f);
            pose.CopyTo(contextPoses, frame * SomaModelContract.JointCount);
        }

        const int suppliedTargets = 2;
        var targetPoses = new SomaPoseTransform[suppliedTargets * SomaModelContract.JointCount];
        for (int frame = 0; frame < suppliedTargets; frame++)
        {
            SomaPoseTransform[] pose = CreateRestLocalPose(
                hipsPosition: new Vector3(1.0f + frame * 0.2f, 1.0f, 0.0f),
                hipsYawRadians: 0.0f);
            pose.CopyTo(targetPoses, frame * SomaModelContract.JointCount);
        }

        var config = new SomaInferenceConfig(
            SomaInferenceConfig.CreateAllowedTokenBits(8, 12),
            correctGeneratedDiff: true,
            headingAlignment: new Vector2(0.0f, 1.0f));
        var input = new SomaInferenceInputBuffer();

        SomaTensorCodec.EncodeInputs(
            contextPoses,
            targetPoses,
            suppliedTargets,
            config,
            SomaPoseTransform.Identity,
            input);

        // Context (0..3) and first 2 targets (4..5) are active; target slots 6..7 are inactive.
        input.HasGlobalRootValues.ShouldBe([1, 1, 1, 1, 1, 1, 0, 0]);
        input.HasLocalPoses.ShouldBe([1, 1, 1, 1, 1, 1, 0, 0]);
        // Local root derivatives are disabled at the last context frame (3) and last active target (5).
        input.HasLocalRootValues.ShouldBe([1, 1, 1, 0, 1, 0, 0, 0]);
        input.AllowedPredNumTokens.ShouldBe([0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0]);

        // Velocity at frame 3 (last context) and frame 5 (last target) must be zero.
        input.LocalRootValues[3 * 4 + 1].ShouldBe(0.0f, 1.0e-5f);
        input.LocalRootValues[3 * 4 + 2].ShouldBe(0.0f, 1.0e-5f);
        input.LocalRootValues[5 * 4 + 1].ShouldBe(0.0f, 1.0e-5f);
        input.LocalRootValues[5 * 4 + 2].ShouldBe(0.0f, 1.0e-5f);

        // Context frames move +0.1m/frame along X => 3.0 m/s at 30 Hz.
        input.LocalRootValues[0 * 4 + 1].ShouldBe(3.0f, 1.0e-4f);
    }

    [Test]
    public void DecodeOutputs_TrimsBoundaryFramesRestoresRootAndClampsFootContacts()
    {
        SomaPoseTransform[] referenceLocal = CreateRestLocalPose(Vector3.Zero, 0.0f);
        var guideAlignment = new SomaPoseTransform(
            new Vector3(2.0f, 0.0f, -3.0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));
        var output = new SomaInferenceOutputBuffer();
        output.PredNumTokens[0] = 6; // 6 * 4 - 6 = 18 valid frames.

        Quaternion expectedHipsModelRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f);
        Quaternion expectedSpine1LocalRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.35f);
        Quaternion expectedSpine1GlobalRot = Quaternion.Normalize(expectedHipsModelRot * expectedSpine1LocalRot);

        int rawFrame = SomaModelContract.BoundaryFrameCount + 0;
        int rawBase = rawFrame * SomaModelContract.GlobalMotionFeatureCount;
        output.PredGlobalMotions[rawBase + 0] = 0.5f;
        output.PredGlobalMotions[rawBase + 1] = 1.02f;
        output.PredGlobalMotions[rawBase + 2] = -0.25f;

        int rotOffset = SomaModelContract.GlobalRootFeatureCount + SomaModelContract.LocalPosePositionFeatureCount;
        for (int joint = 0; joint < SomaModelContract.JointCount; joint++)
        {
            Quaternion jointGlobal = joint == 1 ? expectedSpine1GlobalRot : expectedHipsModelRot;
            SomaTensorCodec.RotationTo6D(jointGlobal, out Vector3 xAxis, out Vector3 yAxis);
            int dst = rawBase + rotOffset + joint * 6;
            output.PredGlobalMotions[dst + 0] = xAxis.X;
            output.PredGlobalMotions[dst + 1] = xAxis.Y;
            output.PredGlobalMotions[dst + 2] = xAxis.Z;
            output.PredGlobalMotions[dst + 3] = yAxis.X;
            output.PredGlobalMotions[dst + 4] = yAxis.Y;
            output.PredGlobalMotions[dst + 5] = yAxis.Z;
        }

        int contactOffset = SomaModelContract.GlobalMotionFeatureCount - SomaModelContract.FootContactCount;
        output.PredGlobalMotions[rawBase + contactOffset + 0] = -0.2f;
        output.PredGlobalMotions[rawBase + contactOffset + 1] = 0.45f;
        output.PredGlobalMotions[rawBase + contactOffset + 2] = 1.4f;
        output.PredGlobalMotions[rawBase + contactOffset + 3] = 0.0f;

        int validFrames = SomaTensorCodec.DecodeOutputs(
            output,
            guideAlignment,
            SomaPoseTransform.Identity,
            referenceLocal);

        validFrames.ShouldBe(18);
        output.ValidFrameCount.ShouldBe(18);

        ReadOnlySpan<SomaPoseTransform> frame0 = output.GetFrameLocalPose(0);
        SomaPoseTransform expectedRoot = SomaPoseTransform.Multiply(
            guideAlignment,
            new SomaPoseTransform(new Vector3(0.5f, 1.02f, -0.25f), expectedHipsModelRot));

        Vector3.Distance(frame0[0].Position, expectedRoot.Position).ShouldBeLessThan(1.0e-5f);
        MathF.Abs(Quaternion.Dot(frame0[0].Rotation, expectedRoot.Rotation)).ShouldBeGreaterThan(0.99999f);
        MathF.Abs(Quaternion.Dot(frame0[1].Rotation, expectedSpine1LocalRot)).ShouldBeGreaterThan(0.99999f);
        frame0[1].Position.ShouldBe(referenceLocal[1].Position);

        ReadOnlySpan<float> contacts0 = output.GetFrameFootContacts(0);
        contacts0[0].ShouldBe(0.0f);
        contacts0[1].ShouldBe(0.45f, 1.0e-6f);
        contacts0[2].ShouldBe(1.0f);
        contacts0[3].ShouldBe(0.0f);
    }

    [Test]
    public void ApplyEndpointCorrection_MatchesStartContextAndEndTargetPoses()
    {
        SomaPoseTransform[] lastContext = CreateRestLocalPose(new Vector3(0.0f, 1.0f, 0.0f), 0.1f);
        SomaPoseTransform[] firstTarget = CreateRestLocalPose(new Vector3(2.0f, 1.0f, 0.5f), 0.7f);
        firstTarget[1] = new SomaPoseTransform(firstTarget[1].Position, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f));

        var output = new SomaInferenceOutputBuffer();
        output.PredNumTokens[0] = 6; // 18 valid frames.
        SomaPoseTransform[] referenceLocal = CreateRestLocalPose(Vector3.Zero, 0.0f);
        SomaTensorCodec.DecodeOutputs(output, SomaPoseTransform.Identity, SomaPoseTransform.Identity, referenceLocal);

        // Seed synthetic slightly drifted generated trajectory.
        for (int frame = 0; frame < output.ValidFrameCount; frame++)
        {
            float t = frame / (float)(output.ValidFrameCount - 1);
            int baseIdx = frame * SomaModelContract.JointCount;
            output.DecodedLocalPoses[baseIdx] = new SomaPoseTransform(
                new Vector3(0.2f + t * 1.5f, 0.95f, -0.1f + t * 0.4f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.0f + t * 0.4f));
        }

        SomaTensorCodec.ApplyEndpointCorrection(lastContext, firstTarget, output);

        ReadOnlySpan<SomaPoseTransform> first = output.GetFrameLocalPose(0);
        ReadOnlySpan<SomaPoseTransform> last = output.GetFrameLocalPose(output.ValidFrameCount - 1);

        Vector3.Distance(first[0].Position, lastContext[0].Position).ShouldBeLessThan(1.0e-5f);
        MathF.Abs(Quaternion.Dot(first[0].Rotation, lastContext[0].Rotation)).ShouldBeGreaterThan(0.99999f);

        Vector3.Distance(last[0].Position, firstTarget[0].Position).ShouldBeLessThan(1.0e-5f);
        MathF.Abs(Quaternion.Dot(last[0].Rotation, firstTarget[0].Rotation)).ShouldBeGreaterThan(0.99999f);
        MathF.Abs(Quaternion.Dot(last[1].Rotation, firstTarget[1].Rotation)).ShouldBeGreaterThan(0.99999f);
    }

    [Test]
    public void TensorRtBridge_ReportsActionableDiagnosticWhenConfigurationIsInvalid()
    {
        bool created = MotionBricksTensorRtBridge.TryCreateSession(
            new MotionBricksSessionConfig { OnnxModelPath = "" },
            out MotionBricksTensorRtSession? session,
            out string? error);

        created.ShouldBeFalse();
        session.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void TensorRtBridge_NativeDllExportsBindCleanlyAndReportExplicitAvailability()
    {
        bool available = MotionBricksTensorRtBackend.Instance.IsAvailable(out string? error);
        if (available)
        {
            error.ShouldBeNull();
            MotionBricksTensorRtBridge.GetTensorRtVersion().ShouldBeGreaterThanOrEqualTo(100000);
        }
        else
        {
            error.ShouldNotBeNullOrWhiteSpace();
            error.ShouldNotContain("missing the required export");
        }
    }

    private static SomaPoseTransform[] CreateRestLocalPose(Vector3 hipsPosition, float hipsYawRadians)
    {
        var poses = new SomaPoseTransform[SomaModelContract.JointCount];
        for (int i = 0; i < poses.Length; i++)
            poses[i] = SomaPoseTransform.Identity;

        poses[SomaModelContract.RootJointIndex] = new SomaPoseTransform(
            hipsPosition,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, hipsYawRadians));
        // Place LeftLeg at +X and RightLeg at -X in local hips space so forward = cross(Up, Right - Left) = +Z.
        poses[SomaModelContract.LeftLegJointIndex] = new SomaPoseTransform(
            new Vector3(0.1f, -0.05f, 0.0f),
            Quaternion.Identity);
        poses[SomaModelContract.RightLegJointIndex] = new SomaPoseTransform(
            new Vector3(-0.1f, -0.05f, 0.0f),
            Quaternion.Identity);
        return poses;
    }
}
