using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidBodyRootCompensationTests
{
    [Test]
    public void ImportedBodyTransaction_IsAtomicAndIndependentOfScalarRegistrationOrder()
    {
        HumanoidComponent humanoid = CreateHumanoid(out Transform hips);
        HumanoidImportedBodySample canonical = CreateBodySample(
            new Vector3(0.25f, 1.0f, -0.5f),
            Quaternion.Identity);
        Vector3 position = new(0.75f, 1.25f, 0.5f);
        Quaternion rotation = Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll(0.35f, -0.2f, 0.1f));

        (HumanoidImportedBodySample Sample, Vector3 Delta, Quaternion Rotation, Vector3 HipsPosition, Quaternion HipsRotation) forward =
            ApplyTransaction(humanoid, hips, canonical, position, rotation, reverseOrder: false);

        humanoid.ResetPose();

        (HumanoidImportedBodySample Sample, Vector3 Delta, Quaternion Rotation, Vector3 HipsPosition, Quaternion HipsRotation) reverse =
            ApplyTransaction(humanoid, hips, canonical, position, rotation, reverseOrder: true);

        reverse.Sample.ShouldBe(forward.Sample);
        AssertVectorEquivalent(reverse.Delta, forward.Delta);
        AssertQuaternionEquivalent(reverse.Rotation, forward.Rotation);
        AssertVectorEquivalent(reverse.HipsPosition, forward.HipsPosition);
        AssertQuaternionEquivalent(reverse.HipsRotation, forward.HipsRotation);
    }

    [Test]
    public void ImportedBodyTransaction_PartialSampleUsesNeutralDefaultsInsteadOfPriorValues()
    {
        HumanoidComponent humanoid = CreateHumanoid(out _);
        object firstOwner = new();
        HumanoidImportedBodySample canonical = CreateBodySample(Vector3.Zero, Quaternion.Identity);

        humanoid.BeginImportedBodySampleTransaction(firstOwner, canonical, hasCanonicalSample: true).ShouldBeTrue();
        humanoid.SetRootPosition(new Vector3(4.0f, 5.0f, 6.0f));
        humanoid.SetRootRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.75f));
        humanoid.CommitImportedBodySampleTransaction(firstOwner).ShouldBeTrue();

        object partialOwner = new();
        humanoid.BeginImportedBodySampleTransaction(partialOwner, canonical, hasCanonicalSample: true).ShouldBeTrue();
        humanoid.SetRootPositionX(2.0f);
        humanoid.CommitImportedBodySampleTransaction(partialOwner).ShouldBeTrue();

        HumanoidImportedBodySample partial = humanoid.CurrentImportedMappedBodySample;
        partial.Position.ShouldBe(new Vector3(2.0f, 0.0f, 0.0f));
        partial.Rotation.ShouldBe(Quaternion.Identity);
        partial.Channels.ShouldBe(EHumanoidImportedBodySampleChannels.PositionX);
    }

    [Test]
    public void ProjectedRootPose_KeepsXZ_Y_AndYawPoliciesIndependent()
    {
        HumanoidComponent humanoid = CreateHumanoid(out _);
        var settings = new ImportedHumanoidClipRootMotionSettings
        {
            BakePositionXZIntoPose = false,
            KeepOriginalPositionXZ = true,
            BakePositionYIntoPose = false,
            BakeOrientationIntoPose = false,
            KeepOriginalOrientation = false,
        };
        Quaternion yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);

        HumanoidProjectedRootPose pose = humanoid.CalculateProjectedRootPose(
            currentImportedPosition: new Vector3(1.0f, 2.0f, 3.0f),
            canonicalImportedPosition: Vector3.Zero,
            currentImportedRotation: yaw90,
            canonicalImportedRotation: Quaternion.Identity,
            weightedMotionScale: 2.0f,
            weight: 1.0f,
            settings,
            ReadOnlySpan<float>.Empty);

        pose.Channels.ShouldBe(
            EHumanoidProjectedRootChannels.PositionXZ |
            EHumanoidProjectedRootChannels.RotationYaw);
        AssertVectorEquivalent(pose.Position, new Vector3(2.0f, 0.0f, 4.0f));
        AssertQuaternionEquivalent(pose.Rotation, yaw90);

        settings.BakePositionXZIntoPose = true;
        HumanoidProjectedRootPose yawOnly = humanoid.CalculateProjectedRootPose(
            new Vector3(1.0f, 2.0f, 3.0f),
            Vector3.Zero,
            yaw90,
            Quaternion.Identity,
            2.0f,
            1.0f,
            settings,
            ReadOnlySpan<float>.Empty);
        yawOnly.Channels.ShouldBe(EHumanoidProjectedRootChannels.RotationYaw);

        settings.BakeOrientationIntoPose = true;
        HumanoidProjectedRootPose baked = humanoid.CalculateProjectedRootPose(
            new Vector3(1.0f, 2.0f, 3.0f),
            Vector3.Zero,
            yaw90,
            Quaternion.Identity,
            2.0f,
            1.0f,
            settings,
            ReadOnlySpan<float>.Empty);
        baked.Channels.ShouldBe(EHumanoidProjectedRootChannels.None);
    }

    [Test]
    public void ProjectedRootY_CoupledModelEvaluatesSeparatelyFromBodyPose()
    {
        int featureCount = ImportedHumanoidCoupledBoneModel.CalculateFeatureCount(1, 3);
        var model = new ImportedHumanoidCoupledBoneModel
        {
            BoneName = "Hips",
            Muscles = [EHumanoidValue.SpineFrontBack],
            MaximumPolynomialDegree = 3,
            NegativeEndpointRotations = [Quaternion.Identity],
            PositiveEndpointRotations = [Quaternion.Identity],
            NegativeEndpointPositionDeltas = [Vector3.Zero],
            PositiveEndpointPositionDeltas = [Vector3.Zero],
            RotationResidualCoefficients = new Vector3[featureCount],
            PositionResidualCoefficients = new Vector3[featureCount],
            ProjectedRootYCoefficients = [2.0f, 0.0f, 0.0f, 0.0f],
            ProjectedRootYZeroOffset = 0.5f,
        };
        Span<float> muscles = stackalloc float[128];
        muscles[(int)EHumanoidValue.SpineFrontBack] = 0.75f;

        model.TryEvaluateProjectedRootY(muscles, 1.0f, 3.0f, out float projectedY).ShouldBeTrue();

        projectedY.ShouldBe(3.0f, 0.0001f);
    }

    [Test]
    public void ProjectedRootLoopPose_ComposesAndInvertsAcrossForwardAndReverseCycles()
    {
        AnimationClipComponent.CountWrappedCycles(0L, 100L).ShouldBe(0L);
        AnimationClipComponent.CountWrappedCycles(99L, 100L).ShouldBe(0L);
        AnimationClipComponent.CountWrappedCycles(100L, 100L).ShouldBe(1L);
        AnimationClipComponent.CountWrappedCycles(-1L, 100L).ShouldBe(-1L);
        AnimationClipComponent.CountWrappedCycles(-100L, 100L).ShouldBe(-1L);
        AnimationClipComponent.CountWrappedCycles(-101L, 100L).ShouldBe(-2L);

        var loopPose = new HumanoidProjectedRootPose(
            new Vector3(1.0f, 0.25f, 0.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f),
            EHumanoidProjectedRootChannels.PositionXZ |
            EHumanoidProjectedRootChannels.PositionY |
            EHumanoidProjectedRootChannels.RotationYaw);
        HumanoidProjectedRootPose forward = AnimationClipComponent.PowProjectedRootPose(loopPose, 3L);
        HumanoidProjectedRootPose reverse = AnimationClipComponent.PowProjectedRootPose(loopPose, -3L);
        HumanoidProjectedRootPose identity = AnimationClipComponent.ComposeProjectedRootPoses(forward, reverse);

        AssertVectorEquivalent(identity.Position, Vector3.Zero, 0.0002f);
        AssertQuaternionEquivalent(identity.Rotation, Quaternion.Identity, 0.0002f);
        forward.Channels.ShouldBe(loopPose.Channels);
        reverse.Channels.ShouldBe(loopPose.Channels);
    }

    [Test]
    public void QuaternionFloatSlotGroup_UsesShortestArcSlerpAndNormalizesResult()
    {
        var layout = new AnimationSlotLayout();
        AnimSlot x = layout.AllocateSlot(EAnimValueType.Float);
        AnimSlot y = layout.AllocateSlot(EAnimValueType.Float);
        AnimSlot z = layout.AllocateSlot(EAnimValueType.Float);
        AnimSlot w = layout.AllocateSlot(EAnimValueType.Float);
        layout.QuaternionFloatGroups =
        [
            new AnimationQuaternionFloatSlotGroup(x.TypeIndex, y.TypeIndex, z.TypeIndex, w.TypeIndex),
        ];
        AnimationValueStore a = layout.CreateStore();
        AnimationValueStore b = layout.CreateStore();
        AnimationValueStore result = layout.CreateStore();
        Quaternion q170 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(170.0f));
        Quaternion qMinus170 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, DegreesToRadians(-170.0f));
        WriteQuaternion(a, q170);
        WriteQuaternion(b, qMinus170);

        AnimationValueStore.Lerp(a, b, 0.5f, result);

        Quaternion actual = ReadQuaternion(result);
        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        AssertQuaternionEquivalent(actual, expected, 0.0002f);
        actual.Length().ShouldBe(1.0f, 0.0001f);
    }

    [Test]
    public void StateMachine_DetectsCompleteImportedRootQuaternionScalarGroup()
    {
        var layout = new AnimationSlotLayout();
        var slots = new Dictionary<string, AnimSlot>(StringComparer.Ordinal)
        {
            ["Avatar:SetRootRotationX:<AnimatedValue>"] = layout.AllocateSlot(EAnimValueType.Float),
            ["Avatar:SetRootRotationY:<AnimatedValue>"] = layout.AllocateSlot(EAnimValueType.Float),
            ["Avatar:SetRootRotationZ:<AnimatedValue>"] = layout.AllocateSlot(EAnimValueType.Float),
            ["Avatar:SetRootRotationW:<AnimatedValue>"] = layout.AllocateSlot(EAnimValueType.Float),
            ["Avatar:SetRootPositionX:<AnimatedValue>"] = layout.AllocateSlot(EAnimValueType.Float),
        };

        AnimationQuaternionFloatSlotGroup[] groups = AnimStateMachine.BuildQuaternionFloatSlotGroups(slots);

        groups.Length.ShouldBe(1);
        groups[0].ShouldBe(new AnimationQuaternionFloatSlotGroup(0, 1, 2, 3));
    }

    [Test]
    public void AvatarProfile_DenseRoleLookupIsStableAndAllocationFree()
    {
        var profile = new ImportedHumanoidAvatarProfile
        {
            Roles =
            [
                new ImportedHumanoidAvatarRoleProfile
                {
                    Role = EHumanoidAvatarRole.Hips,
                    HumanName = "Hips",
                    TransformName = "AvatarHips",
                    Required = true,
                },
            ],
            NeutralPoseBoneRotations = new Dictionary<string, Quaternion>(StringComparer.Ordinal)
            {
                ["Hips"] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.25f),
            },
            ImportedNeutralBoneLocalPositions = new Dictionary<string, Vector3>(StringComparer.Ordinal)
            {
                ["Hips"] = new Vector3(0.0f, 1.0f, 0.0f),
            },
        };
        profile.BuildDenseLookups();
        profile.TryGetRole(EHumanoidAvatarRole.Hips, out _).ShouldBeTrue();
        profile.TryGetNeutralRotation(EHumanoidAvatarRole.Hips, out _).ShouldBeTrue();
        profile.TryGetNeutralPosition(EHumanoidAvatarRole.Hips, out _).ShouldBeTrue();

        _ = profile.TryGetRole(EHumanoidAvatarRole.Hips, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool lookupsMatched = true;
        for (int i = 0; i < 1_000; i++)
        {
            lookupsMatched &= profile.TryGetRole(EHumanoidAvatarRole.Hips, out _);
            lookupsMatched &= !profile.TryGetRole(EHumanoidAvatarRole.LeftEye, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        lookupsMatched.ShouldBeTrue();
        allocated.ShouldBe(0L);
    }

    private static (HumanoidImportedBodySample Sample, Vector3 Delta, Quaternion Rotation, Vector3 HipsPosition, Quaternion HipsRotation)
        ApplyTransaction(
            HumanoidComponent humanoid,
            Transform hips,
            HumanoidImportedBodySample canonical,
            Vector3 position,
            Quaternion rotation,
            bool reverseOrder)
    {
        object owner = new();
        humanoid.BeginImportedBodySampleTransaction(owner, canonical, hasCanonicalSample: true).ShouldBeTrue();
        Vector3 bindPosition = hips.Translation;
        Quaternion bindRotation = hips.Rotation;

        if (reverseOrder)
        {
            humanoid.SetRootRotationW(rotation.W);
            humanoid.SetRootRotationZ(rotation.Z);
            humanoid.SetRootRotationY(rotation.Y);
            humanoid.SetRootRotationX(rotation.X);
            humanoid.SetRootPositionZ(position.Z);
            humanoid.SetRootPositionY(position.Y);
            humanoid.SetRootPositionX(position.X);
        }
        else
        {
            humanoid.SetRootPositionX(position.X);
            humanoid.SetRootPositionY(position.Y);
            humanoid.SetRootPositionZ(position.Z);
            humanoid.SetRootRotationX(rotation.X);
            humanoid.SetRootRotationY(rotation.Y);
            humanoid.SetRootRotationZ(rotation.Z);
            humanoid.SetRootRotationW(rotation.W);
        }

        hips.Translation.ShouldBe(bindPosition);
        AssertQuaternionEquivalent(hips.Rotation, bindRotation);
        humanoid.CommitImportedBodySampleTransaction(owner).ShouldBeTrue();

        return (
            humanoid.CurrentImportedMappedBodySample,
            humanoid.CurrentConvertedBodyTranslationDelta,
            humanoid.CurrentConvertedBodyRotationDelta,
            hips.Translation,
            hips.Rotation);
    }

    private static HumanoidComponent CreateHumanoid(out Transform hipsTransform)
    {
        var root = new SceneNode("Root", new Transform());
        var hips = new SceneNode(root, "Hips", new Transform(new Vector3(0.0f, 1.0f, 0.0f)));
        _ = new SceneNode(hips, "Spine", new Transform(new Vector3(0.0f, 0.5f, 0.0f)));
        var leftFoot = new SceneNode(hips, "LeftFoot", new Transform(new Vector3(-0.2f, -1.0f, 0.0f)));
        var rightFoot = new SceneNode(hips, "RightFoot", new Transform(new Vector3(0.2f, -1.0f, 0.0f)));
        SaveBindPoseRecursive(root);

        HumanoidComponent humanoid = root.AddComponent<HumanoidComponent>()!;
        humanoid.Hips.Node = hips;
        humanoid.Left.Foot.Node = leftFoot;
        humanoid.Right.Foot.Node = rightFoot;
        hipsTransform = hips.GetTransformAs<Transform>(true)!;
        return humanoid;
    }

    private static HumanoidImportedBodySample CreateBodySample(Vector3 position, Quaternion rotation)
        => new()
        {
            Position = position,
            Rotation = Quaternion.Normalize(rotation),
            Channels = EHumanoidImportedBodySampleChannels.All,
        };

    private static void SaveBindPoseRecursive(SceneNode node)
    {
        node.Transform.SaveBindState();
        foreach (TransformBase child in node.Transform.Children)
            if (child.SceneNode is SceneNode childNode)
                SaveBindPoseRecursive(childNode);
    }

    private static void WriteQuaternion(AnimationValueStore store, Quaternion value)
    {
        store.SetFloat(0, value.X);
        store.SetFloat(1, value.Y);
        store.SetFloat(2, value.Z);
        store.SetFloat(3, value.W);
    }

    private static Quaternion ReadQuaternion(AnimationValueStore store)
        => new(store.GetFloat(0), store.GetFloat(1), store.GetFloat(2), store.GetFloat(3));

    private static float DegreesToRadians(float degrees)
        => degrees * (MathF.PI / 180.0f);

    private static void AssertVectorEquivalent(Vector3 actual, Vector3 expected, float tolerance = 0.0001f)
    {
        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
    }

    private static void AssertQuaternionEquivalent(Quaternion actual, Quaternion expected, float tolerance = 0.0001f)
    {
        actual = Quaternion.Normalize(actual);
        expected = Quaternion.Normalize(expected);
        if (Quaternion.Dot(actual, expected) < 0.0f)
            actual = Quaternion.Negate(actual);

        actual.X.ShouldBe(expected.X, tolerance);
        actual.Y.ShouldBe(expected.Y, tolerance);
        actual.Z.ShouldBe(expected.Z, tolerance);
        actual.W.ShouldBe(expected.W, tolerance);
    }
}
