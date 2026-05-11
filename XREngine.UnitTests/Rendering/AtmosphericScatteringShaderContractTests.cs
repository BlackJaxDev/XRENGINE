using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AtmosphericScatteringShaderContractTests
{
    [Test]
    public void CommonShader_ContainsPhysicalScatteringInvariants()
    {
        string source = LoadShader("Scene3D/Atmosphere/AtmosphereCommon.glsl");

        source.ShouldContain("XRENGINE_Atmosphere_IntersectSphere");
        source.ShouldContain("XRENGINE_Atmosphere_IntersectPlanet");
        source.ShouldContain("XRENGINE_Atmosphere_ClassifySegment");
        source.ShouldContain("XRENGINE_Atmosphere_RayleighDensity");
        source.ShouldContain("XRENGINE_Atmosphere_MieDensity");
        source.ShouldContain("XRENGINE_Atmosphere_RayleighPhase");
        source.ShouldContain("XRENGINE_Atmosphere_MiePhase");
        source.ShouldContain("clamp(g, -0.99, 0.99)");
        source.ShouldContain("XRENGINE_Atmosphere_OpticalDepthScaleApproximation");
        source.ShouldContain("XRENGINE_Atmosphere_ReferenceOpticalDepth");
        source.ShouldContain("for (int depthSample = 0;");
    }

    [Test]
    public void AerialPerspectiveShaders_KeepNeutralAndTemporalContracts()
    {
        string aerial = LoadShader("Scene3D/Atmosphere/AtmosphereAerialPerspective.fs");
        string reproject = LoadShader("Scene3D/Atmosphere/AtmosphereReproject.fs");
        string upscale = LoadShader("Scene3D/Atmosphere/AtmosphereUpscale.fs");

        aerial.ShouldContain("vec4(0.0, 0.0, 0.0, 1.0)");
        aerial.ShouldContain("InverseProjMatrix");
        aerial.ShouldContain("InverseViewMatrix");
        aerial.ShouldContain("XRENGINE_Atmosphere_ComputeScattering");

        reproject.ShouldContain("AtmosphereHistoryReady");
        reproject.ShouldContain("AtmospherePreviousViewProjection");
        reproject.ShouldContain("vec4(0.0, 0.0, 0.0, 1.0)");

        upscale.ShouldContain("AtmosphereHalfTemporal");
        upscale.ShouldContain("AtmosphereHalfDepth");
        upscale.ShouldContain("DepthView");
        upscale.ShouldContain("vec4(0.0, 0.0, 0.0, 1.0)");
    }

    [Test]
    public void PostProcessShader_OnlyCompositesAtmosphereAndLeavesRaymarchSeparated()
    {
        string source = LoadShader("Scene3D/PostProcess.fs");

        source.ShouldContain("uniform sampler2D AtmosphereColor;");
        source.ShouldContain("hdrSceneColor = hdrSceneColor * atmosphere.a + atmosphere.rgb;");
        source.ShouldNotContain("AtmosphereCommon.glsl");
        source.ShouldNotContain("XRENGINE_Atmosphere_ComputeScattering");

        int atmosphereComposite = source.IndexOf("texture(AtmosphereColor", StringComparison.Ordinal);
        int volumetricComposite = source.IndexOf("texture(VolumetricFogColor", StringComparison.Ordinal);

        atmosphereComposite.ShouldBeGreaterThanOrEqualTo(0);
        volumetricComposite.ShouldBeGreaterThan(atmosphereComposite);
    }

    [Test]
    public void SkyShader_ReusesSharedAtmosphereMath()
    {
        string source = LoadShader("Scene3D/Atmosphere/AtmosphereSky.fs");

        source.ShouldContain("#include \"AtmosphereCommon.glsl\"");
        source.ShouldContain("XRENGINE_Atmosphere_ClassifySegment");
        source.ShouldContain("XRENGINE_Atmosphere_ComputeScattering");
        source.ShouldContain("DebugMode");
    }

    private static string LoadShader(string relativePath)
    {
        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(ResolveShaderRoot(), normalizedRelativePath);
        File.Exists(fullPath).ShouldBeTrue($"Shader file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveShaderRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Build", "CommonAssets", "Shaders");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Build/CommonAssets/Shaders from test base directory.");
    }
}
