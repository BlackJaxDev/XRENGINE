using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;
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
            Source = "SyntheticReference",
            SampleRate = 30,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    BodyPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 0.0f)),
                    BodyRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                    ProjectedRootPosition = HumanoidPoseAuditVector3.From(Vector3.Zero),
                    ProjectedRootRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                    TemporalRootMotionTranslation = HumanoidPoseAuditVector3.From(Vector3.Zero),
                    TemporalRootMotionRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                    ComposedHipsLocalPosition = HumanoidPoseAuditVector3.From(Vector3.Zero),
                    ComposedHipsLocalRotation = HumanoidPoseAuditQuaternion.From(Quaternion.Identity),
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.5f },
                    ],
                    Bones =
                    [
                        new HumanoidPoseAuditBoneSample
                        {
                            Name = "LeftHand",
                            LocalPosition = HumanoidPoseAuditVector3.From(Vector3.Zero),
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
            Source = "SyntheticActual",
            SampleRate = 30,
            Samples =
            [
                new HumanoidPoseAuditSample
                {
                    Index = 0,
                    TimeSeconds = 0.0f,
                    BodyPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 3.0f, 4.0f)),
                    BodyRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f)),
                    ProjectedRootPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 2.0f)),
                    ProjectedRootRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.25f)),
                    TemporalRootMotionTranslation = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 0.0f, 3.0f)),
                    TemporalRootMotionRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6.0f)),
                    ComposedHipsLocalPosition = HumanoidPoseAuditVector3.From(new Vector3(0.0f, 4.0f, 0.0f)),
                    ComposedHipsLocalRotation = HumanoidPoseAuditQuaternion.From(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3.0f)),
                    Muscles =
                    [
                        new HumanoidPoseAuditNamedFloat { Name = "Left Arm Down-Up", Value = 0.25f },
                    ],
                    Bones =
                    [
                        new HumanoidPoseAuditBoneSample
                        {
                            Name = "LeftHand",
                            LocalPosition = HumanoidPoseAuditVector3.From(new Vector3(3.0f, 0.0f, 0.0f)),
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
        comparison.ProjectedRootPositionError.Max.ShouldBe(2.0f, 0.0001f);
        comparison.ProjectedRootRotationErrorDegrees.Max.ShouldBe(45.0f, 0.0001f);
        comparison.TemporalRootMotionTranslationError.Max.ShouldBe(3.0f, 0.0001f);
        comparison.TemporalRootMotionRotationErrorDegrees.Max.ShouldBe(30.0f, 0.0001f);
        comparison.ComposedHipsLocalPositionError.Max.ShouldBe(4.0f, 0.0001f);
        comparison.ComposedHipsLocalRotationErrorDegrees.Max.ShouldBe(60.0f, 0.0001f);
        comparison.ComposedHipsLocalPositionError.WorstSample.ShouldNotBeNull();
        comparison.ComposedHipsLocalPositionError.WorstSample!.ReferenceTimeSeconds.ShouldBe(0.0f);

        comparison.MuscleAbsoluteError.ShouldContain(x =>
            x.Name == "Left Arm Down-Up" &&
            Math.Abs(x.Metric.Max - 0.25f) < 0.0001f);

        comparison.BoneLocalRotationErrorDegrees.ShouldContain(x =>
            x.Name == "LeftHand" &&
            Math.Abs(x.Metric.Max - 90.0f) < 0.0001f);

        comparison.BoneLocalPositionError.ShouldContain(x =>
            x.Name == "LeftHand" &&
            Math.Abs(x.Metric.Max - 3.0f) < 0.0001f &&
            x.Metric.WorstSample != null &&
            x.Metric.WorstSample.ActualIndex == 0);

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
    public void Deserialize_LoadsSourceRawCurvesAndDefaultMuscleRanges()
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
        const string yaml = """
AnimationClip:
  m_Name: Audit
  m_SampleRate: 30
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 0
    m_LoopTime: 0
  m_FloatCurves:
    - path: ''
      attribute: LeftHand.Index.Spread
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.5
    - path: ''
      attribute: Left Eye Down-Up
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.25
    - path: ''
      attribute: RootT.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: -1
    - path: ''
      attribute: RootT.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 2
    - path: ''
      attribute: RootT.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 3
    - path: ''
      attribute: RootQ.x
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootQ.y
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: -0.70710677
    - path: ''
      attribute: RootQ.z
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0
    - path: ''
      attribute: RootQ.w
      classID: 95
      curve:
        m_Curve:
          - time: 0
            value: 0.70710677
""";
        string clipPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "HumanoidPoseAuditTests",
            $"{Guid.NewGuid():N}.anim");
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
        File.WriteAllText(clipPath, yaml);
        AnimationClip clip = AnimYamlImporter.Import(clipPath);

        var clipComponent = root.AddComponent<AnimationClipComponent>()!;
        clipComponent.Animation = clip;

        var humanoid = root.AddComponent<HumanoidComponent>()!;

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
