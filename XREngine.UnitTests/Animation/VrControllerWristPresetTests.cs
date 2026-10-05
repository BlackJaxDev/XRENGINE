using System.Numerics;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Input;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class VrControllerWristPresetTests
{
    [TestCase("/interaction_profiles/valve/index_controller")]
    [TestCase("/interaction_profiles/htc/vive_controller")]
    [TestCase("/interaction_profiles/khr/simple_controller")]
    [TestCase("/interaction_profiles/oculus/touch_controller")]
    [TestCase("/interaction_profiles/microsoft/motion_controller")]
    public void EverySupportedProfile_HasANonzeroExplicitlyUnqualifiedGeometricDefault(string profile)
    {
        VrControllerWristPresets.IsSupportedProfile(profile).ShouldBeTrue();
        Vector3 offset = VrControllerWristPresets.Resolve(profile, new UserSettings());
        offset.ShouldBe(VrControllerWristPresets.GeometricDefault);
        offset.Length().ShouldBeGreaterThan(0);
        VrControllerWristPresets.IsValid(offset).ShouldBeTrue();
    }

    [Test]
    public void ProfileOverride_RoundTripsWithoutChangingAnotherProfileOrAcceptingInvalidValues()
    {
        const string profile = "/interaction_profiles/valve/index_controller";
        var settings = new UserSettings();
        settings.VrControllerWristOffsets[profile] = new(0.01f, -0.025f, 0.045f);
        var restored = MemoryPackSerializer.Deserialize<UserSettings>(MemoryPackSerializer.Serialize(settings))!;
        VrControllerWristPresets.Resolve(profile, restored).ShouldBe(settings.VrControllerWristOffsets[profile]);
        VrControllerWristPresets.Resolve("/interaction_profiles/htc/vive_controller", restored).ShouldBe(VrControllerWristPresets.GeometricDefault);
        restored.VrControllerWristOffsets[profile] = new(float.NaN, 0, 0);
        VrControllerWristPresets.Resolve(profile, restored).ShouldBe(VrControllerWristPresets.GeometricDefault);
    }
}
