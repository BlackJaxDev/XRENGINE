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
        source.ShouldContain("XRENGINE_Atmosphere_PhaseRayleigh");
        source.ShouldContain("XRENGINE_Atmosphere_PhaseMie");
        source.ShouldContain("clamp(anisotropy, -0.99f, 0.99f)");
        source.ShouldContain("XRENGINE_Atmosphere_OpticalDepthScaleApproximation");
        source.ShouldContain("XRENGINE_Atmosphere_ReferenceOpticalDepth");
        source.ShouldContain("for (int i = 0; i < 32; ++i)");
    }

    [Test]
    public void AerialPerspectiveShaders_KeepNeutralAndTemporalContracts()
    {
        string aerial = LoadShader("Scene3D/Atmosphere/AtmosphereAerialPerspective.fs");
        string reproject = LoadShader("Scene3D/Atmosphere/AtmosphereReproject.fs");
        string upscale = LoadShader("Scene3D/Atmosphere/AtmosphereUpscale.fs");

        aerial.ShouldContain("vec4(0.0f, 0.0f, 0.0f, 1.0f)");
        aerial.ShouldContain("InverseProjMatrix");
        aerial.ShouldContain("InverseViewMatrix");
        aerial.ShouldContain("XRENGINE_Atmosphere_ComputeScattering");

        reproject.ShouldContain("AtmosphereHistoryReady");
        reproject.ShouldContain("AtmospherePreviousViewProjection");
        reproject.ShouldContain("bool IsNeutralAtmosphere(vec4 value)");

        upscale.ShouldContain("AtmosphereHalfTemporal");
        upscale.ShouldContain("AtmosphereHalfDepth");
        upscale.ShouldContain("XRENGINE_FramebufferTextureUVToClipXY");
        upscale.ShouldContain("vec4(0.0f, 0.0f, 0.0f, 1.0f)");
    }

    [Test]
    public void PostProcessShader_OnlyCompositesAtmosphereAndLeavesRaymarchSeparated()
    {
        string source = LoadShader("Scene3D/PostProcess.fs");

        source.ShouldContain("uniform sampler2D AtmosphereColor;");
        source.ShouldContain("hdrSceneColor = SafeColor(hdrSceneColor * atmosphere.a + atmosphere.rgb);");
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
        source.ShouldContain("XRENGINE_Atmosphere_DebugOutput");
        source.ShouldContain("XRENGINE_Atmosphere_ComputeScattering");
        source.ShouldContain("atmosphere = XRENGINE_Atmosphere_DebugOutput");
    }

    private static string LoadShader(string relativePath)
    {
        string normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(ResolveShaderRoot(), normalizedRelativePath);
        File.Exists(fullPath).ShouldBeTrue($"Shader file not found: {fullPath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n", StringComparison.Ordinal);
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
