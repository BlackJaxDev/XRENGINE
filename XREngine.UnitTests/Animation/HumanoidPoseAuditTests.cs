using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidPoseAuditTests
{
    [Test]
    public void Compare_ComputesBodyBoneAndMuscleErrorMetrics()
    {
        var reference = new HumanoidPoseAuditReport
        {
            Source = "UnityMecanim",
            SampleRate = 30,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    BodyPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 0.0f)),
                    BodyRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.5f },
                    ],
                    Bones =
                    [
                        new HumanoidPoseAuditBoneSample
                        {
                            Name = "LeftHand",
                            LocalRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                            RootSpacePosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 0.0f)),
                            WorldPosition = HumanoidPoseAuditVector3.From(new Vector3(1.0f, 2.0f, 3.0f)),
                        },
                    ],
                },
            ],
        };

        var actual = new HumanoidPoseAuditReport
        {
            Source = "XREngine",
            SampleRate = 30,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    BodyPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 3.0f, 4.0f)),
                    BodyRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f)),
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.25f },
                    ],
                    Bones =
                    [
                        new HumanoidPoseAuditBoneSample
                        {
                            Name = "LeftHand",
                            LocalRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f)),
                            RootSpacePosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 1.0f)),
                            WorldPosition = HumanoidPoseAuditVector3.From(new Vector3(1.0f, 2.0f, 4.0f)),
                        },
                    ],
                },
            ],
        };

        HumanoidPoseAuditComparisonReport comparison = HumanoidPoseAuditComparer.Compare(reference, actual);

        comparison.ComparedSamples.ShouldBe(1);
        comparison.BodyPositionError.Max.ShouldBe(5.0f, 0.0001f);
        comparison.BodyRotationErrorDegrees.Max.ShouldBe(90.0f, 0.0001f);

        comparison.MuscleAbsoluteError.ShouldContain(x =>
            x.Name == "Left Arm Down-Up" &&
            Math.Abs(x.Metric.Max - 0.25f) < 0.0001f);

        comparison.BoneLocalRotationErrorDegrees.ShouldContain(x =>
            x.Name == "LeftHand" &&
            Math.Abs(x.Metric.Max - 90.0f) < 0.0001f);

        comparison.BoneRootSpacePositionError.ShouldContain(x =>
            x.Name == "LeftHand" &&
            Math.Abs(x.Metric.Max - 1.0f) < 0.0001f);
    }
}
