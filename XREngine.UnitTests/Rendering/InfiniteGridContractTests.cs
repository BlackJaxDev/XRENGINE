using System;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Environment;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class InfiniteGridContractTests
{
    [Test]
    public void InfiniteGrid_VertexShader_ContainsUnprojectionAndDepthRangeHandling()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGrid.vs"));

        source.ShouldContain("uniform mat4 InverseViewMatrix;");
        source.ShouldContain("uniform mat4 InverseProjMatrix;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("uniform int ClipDepthRange;");
        source.ShouldContain("vec3 Unproject(vec2 clipXY, float clipZ, mat4 invView, mat4 invProj)");
        source.ShouldContain("NearWorldPos = Unproject(clipXY, GetNearClipZ(), InverseViewMatrix, InverseProjMatrix);");
        source.ShouldContain("FarWorldPos = Unproject(clipXY, GetFarClipZ(), InverseViewMatrix, InverseProjMatrix);");
    }

    [Test]
    public void InfiniteGridStereo_VertexShader_UsesPerEyeMatrices()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridStereo.vs"));

        source.ShouldContain("#extension GL_OVR_multiview2 : require");
        source.ShouldContain("layout(num_views = 2) in;");
        source.ShouldContain("uniform mat4 LeftEyeInverseViewMatrix;");
        source.ShouldContain("uniform mat4 RightEyeInverseViewMatrix;");
        source.ShouldContain("uniform mat4 LeftEyeInverseProjMatrix;");
        source.ShouldContain("uniform mat4 RightEyeInverseProjMatrix;");
        source.ShouldContain("gl_ViewID_OVR == 0 ? LeftEyeInverseViewMatrix : RightEyeInverseViewMatrix");
    }

    [Test]
    public void InfiniteGrid_FragmentShader_ContainsMultiScaleLODAndDepthWrite()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGrid.fs"));

        source.ShouldContain("uniform mat4 ViewProjectionMatrix;");
        source.ShouldContain("uniform vec3 CameraPosition;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("uniform int ClipDepthRange;");
        source.ShouldContain("IntersectGridPlane");
        source.ShouldContain("gl_FragDepth = DepthMode == 1 ? (1.0 - depth) : depth;");
        source.ShouldContain("fwidth(coord)");
        source.ShouldContain("float scale0 = baseCell * pow(lodScale, lodFloor);");
        source.ShouldContain("float scale1 = scale0 * lodScale;");
        source.ShouldContain("PristineGrid(coord, dxy, scale0, GridLineWidth);");
        source.ShouldContain("GridXAxisColor");
        source.ShouldContain("GridZAxisColor");
        source.ShouldContain("distanceFade");
        source.ShouldContain("SmootherStep");
        source.ShouldContain("OutColor = vec4(gridRgb, finalAlpha);");
    }

    [Test]
    public void InfiniteGridStereo_FragmentShader_UsesPerEyeMatrices()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridStereo.fs"));

        source.ShouldContain("#extension GL_OVR_multiview2 : require");
        source.ShouldContain("uniform mat4 LeftEyeViewProjectionMatrix;");
        source.ShouldContain("uniform mat4 RightEyeViewProjectionMatrix;");
        source.ShouldContain("gl_ViewID_OVR == 0 ? LeftEyeViewProjectionMatrix : RightEyeViewProjectionMatrix");
    }

    [Test]
    public void InfiniteGrid_MotionVectors_FragmentShader_ContainsAnalyticalMotionVectors()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridMotionVectors.fs"));

        source.ShouldContain("layout(location = 0) out vec2 OutVelocity;");
        source.ShouldContain("uniform mat4 CurrViewProjection;");
        source.ShouldContain("uniform mat4 PrevViewProjection;");
        source.ShouldContain("IntersectGridPlane");
        source.ShouldContain("OutVelocity = clamp(currNdc - prevNdc, vec2(-2.0), vec2(2.0));");
    }

    [Test]
    public void InfiniteGrid_ReactiveMask_FragmentShader_OutputsCoverageAlpha()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridReactiveMask.fs"));

        source.ShouldContain("layout(location = 0) out float OutReactiveMask;");
        source.ShouldContain("IntersectGridPlane");
        source.ShouldContain("OutReactiveMask = finalAlpha;");
    }

    [Test]
    public void InfiniteGridFloorComponent_FallbackSources_MatchShaderFiles()
    {
        string vsFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGrid.vs")));
        string vsFallback = NormalizeNewlines(InfiniteGridFloorComponent.VertexShaderSource);
        vsFile.Trim().ShouldBe(vsFallback.Trim());

        string stereoVsFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridStereo.vs")));
        string stereoVsFallback = NormalizeNewlines(InfiniteGridFloorComponent.StereoVertexShaderSource);
        stereoVsFile.Trim().ShouldBe(stereoVsFallback.Trim());

        string fsFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGrid.fs")));
        string fsFallback = NormalizeNewlines(InfiniteGridFloorComponent.FragmentShaderSource);
        fsFile.Trim().ShouldBe(fsFallback.Trim());

        string mvFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridMotionVectors.fs")));
        string mvFallback = NormalizeNewlines(InfiniteGridFloorComponent.MotionVectorsFragmentShaderSource);
        mvFile.Trim().ShouldBe(mvFallback.Trim());

        string mvStereoFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridMotionVectorsStereo.fs")));
        string mvStereoFallback = NormalizeNewlines(InfiniteGridFloorComponent.MotionVectorsStereoFragmentShaderSource);
        mvStereoFile.Trim().ShouldBe(mvStereoFallback.Trim());

        string rmFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridReactiveMask.fs")));
        string rmFallback = NormalizeNewlines(InfiniteGridFloorComponent.ReactiveMaskFragmentShaderSource);
        rmFile.Trim().ShouldBe(rmFallback.Trim());

        string rmStereoFile = NormalizeNewlines(LoadShaderSource(Path.Combine("Scene3D", "InfiniteGridReactiveMaskStereo.fs")));
        string rmStereoFallback = NormalizeNewlines(InfiniteGridFloorComponent.ReactiveMaskStereoFragmentShaderSource);
        rmStereoFile.Trim().ShouldBe(rmStereoFallback.Trim());
    }

    [Test]
    public void InfiniteGridFloorComponent_LifecycleAndProperties()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;

        comp.Enabled.ShouldBeTrue();
        comp.CellSize.ShouldBe(1.0f);
        comp.MajorGridInterval.ShouldBe(10.0f);
        comp.LineWidth.ShouldBe(1.0f);
        comp.MaxDistance.ShouldBe(500.0f);
        comp.FadeRange.ShouldBe(150.0f);
        comp.ShowAxes.ShouldBeTrue();
        comp.AffectedByMotionBlur.ShouldBeTrue();

        // Test property updates
        comp.CellSize = 5.0f;
        comp.CellSize.ShouldBe(5.0f);

        comp.MajorGridInterval = 5.0f;
        comp.MajorGridInterval.ShouldBe(5.0f);

        comp.MaxDistance = 1000.0f;
        comp.MaxDistance.ShouldBe(1000.0f);

        comp.MinorLineColor = new ColorF4(0.1f, 0.2f, 0.3f, 0.4f);
        comp.MinorLineColor.ShouldBe(new ColorF4(0.1f, 0.2f, 0.3f, 0.4f));

        comp.ShowAxes = false;
        comp.ShowAxes.ShouldBeFalse();

        comp.AffectedByMotionBlur = false;
        comp.AffectedByMotionBlur.ShouldBeFalse();
        comp.AffectedByMotionBlur = true;
        comp.AffectedByMotionBlur.ShouldBeTrue();
    }

    [Test]
    public void InfiniteGridFloorComponent_ResolveEffectiveRenderPass_HonorsPrecedence()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;

        // 1. If affected by motion blur, must resolve to TransparentForward regardless of DoF / Bloom
        comp.AffectedByMotionBlur = true;
        comp.AffectedByDepthOfField = true;
        comp.AffectedByBloom = true;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.TransparentForward);

        comp.AffectedByDepthOfField = false;
        comp.AffectedByBloom = false;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.TransparentForward);

        // 2. If not affected by motion blur, but affected by DoF -> PostMotionBlurForward
        comp.AffectedByMotionBlur = false;
        comp.AffectedByDepthOfField = true;
        comp.AffectedByBloom = true;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.PostMotionBlurForward);

        comp.AffectedByBloom = false;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.PostMotionBlurForward);

        // 3. If not affected by motion blur or DoF, but affected by Bloom -> PostDepthOfFieldForward
        comp.AffectedByMotionBlur = false;
        comp.AffectedByDepthOfField = false;
        comp.AffectedByBloom = true;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.PostDepthOfFieldForward);

        // 4. If not affected by motion blur, DoF, or Bloom -> PostBloomForward
        comp.AffectedByBloom = false;
        comp.ResolveEffectiveRenderPass().ShouldBe((int)EDefaultRenderPass.PostBloomForward);
    }

    [Test]
    public void XRCamera_TracksUnjitteredViewProjectionAcrossFramesAndHandlesCuts()
    {
        var camera = new XRCamera();

        // Frame 1: initial view-projection history is not ready
        camera.HasPreviousViewProjectionMatrix.ShouldBeFalse();
        Matrix4x4 firstVp = camera.ViewProjectionMatrixUnjittered;
        camera.PreviousViewProjectionMatrixUnjittered.ShouldBe(firstVp);

        // Frame 2: camera moves without a cut -> history is available
        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(10f, 5f, 20f)).GetAwaiter().GetResult();
        Matrix4x4 secondVp = camera.ViewProjectionMatrixUnjittered;
        secondVp.ShouldNotBe(firstVp);

        camera.HasPreviousViewProjectionMatrix.ShouldBeTrue();
        camera.PreviousViewProjectionMatrixUnjittered.ShouldBe(firstVp);

        // Frame 3: camera moves again -> history updates to second frame
        camera.Transform.SetRenderMatrix(Matrix4x4.CreateTranslation(20f, 5f, 40f)).GetAwaiter().GetResult();
        Matrix4x4 thirdVp = camera.ViewProjectionMatrixUnjittered;
        thirdVp.ShouldNotBe(secondVp);

        camera.HasPreviousViewProjectionMatrix.ShouldBeTrue();
        camera.PreviousViewProjectionMatrixUnjittered.ShouldBe(secondVp);

        // Instantaneous cut / epoch invalidation resets history
        camera.InvalidateTemporalHistory();
        camera.HasPreviousViewProjectionMatrix.ShouldBeFalse();
        camera.PreviousViewProjectionMatrixUnjittered.ShouldBe(thirdVp);
    }

    [Test]
    public void UnitTestingWorldSettings_GridFloor_Roundtrips()
    {
        var settings = new UnitTestingWorldSettings { GridFloor = true };
        settings.GridFloor.ShouldBeTrue();

        string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
        UnitTestingWorldSettings deserialized = UnitTestingWorldSettingsStore.ParseJsonc(json);
        deserialized.GridFloor.ShouldBeTrue();
    }

    [Test]
    public void InfiniteGridFloorComponent_RayPlaneIntersections_XZPlane()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = true;
        comp.ShowXYPlane = false;
        comp.ShowYZPlane = false;
        comp.GridHeight = 0.0f;
        comp.UseTransformY = false;

        // Ray pointing straight down to ground
        Vector3 origin = new(0f, 10f, 0f);
        Vector3 dir = new(0f, -1f, 0f);
        bool hit = comp.TryIntersectRay(origin, dir, out Vector3 worldHit, out float dist, out int hitPlane);

        hit.ShouldBeTrue();
        hitPlane.ShouldBe(InfiniteGridFloorComponent.XZPlane);
        dist.ShouldBe(10.0f, 1e-4f);
        worldHit.X.ShouldBe(0.0f, 1e-4f);
        worldHit.Y.ShouldBe(0.0f, 1e-4f);
        worldHit.Z.ShouldBe(0.0f, 1e-4f);

        // Test with custom height
        comp.GridHeight = 3.5f;
        hit = comp.TryIntersectRay(origin, dir, out worldHit, out dist, out hitPlane);
        hit.ShouldBeTrue();
        dist.ShouldBe(6.5f, 1e-4f);
        worldHit.Y.ShouldBe(3.5f, 1e-4f);

        // Ray pointing upwards should miss
        hit = comp.TryIntersectRay(origin, new Vector3(0f, 1f, 0f), out _, out _, out _);
        hit.ShouldBeFalse();
    }

    [Test]
    public void InfiniteGridFloorComponent_RayPlaneIntersections_XYPlane()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = false;
        comp.ShowXYPlane = true;
        comp.ShowYZPlane = false;

        // Ray pointing forward along -Z into XY plane at Z=0
        Vector3 origin = new(5f, 2f, 10f);
        Vector3 dir = new(0f, 0f, -1f);
        bool hit = comp.TryIntersectRay(origin, dir, out Vector3 worldHit, out float dist, out int hitPlane);

        hit.ShouldBeTrue();
        hitPlane.ShouldBe(InfiniteGridFloorComponent.XYPlane);
        dist.ShouldBe(10.0f, 1e-4f);
        worldHit.X.ShouldBe(5.0f, 1e-4f);
        worldHit.Y.ShouldBe(2.0f, 1e-4f);
        worldHit.Z.ShouldBe(0.0f, 1e-4f);

        // Ray pointing away from plane (+Z) should miss
        hit = comp.TryIntersectRay(origin, new Vector3(0f, 0f, 1f), out _, out _, out _);
        hit.ShouldBeFalse();
    }

    [Test]
    public void InfiniteGridFloorComponent_RayPlaneIntersections_YZPlane()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = false;
        comp.ShowXYPlane = false;
        comp.ShowYZPlane = true;

        // Ray pointing left along -X into YZ plane at X=0
        Vector3 origin = new(10f, 4f, -3f);
        Vector3 dir = new(-1f, 0f, 0f);
        bool hit = comp.TryIntersectRay(origin, dir, out Vector3 worldHit, out float dist, out int hitPlane);

        hit.ShouldBeTrue();
        hitPlane.ShouldBe(InfiniteGridFloorComponent.YZPlane);
        dist.ShouldBe(10.0f, 1e-4f);
        worldHit.X.ShouldBe(0.0f, 1e-4f);
        worldHit.Y.ShouldBe(4.0f, 1e-4f);
        worldHit.Z.ShouldBe(-3.0f, 1e-4f);

        // Ray pointing away from plane (+X) should miss
        hit = comp.TryIntersectRay(origin, new Vector3(1f, 0f, 0f), out _, out _, out _);
        hit.ShouldBeFalse();
    }

    [Test]
    public void InfiniteGridFloorComponent_ClosestPlaneSelection_WhenMultiplePlanesHit()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = true;
        comp.ShowXYPlane = true;
        comp.ShowYZPlane = false;
        comp.GridHeight = 0.0f;
        comp.UseTransformY = false;

        // Ray from (0, 4, 10) directed towards (0, 0, -2)
        // Hits XY plane (Z=0) at distance ~10.54
        // Hits XZ plane (Y=0) at distance ~12.65
        // XY plane is closer (smaller t), so XY plane should be selected!
        Vector3 origin = new(0f, 4f, 10f);
        Vector3 target = new(0f, 0f, -2f);
        Vector3 dir = Vector3.Normalize(target - origin);

        bool hit = comp.TryIntersectRay(origin, dir, out Vector3 worldHit, out float dist, out int hitPlane);
        hit.ShouldBeTrue();
        hitPlane.ShouldBe(InfiniteGridFloorComponent.XYPlane);
        worldHit.Z.ShouldBe(0.0f, 1e-3f);

        // Now change origin so XZ plane is closer:
        // Ray from (0, 1, 10) directed towards (0, -5, 0)
        // Hits XZ (Y=0) before XY (Z=0)
        origin = new(0f, 1f, 10f);
        target = new(0f, -5f, 0f);
        dir = Vector3.Normalize(target - origin);

        hit = comp.TryIntersectRay(origin, dir, out worldHit, out dist, out hitPlane);
        hit.ShouldBeTrue();
        hitPlane.ShouldBe(InfiniteGridFloorComponent.XZPlane);
        worldHit.Y.ShouldBe(0.0f, 1e-3f);
    }

    [Test]
    public void InfiniteGridFloorComponent_PlaneTogglesAndMask()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;

        // Default: XZ enabled
        comp.ShowXZPlane.ShouldBeTrue();
        comp.ShowXYPlane.ShouldBeFalse();
        comp.ShowYZPlane.ShouldBeFalse();
        comp.EnabledPlaneMask.ShouldBe(1 << InfiniteGridFloorComponent.XZPlane);

        // Enable all 3
        comp.ShowXYPlane = true;
        comp.ShowYZPlane = true;
        comp.EnabledPlaneMask.ShouldBe(
            (1 << InfiniteGridFloorComponent.XZPlane) |
            (1 << InfiniteGridFloorComponent.XYPlane) |
            (1 << InfiniteGridFloorComponent.YZPlane));

        // Disable depth hits
        comp.EnableDepthHits = false;
        bool hit = comp.TryIntersectRay(new Vector3(0, 10, 0), new Vector3(0, -1, 0), out _, out _, out _);
        hit.ShouldBeFalse();

        // Re-enable depth hits, disable component
        comp.EnableDepthHits = true;
        comp.Enabled = false;
        hit = comp.TryIntersectRay(new Vector3(0, 10, 0), new Vector3(0, -1, 0), out _, out _, out _);
        hit.ShouldBeFalse();
    }

    [Test]
    public void InfiniteGridFloorComponent_MaxDistanceAndAltitudeCulling()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = true;
        comp.ShowXYPlane = false;
        comp.ShowYZPlane = false;
        comp.GridHeight = 0.0f;
        comp.UseTransformY = false;
        comp.MaxDistance = 50.0f;
        comp.AltitudeDistanceScale = 0.0f; // test max distance alone

        // Ray origin at (0, 1, 0), looking at shallow angle hitting ground far away (X = 200)
        Vector3 origin = new(0f, 1f, 0f);
        Vector3 hitPoint = new(200f, 0f, 0f);
        Vector3 dir = Vector3.Normalize(hitPoint - origin);

        bool hit = comp.TryIntersectRay(origin, dir, out _, out _, out _, checkMaxDistance: true);
        hit.ShouldBeFalse("Hit beyond max distance should be culled");

        // Now increase MaxDistance to 500
        comp.MaxDistance = 500.0f;
        hit = comp.TryIntersectRay(origin, dir, out Vector3 hitPos, out float dist, out int plane, checkMaxDistance: true);
        hit.ShouldBeTrue("Hit within increased max distance should succeed");
        hitPos.X.ShouldBe(200.0f, 1e-2f);
    }

    [Test]
    public void XRCamera_WorldToNormalizedViewportCoordinate_RoundTrips()
    {
        var camera = new XRCamera();
        var tfm = (Transform)camera.Transform;
        tfm.Translation = new Vector3(0, 10, 20);
        tfm.LookAt(Vector3.Zero);
        tfm.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
        Vector3 worldPoint = new Vector3(0, 0, 0);

        Vector3 clip01 = camera.WorldToNormalizedViewportCoordinate(worldPoint);
        TestContext.WriteLine($"clip01: {clip01}");

        clip01.Z.ShouldBeInRange(0.0f, 1.0f);
        clip01.X.ShouldBeInRange(0.0f, 1.0f);
        clip01.Y.ShouldBeInRange(0.0f, 1.0f);

        Vector3 reconstructed = camera.NormalizedViewportToWorldCoordinate(new Vector2(clip01.X, clip01.Y), clip01.Z);
        TestContext.WriteLine($"reconstructed: {reconstructed}");

        reconstructed.X.ShouldBe(worldPoint.X, 1e-3f);
        reconstructed.Y.ShouldBe(worldPoint.Y, 1e-3f);
        reconstructed.Z.ShouldBe(worldPoint.Z, 1e-3f);
    }

    [Test]
    public void InfiniteGridFloorComponent_TryGetDepthHit_ReturnsValidNormalizedDepth()
    {
        var node = new SceneNode("GridTestNode");
        var comp = node.AddComponent<InfiniteGridFloorComponent>()!;
        comp.ShowXZPlane = true;
        comp.ShowXYPlane = false;
        comp.ShowYZPlane = false;
        comp.GridHeight = 0.0f;
        comp.UseTransformY = false;
        comp.NotifyComponentActivated();

        try
        {
            var camera = new XRCamera();
            var tfm = (Transform)camera.Transform;
            tfm.Translation = new Vector3(0, 10, 20);
            tfm.LookAt(Vector3.Zero);
            tfm.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

            var viewport = new XRViewport(null, 1920, 1080);
            viewport.Camera = camera;

            // Cursor at viewport center (0.5, 0.5) looking at (0, 0, 0) on ground
            bool hit = comp.TryGetDepthHit(viewport, new Vector2(0.5f, 0.5f), out Vector3 hitPoint, out float depth, out int plane);
            hit.ShouldBeTrue();
            plane.ShouldBe(InfiniteGridFloorComponent.XZPlane);
            depth.ShouldBeInRange(0.0f, 1.0f);
            hitPoint.X.ShouldBe(0.0f, 1e-2f);
            hitPoint.Y.ShouldBe(0.0f, 1e-2f);
            hitPoint.Z.ShouldBe(0.0f, 1e-2f);

            // Active grid detection test
            bool activeHit = InfiniteGridFloorComponent.TryGetActiveGridDepthHit(
                viewport,
                new Vector2(0.5f, 0.5f),
                out Vector3 activeHitPoint,
                out float activeDepth,
                out var hitGrid,
                out int activePlane);

            activeHit.ShouldBeTrue();
            hitGrid.ShouldNotBeNull();
            activePlane.ShouldBe(InfiniteGridFloorComponent.XZPlane);
            activeDepth.ShouldBeInRange(0.0f, 1.0f);
            activeHitPoint.X.ShouldBe(0.0f, 1e-2f);
            activeHitPoint.Y.ShouldBe(0.0f, 1e-2f);
            activeHitPoint.Z.ShouldBe(0.0f, 1e-2f);
        }
        finally
        {
            comp.Destroy();
        }
    }

    private static string LoadShaderSource(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, "Build", "CommonAssets", "Shaders", relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected shader file '{path}' to exist.");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ResolveRepoRoot()
    {
        string? dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "XRENGINE.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        Assert.Fail("Could not find repo root (XRENGINE.slnx).");
        return string.Empty;
    }
}
