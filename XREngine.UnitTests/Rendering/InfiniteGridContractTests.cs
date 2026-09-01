using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Environment;
using XREngine.Data.Colors;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene;

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
        source.ShouldContain("float t = (GridHeight - rayOrigin.y) / rayDir.y;");
        source.ShouldContain("gl_FragDepth = DepthMode == 1 ? (1.0 - depth) : depth;");
        source.ShouldContain("fwidth(coord)");
        source.ShouldContain("float scale0 = baseCell * pow(10.0, lodFloor);");
        source.ShouldContain("float scale1 = scale0 * 10.0;");
        source.ShouldContain("PristineGrid(coord, dxy, scale0, GridLineWidth);");
        source.ShouldContain("GridXAxisColor");
        source.ShouldContain("GridZAxisColor");
        source.ShouldContain("smoothstep(0.0, 1.0, distanceFade)");
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
