using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering.Shadows;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LocalShadowFrustumRelevanceTests
{
    [Test]
    public void FrustumRelevance_DisjointFrustaReturnFalse()
    {
        PreparedFrustum shadow = CreatePreparedFrustum(Vector3.Zero);
        PreparedFrustum camera = CreatePreparedFrustum(new Vector3(100.0f, 0.0f, 0.0f));
        ShadowRelevanceCameraSet cameras = new([camera]);
        List<Vector3> scratch = new(64);

        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(shadow, cameras, scratch)
            .ShouldBeFalse();
    }

    [Test]
    public void FrustumRelevance_TouchingFrustaReturnTrue()
    {
        PreparedFrustum shadow = CreatePreparedFrustum(Vector3.Zero, farZ: 10.0f);
        PreparedFrustum camera = CreatePreparedFrustum(new Vector3(0.0f, 0.0f, -9.9f), nearZ: 0.1f, farZ: 2.0f);
        ShadowRelevanceCameraSet cameras = new([camera]);
        List<Vector3> scratch = new(64);

        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(shadow, cameras, scratch)
            .ShouldBeTrue();
    }

    [Test]
    public void FrustumRelevance_ContainedFrustumReturnsTrue()
    {
        PreparedFrustum shadow = CreatePreparedFrustum(new Vector3(0.0f, 0.0f, -2.0f), fovY: 20.0f, farZ: 2.0f);
        PreparedFrustum camera = CreatePreparedFrustum(Vector3.Zero, fovY: 70.0f, farZ: 20.0f);
        ShadowRelevanceCameraSet cameras = new([camera]);
        List<Vector3> scratch = new(64);

        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(shadow, cameras, scratch)
            .ShouldBeTrue();
    }

    [Test]
    public void FrustumRelevance_InvalidOrEmptyStateReturnsConservativeTrue()
    {
        PreparedFrustum camera = CreatePreparedFrustum(Vector3.Zero);
        List<Vector3> scratch = new(64);

        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(null, new ShadowRelevanceCameraSet([camera]), scratch)
            .ShouldBeTrue();
        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(camera, new ShadowRelevanceCameraSet([]), scratch)
            .ShouldBeTrue();
        LocalShadowFrustumRelevance.AnyCameraIntersectsShadowFrustum(camera, new ShadowRelevanceCameraSet([camera]), null!)
            .ShouldBeTrue();
    }

    [Test]
    public void LocalShadowSourceContracts_IncludeAtlasAndLegacyRelevanceGates()
    {
        string shadowTypes = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Shadows", "ShadowAtlasTypes.cs"));
        shadowTypes.ShouldContain("NotRelevant");
        shadowTypes.ShouldContain("ForcedSkipReason");

        string lights = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Lights3DCollection.Shadows.cs"));
        lights.ShouldContain("CalculatePointShadowFaceMask");
        lights.ShouldContain("SkipReason.NotRelevant");
        lights.ShouldContain("IsSpotShadowRelevant");

        string point = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "PointLightComponent.cs"));
        point.ShouldContain("CurrentShadowFaceRelevanceMask");
        point.ShouldContain("RenderSequentialShadowFaces(faceMask)");
        point.ShouldContain("faceIndices[..faceCount]");

        string spot = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "SpotLightComponent.cs"));
        spot.ShouldContain("PrimaryShadowViewportRelevant");
        spot.ShouldContain("GenerateMomentShadowMipmapsIfNeeded");
    }

    private static PreparedFrustum CreatePreparedFrustum(
        Vector3 position,
        float fovY = 60.0f,
        float nearZ = 0.1f,
        float farZ = 10.0f)
        => new Frustum(
            fovY,
            aspect: 1.0f,
            nearZ,
            farZ,
            forward: -Vector3.UnitZ,
            up: Vector3.UnitY,
            position).Prepare();

    private static string LoadRepoSource(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        Assert.Inconclusive($"Repository source file not found: {relativePath}");
        return string.Empty;
    }
}
