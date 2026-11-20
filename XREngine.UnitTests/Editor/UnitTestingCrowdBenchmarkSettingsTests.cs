using System.Numerics;
using Newtonsoft.Json;
using NUnit.Framework;
using Shouldly;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class UnitTestingCrowdBenchmarkSettingsTests
{
    [Test]
    public void Placement_PutsEachAvatarOnItsRingFacingTheCenter()
    {
        UnitTestingCrowdBenchmarkSettings crowd = new()
        {
            Enabled = true,
            Center = new UnitTestingWorldSettings.TranslationXYZ { X = 2.0f, Y = 1.0f, Z = -3.0f },
        };
        Vector3 center = new(2.0f, 1.0f, -3.0f);
        float[] radii = UnitTestingCrowdBenchmarkSettings.DefaultRingRadii;
        const int avatarCount = 25;
        int[] perRing = new int[radii.Length];

        for (int index = 0; index < avatarCount; index++)
        {
            crowd.ResolvePlacement(index, avatarCount, out Vector3 position, out float yaw);
            int ring = index % radii.Length;
            perRing[ring]++;
            Vector3.Distance(position, center).ShouldBe(radii[ring], 1.0e-4f);
            position.Y.ShouldBe(center.Y, 1.0e-5f);

            // The avatar forward axis (+Z) rotated by the yaw points at the center.
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            Vector3 toCenter = Vector3.Normalize(center - position);
            Vector3.Dot(forward, toCenter).ShouldBe(1.0f, 1.0e-4f);
        }

        perRing.Max().ShouldBeLessThanOrEqualTo(perRing.Min() + 1);
    }

    [Test]
    public void Placement_SpreadsTheAvatarsOfOneRing()
    {
        UnitTestingCrowdBenchmarkSettings crowd = new() { RingRadii = [10.0f] };
        const int avatarCount = 4;
        Vector3[] positions = new Vector3[avatarCount];
        for (int index = 0; index < avatarCount; index++)
            crowd.ResolvePlacement(index, avatarCount, out positions[index], out _);

        for (int first = 0; first < avatarCount; first++)
            for (int second = first + 1; second < avatarCount; second++)
                Vector3.Distance(positions[first], positions[second]).ShouldBeGreaterThan(10.0f);
    }

    [Test]
    public void RenderMode_ReplacesTheVirtualGeometrySettingsOnlyWhenEnabled()
    {
        UnitTestingCrowdBenchmarkSettings crowd = new() { Mode = UnitTestCrowdBenchmarkMode.DeformationLod };
        crowd.TryResolveRenderMode(out _, out _, out _).ShouldBeFalse();

        crowd.Enabled = true;
        crowd.TryResolveRenderMode(out bool cut, out bool skinnedCut, out bool deformationLod).ShouldBeTrue();
        (cut, skinnedCut, deformationLod).ShouldBe((true, true, true));

        crowd.Mode = UnitTestCrowdBenchmarkMode.ClusterCut;
        crowd.TryResolveRenderMode(out cut, out skinnedCut, out deformationLod).ShouldBeTrue();
        (cut, skinnedCut, deformationLod).ShouldBe((true, true, false));

        crowd.Mode = UnitTestCrowdBenchmarkMode.Triangles;
        crowd.TryResolveRenderMode(out cut, out skinnedCut, out deformationLod).ShouldBeTrue();
        (cut, skinnedCut, deformationLod).ShouldBe((false, false, false));

        crowd.Mode = UnitTestCrowdBenchmarkMode.UseRenderSettings;
        crowd.TryResolveRenderMode(out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Settings_RoundTripThroughJson()
    {
        UnitTestingWorldSettings settings = new();
        settings.CrowdBenchmark.Enabled = true;
        settings.CrowdBenchmark.AvatarCount = 100;
        settings.CrowdBenchmark.RingRadii = [2.0f, 4.0f];
        settings.CrowdBenchmark.Mode = UnitTestCrowdBenchmarkMode.ClusterCut;

        // The editor copies the settings with preserved references, which an array setting cannot survive.
        JsonSerializerSettings editorJson = new() { PreserveReferencesHandling = PreserveReferencesHandling.All };
        UnitTestingWorldSettings copy = JsonConvert.DeserializeObject<UnitTestingWorldSettings>(
            JsonConvert.SerializeObject(settings, editorJson), editorJson)!;

        copy.CrowdBenchmark.Enabled.ShouldBeTrue();
        copy.CrowdBenchmark.AvatarCount.ShouldBe(100);
        copy.CrowdBenchmark.RingRadii.ShouldBe([2.0f, 4.0f]);
        copy.CrowdBenchmark.Mode.ShouldBe(UnitTestCrowdBenchmarkMode.ClusterCut);
    }
}
