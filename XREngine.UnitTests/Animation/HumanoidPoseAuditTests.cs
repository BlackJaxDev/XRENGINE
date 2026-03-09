using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidPoseAuditTests
{
    private static readonly MethodInfo ResolveOutputPathMethod =
        typeof(HumanoidPoseAuditComponent).GetMethod("ResolveOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate HumanoidPoseAuditComponent.ResolveOutputPath.");
    private static readonly MethodInfo ResolveComparisonOutputPathMethod =
        typeof(HumanoidPoseAuditComponent).GetMethod("ResolveComparisonOutputPath", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Failed to locate HumanoidPoseAuditComponent.ResolveComparisonOutputPath.");

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

    [Test]
    public void Compare_CanonicalizesHumanTraitAndCurveAttributeMuscleNames()
    {
        var reference = new HumanoidPoseAuditReport
        {
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Index Spread", Value = 0.75f },
                    ],
                },
            ],
        };

        var actual = new HumanoidPoseAuditReport
        {
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "LeftHand.Index.Spread", Value = 0.25f },
                    ],
                },
            ],
        };

        HumanoidPoseAuditComparisonReport comparison = HumanoidPoseAuditComparer.Compare(reference, actual);

        comparison.MuscleAbsoluteError.ShouldContain(x =>
            x.Name == "Left Index Spread" &&
            Math.Abs(x.Metric.Max - 0.5f) < 0.0001f);
    }

    [Test]
    public void Compare_AlignsSamplesByTimeWhenSampleRatesDiffer()
    {
        var reference = new HumanoidPoseAuditReport
        {
            SampleRate = 25,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.10f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 1,
                    TimeSeconds = 0.04f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.20f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 2,
                    TimeSeconds = 0.08f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.30f },
                    ],
                },
            ],
        };

        var actual = new HumanoidPoseAuditReport
        {
            SampleRate = 60,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.10f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 1,
                    TimeSeconds = 1.0f / 60.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 10.0f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 2,
                    TimeSeconds = 2.0f / 60.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.20f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 3,
                    TimeSeconds = 3.0f / 60.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 10.0f },
                    ],
                },
                new HumanoidPoseAuditSample
                {
                    Index = 4,
                    TimeSeconds = 4.0f / 60.0f,
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.30f },
                    ],
                },
            ],
        };

        HumanoidPoseAuditComparisonReport comparison = HumanoidPoseAuditComparer.Compare(reference, actual);

        comparison.ComparedSamples.ShouldBe(3);
        comparison.MuscleAbsoluteError.ShouldContain(x =>
            x.Name == "Left Arm Down-Up" &&
            Math.Abs(x.Metric.Max) < 0.0001f &&
            Math.Abs(x.Metric.Average) < 0.0001f);
    }

    [Test]
    public void Overlay_TryFindClosestSample_SelectsNearestTime()
    {
        HumanoidPoseAuditSample[] samples =
        [
            new() { Index = 0, TimeSeconds = 0.0f },
            new() { Index = 1, TimeSeconds = 0.10f },
            new() { Index = 2, TimeSeconds = 0.20f },
        ];

        bool found = HumanoidPoseAuditOverlayComponent.TryFindClosestSample(samples, 0.14f, out HumanoidPoseAuditSample? sample);

        found.ShouldBeTrue();
        sample.ShouldNotBeNull();
        sample!.Index.ShouldBe(1);
    }

    [Test]
    public void Overlay_ReconstructsReferenceWorldPositionFromRootSpace()
    {
        HumanoidPoseAuditBoneSample bone = new()
        {
            Name = "LeftHand",
            RootSpacePosition = HumanoidPoseAuditVector3.From(new Vector3(1.0f, 0.0f, 0.0f)),
        };
        Matrix4x4 rootWorld =
            Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f) *
            Matrix4x4.CreateTranslation(new Vector3(5.0f, 2.0f, 3.0f));

        Vector3 world = HumanoidPoseAuditOverlayComponent.GetReferenceBoneWorldPosition(bone, rootWorld);

        world.X.ShouldBe(5.0f, 0.0001f);
        world.Y.ShouldBe(2.0f, 0.0001f);
        world.Z.ShouldBe(2.0f, 0.0001f);
    }

    [Test]
    public void Overlay_ComputeReferenceScale_MatchesAvatarRootSpaceSize()
    {
        HumanoidPoseAuditSample sample = new()
        {
            Bones =
            [
                new HumanoidPoseAuditBoneSample
                {
                    Name = "Hips",
                    RootSpacePosition = HumanoidPoseAuditVector3.From(Vector3.Zero),
                },
                new HumanoidPoseAuditBoneSample
                {
                    Name = "Spine",
                    RootSpacePosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 1.0f, 0.0f)),
                },
            ],
        };
        Dictionary<string, Vector3> actualRootSpace = new(StringComparer.Ordinal)
        {
            ["Hips"] = Vector3.Zero,
            ["Spine"] = new Vector3(0.0f, 2.0f, 0.0f),
        };

        float scale = HumanoidPoseAuditOverlayComponent.ComputeReferenceScale(sample, actualRootSpace);

        scale.ShouldBe(2.0f, 0.0001f);
    }

    [Test]
    public void Overlay_ResolveMuscleDebugBoneName_AnchorsLabelsToDrivenBones()
    {
        HumanoidPoseAuditOverlayComponent.ResolveMuscleDebugBoneName(EHumanoidValue.LeftArmDownUp).ShouldBe("LeftUpperArm");
        HumanoidPoseAuditOverlayComponent.ResolveMuscleDebugBoneName(EHumanoidValue.RightForearmTwistInOut).ShouldBe("RightLowerArm");
        HumanoidPoseAuditOverlayComponent.ResolveMuscleDebugBoneName(EHumanoidValue.LeftHandIndexSpread).ShouldBe("LeftHand");
        HumanoidPoseAuditOverlayComponent.ResolveMuscleDebugBoneName(EHumanoidValue.HeadTurnLeftRight).ShouldBe("Head");
    }

    [Test]
    public void Overlay_BuildMuscleDebugText_CombinesSameBoneEntriesIntoOneMultilineLabel()
    {
        string text = HumanoidPoseAuditOverlayComponent.BuildMuscleDebugText(
        [
            ("Left Arm Down-Up", 0.750f),
            ("Left Arm Front-Back", -0.250f),
            ("Left Arm Twist In-Out", 0.125f),
        ]);

        text.ShouldBe("Left Arm Down-Up: +0.750\nLeft Arm Front-Back: -0.250\nLeft Arm Twist In-Out: +0.125");
    }

    [Test]
    public void Deserialize_LoadsUnityRawCurvesAndDefaultMuscleRanges()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "Source": "UnityMecanim",
              "ClipName": "Sexy Walk",
              "AvatarName": "Jax",
              "DurationSeconds": 1.0,
              "SampleRate": 30,
              "SampleCount": 1,
              "MuscleDefaultRanges": [
                { "Name": "Left Arm Down-Up", "Min": -60.0, "Max": 100.0 }
              ],
              "Samples": [
                {
                  "Index": 0,
                  "TimeSeconds": 0.0,
                  "BodyPosition": { "X": 0.0, "Y": 0.0, "Z": 0.0 },
                  "BodyRotation": { "X": 0.0, "Y": 0.0, "Z": 0.0, "W": 1.0 },
                  "Muscles": [
                    { "Name": "Left Arm Down-Up", "Value": 0.4 }
                  ],
                  "RawCurves": [
                    { "Path": "", "TypeName": "UnityEngine.Animator", "PropertyName": "Left Arm Down-Up", "Value": -0.687864 }
                  ],
                  "Bones": []
                }
              ]
            }
            """;

        HumanoidPoseAuditReport report = JsonConvert.DeserializeObject<HumanoidPoseAuditReport>(json)!;

        report.SchemaVersion.ShouldBe(2);
        report.MuscleDefaultRanges.Count.ShouldBe(1);
        report.MuscleDefaultRanges[0].Name.ShouldBe("Left Arm Down-Up");
        report.MuscleDefaultRanges[0].Min.ShouldBe(-60.0f);
        report.MuscleDefaultRanges[0].Max.ShouldBe(100.0f);
        report.Samples.Count.ShouldBe(1);
        report.Samples[0].RawCurves.Count.ShouldBe(1);
        report.Samples[0].RawCurves[0].PropertyName.ShouldBe("Left Arm Down-Up");
        report.Samples[0].RawCurves[0].Value.ShouldBe(-0.687864f, 0.000001f);
    }

    [Test]
    public void Sample_UsesHumanTraitMuscleNamesAndExportsRawCurveInputs()
    {
        var root = new SceneNode("Root", new Transform());
        var clip = new AnimationClip
        {
            Name = "Audit",
            LengthInSeconds = 0.0f,
            RootMember = new AnimationMember("Root", EAnimationMemberType.Group),
        };

        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;

        var humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.SetValue(EHumanoidValue.LeftHandIndexSpread, 0.5f);
    humanoid.SetValue(EHumanoidValue.LeftEyeDownUp, 0.25f);
        humanoid.SetRootPosition(new Vector3(1.0f, 2.0f, 3.0f));
        humanoid.SetRootRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f));

        HumanoidPoseAuditReport report = HumanoidPoseAuditSampler.Sample(clipComponent, humanoid, sampleRateOverride: 30);

        report.SampleCount.ShouldBe(1);
        HumanoidPoseAuditSample sample = report.Samples[0];
        sample.BodyPosition.Value.ShouldBe(new Vector3(1.0f, 2.0f, 3.0f));
        Quaternion expectedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        if (Quaternion.Dot(sample.BodyRotation.Value, expectedRotation) < 0.0f)
            expectedRotation = Quaternion.Negate(expectedRotation);
        sample.BodyRotation.Value.X.ShouldBe(expectedRotation.X, 0.0001f);
        sample.BodyRotation.Value.Y.ShouldBe(expectedRotation.Y, 0.0001f);
        sample.BodyRotation.Value.Z.ShouldBe(expectedRotation.Z, 0.0001f);
        sample.BodyRotation.Value.W.ShouldBe(expectedRotation.W, 0.0001f);
        sample.Muscles[0].Name.ShouldBe("Spine Front-Back");
        sample.Muscles.ShouldContain(x => x.Name == "Left Index Spread" && Math.Abs(x.Value - 0.5f) < 0.0001f);
        sample.RawCurves.ShouldContain(x => x.TypeName == typeof(HumanoidComponent).FullName && x.PropertyName == "LeftHand.Index.Spread" && Math.Abs(x.Value - 0.5f) < 0.0001f);
        sample.RawCurves.ShouldContain(x => x.TypeName == typeof(HumanoidComponent).FullName && x.PropertyName == "Left Eye Down-Up" && Math.Abs(x.Value - 0.25f) < 0.0001f);
    }

    [Test]
    public void OutputPaths_DefaultToDesktopWhenUnset()
    {
        var component = new HumanoidPoseAuditComponent();
        var report = new HumanoidPoseAuditReport
        {
            ClipName = "Sexy Walk",
        };

        string outputPath = InvokePrivate<string>(ResolveOutputPathMethod, component, report);
        string comparisonPath = InvokePrivate<string>(ResolveComparisonOutputPathMethod, component, outputPath);

        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        outputPath.ShouldBe(Path.Combine(desktopPath, "Sexy Walk_humanoid_pose_audit.json"));
        comparisonPath.ShouldBe(Path.Combine(desktopPath, "Sexy Walk_humanoid_pose_audit.comparison.json"));
    }

    private static T InvokePrivate<T>(MethodInfo method, object target, params object?[]? args)
        => (T)(method.Invoke(target, args) ?? throw new InvalidOperationException($"Private method '{method.Name}' returned null."));
}
