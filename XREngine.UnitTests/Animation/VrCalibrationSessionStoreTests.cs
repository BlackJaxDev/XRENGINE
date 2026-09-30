using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Input;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class VrCalibrationSessionStoreTests
{
    private static readonly VrCalibrationSessionScope Scope = new("local-player", "avatar-asset", RuntimeVrRuntimeKind.OpenXR, 3, 4);
    private static readonly VrBodyMeasurementKey Measurement = new(EVrBodyMeasurementMode.Height, 1.8f, 0.936f);

    [Test]
    public void Restore_RebindsSamePhysicalDeviceToNewRigWithoutRetainingOldNodes()
    {
        var store = new VrCalibrationSessionStore();
        Matrix4x4 offset = Matrix4x4.CreateRotationY(0.4f) * Matrix4x4.CreateTranslation(0.1f, 0.2f, 0.3f);
        VrStoredCalibrationSlot[] stored = [new(EHumanoidIKTarget.Hips, "physical-a", offset)];
        store.TrySave(Scope, Measurement, stored, out _).ShouldBeTrue();
        stored[0] = new(EHumanoidIKTarget.Chest, "different", Matrix4x4.Identity);
        var node = new SceneNode("New rig source", new Transform());
        try
        {
            VrCalibrationTarget[] restored = new VrCalibrationTarget[11];
            store.TryRestore(Scope, Measurement, [new("physical-a", node.Transform, true)], restored,
                out int written, out int missing, out string notice).ShouldBeTrue();
            written.ShouldBe(1); missing.ShouldBe(0); notice.ShouldBeEmpty();
            restored[0].Slot.ShouldBe(EHumanoidIKTarget.Hips);
            restored[0].Source.ShouldBeSameAs(node.Transform);
            restored[0].Offset.ShouldBe(offset);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void Restore_MissingTrackerLeavesSlotEmptyAndDifferentIdentityCannotInheritIt()
    {
        var store = new VrCalibrationSessionStore();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-a", Matrix4x4.Identity),
            new(EHumanoidIKTarget.LeftFoot, "physical-b", Matrix4x4.Identity)], out _).ShouldBeTrue();
        var node = new SceneNode("Current source", new Transform());
        var replacement = new SceneNode("Replacement device", new Transform());
        try
        {
            VrCalibrationTarget[] restored = new VrCalibrationTarget[11];
            store.TryRestore(Scope, Measurement, [new("physical-a", node.Transform, true), new("replacement", replacement.Transform, true)],
                restored, out int written, out int missing, out string notice).ShouldBeTrue();
            written.ShouldBe(1); missing.ShouldBe(1); notice.ShouldNotBeNullOrEmpty();
            restored[0].Slot.ShouldBe(EHumanoidIKTarget.Hips);
        }
        finally { node.Destroy(true); replacement.Destroy(true); }
    }

    [Test]
    public void Restore_RejectsAvatarMeasurementProviderGenerationAndBasisChanges()
    {
        var store = new VrCalibrationSessionStore();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-a", Matrix4x4.Identity)], out _).ShouldBeTrue();
        VrCalibrationTarget[] restored = new VrCalibrationTarget[11];
        foreach (VrCalibrationSessionScope changed in new[] { Scope with { AvatarIdentity = "other-avatar" },
            Scope with { PlayerIdentity = "other-player" }, Scope with { Provider = RuntimeVrRuntimeKind.OpenVR },
            Scope with { ProviderGeneration = 4 }, Scope with { ReferenceSpaceVersion = 5 } })
        {
            store.TryRestore(changed, Measurement, [], restored, out int written, out _, out string notice).ShouldBeFalse();
            written.ShouldBe(0); notice.ShouldNotBeNullOrEmpty();
        }
        store.TryRestore(Scope, Measurement with { ValueMeters = 1.7f }, [], restored, out _, out _, out _).ShouldBeFalse();
        store.Remove(Scope.PlayerIdentity);
        store.TryRestore(Scope, Measurement, [], restored, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Save_InvalidReplacementDoesNotOverwriteCommittedState()
    {
        var store = new VrCalibrationSessionStore();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-a", Matrix4x4.Identity)], out _).ShouldBeTrue();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-a", Matrix4x4.Identity),
            new(EHumanoidIKTarget.Chest, "physical-a", Matrix4x4.Identity)], out _).ShouldBeFalse();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-b", default)], out _).ShouldBeFalse();
        VrCalibrationTarget[] restored = new VrCalibrationTarget[11];
        store.TryRestore(Scope, Measurement, [], restored, out int written, out int missing, out _).ShouldBeTrue();
        written.ShouldBe(0); missing.ShouldBe(1);
        new VrCalibrationSessionStore().TryRestore(Scope, Measurement, [], restored, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Restore_RejectsDuplicateCurrentIdentitiesAndSkipsDestroyedSources()
    {
        var store = new VrCalibrationSessionStore();
        store.TrySave(Scope, Measurement, [new(EHumanoidIKTarget.Hips, "physical-a", Matrix4x4.Identity)], out _).ShouldBeTrue();
        var node = new SceneNode("Source", new Transform());
        var restored = new VrCalibrationTarget[11];
        store.TryRestore(Scope, Measurement, [new("physical-a", node.Transform, true), new("physical-a", node.Transform, true)],
            restored, out int written, out _, out _).ShouldBeFalse();
        written.ShouldBe(0);
        node.Destroy(true);
        store.TryRestore(Scope, Measurement, [new("physical-a", node.Transform, true)], restored,
            out written, out int missing, out _).ShouldBeTrue();
        written.ShouldBe(0); missing.ShouldBe(1);
    }

    [Test]
    public void MeasurementKey_UsesOnlyTheActiveModeAndRejectsUnset()
    {
        var settings = new UserSettings();
        VrBodyMeasurementKey.TryFromSettings(settings, out _).ShouldBeFalse();
        settings.BodyMeasurementMode = EVrBodyMeasurementMode.ArmSpan;
        settings.PlayerArmSpan = 1.95f;
        VrBodyMeasurementKey.TryFromSettings(settings, out VrBodyMeasurementKey first).ShouldBeTrue();
        settings.PlayerHeight = 1.75f;
        settings.StandingHeightToEyeHeightRatio = 0.9f;
        VrBodyMeasurementKey.TryFromSettings(settings, out VrBodyMeasurementKey second).ShouldBeTrue();
        second.ShouldBe(first);
    }
}
