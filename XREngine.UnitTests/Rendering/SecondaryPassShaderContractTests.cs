using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SecondaryPassShaderContractTests
{
    [Test]
    public void GrabpassGaussian_UsesPreweightedFixedTapKernel()
    {
        string source = LoadShaderSource(Path.Combine("UI", "GrabpassGaussian.frag"));

        source.ShouldContain("const int MaxBlurTaps = 12;");
        source.ShouldContain("const vec2 BlurTapOffsets[MaxBlurTaps] = vec2[](");
        source.ShouldContain("const float BlurTapWeights[MaxBlurTaps] = float[](");
        source.ShouldContain("int activeTapCount = clamp(SampleCount, 0, MaxBlurTaps);");
        source.ShouldContain("vec2 blurStep = texelSize * max(BlurStrength, 0.0);");
        source.ShouldNotContain("gaussian(");
        source.ShouldNotContain("sqrt(");
        source.ShouldNotContain("cos(");
        source.ShouldNotContain("sin(");
    }

    [Test]
    public void SkyboxVertex_PrecomputesWorldRayAndRotation()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "Skybox.vs"));

        source.ShouldContain("layout(location = 1) out vec3 FragWorldDir;");
        source.ShouldContain("uniform mat4 InverseViewMatrix;");
        source.ShouldContain("uniform mat4 InverseProjMatrix;");
        source.ShouldContain("uniform float SkyboxRotation = 0.0;");
        source.ShouldContain("vec3 GetWorldRay(vec2 clipXY)");
        source.ShouldContain("vec3 RotateSkyDirection(vec3 dir)");
        source.ShouldContain("FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));");
    }

    [TestCase("Scene3D/SkyboxEquirect.fs")]
    [TestCase("Scene3D/SkyboxOctahedral.fs")]
    [TestCase("Scene3D/SkyboxCubemap.fs")]
    [TestCase("Scene3D/SkyboxCubemapArray.fs")]
    [TestCase("Scene3D/SkyboxGradient.fs")]
    [TestCase("Scene3D/SkyboxDynamic.fs")]
    public void SkyboxFragments_ConsumeInterpolatedWorldDirection(string shaderRelativePath)
    {
        string source = LoadShaderSource(shaderRelativePath);
        bool normalizesWorldDirection =
            source.Contains("vec3 dir = normalize(FragWorldDir);", StringComparison.Ordinal)
            || source.Contains("vec3 dir = SafeNormalize3(FragWorldDir);", StringComparison.Ordinal);

        source.ShouldContain("layout (location = 1) in vec3 FragWorldDir;");
        normalizesWorldDirection.ShouldBeTrue();
        source.ShouldNotContain("GetWorldDirection(");
        source.ShouldNotContain("uniform mat4 InverseProjMatrix;");
        source.ShouldNotContain("uniform mat4 InverseViewMatrix;");
    }

    [Test]
    public void SkyboxDynamic_AvoidsZeroVectorNormalization()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "SkyboxDynamic.fs"));

        source.ShouldContain("vec2 SafeNormalize2(vec2 v)");
        source.ShouldContain("vec3 SafeNormalize3(vec3 v)");
        source.ShouldContain("vec3 dir = SafeNormalize3(FragWorldDir);");
        source.ShouldNotContain("vec2 st = normalize(dir.xz)");
        source.ShouldNotContain("vec2 cloudUv = normalize(max(abs(dir.y), 0.06) * dir.xz)");
        source.ShouldNotContain("SafeNormalize2(dir.xz)");
        source.ShouldNotContain("SafeNormalize2(max(abs(dir.y), 0.06) * dir.xz)");
    }

    [Test]
    public void SkyboxDynamic_UsesSeamlessSphericalSampling()
    {
        string source = LoadShaderSource(Path.Combine("Scene3D", "SkyboxDynamic.fs"));

        // Stars and clouds must sample 3D noise directly on the direction vector
        // to avoid the diagonal seam + pole stretch that octahedral UV mapping produces.
        source.ShouldContain("vec3 starP = dir * starDensity;");
        source.ShouldContain("float starHash = Hash3(starCell);");
        source.ShouldContain("vec3 cloudP = dir * SkyCloudScale");
        source.ShouldContain("float cloudBase = Fbm3_6(warped);");
        source.ShouldContain("float cloudMask = smoothstep(-0.08, 0.12, dir.y);");

        // The old octahedral-based UVs must not be used for stars or clouds anymore.
        source.ShouldNotContain("vec2 starUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 cloudUv = DirectionToOctahedralPlane(dir)");
    }

    [Test]
    public void SkyboxComponent_FallbackSources_MatchVertexDirectionContract()
    {
        string source = ReadWorkspaceFile(Path.Combine("XRENGINE", "Scene", "Components", "Misc", "SkyboxComponent.cs"));
        bool normalizesWorldDirection =
            source.Contains("vec3 dir = normalize(FragWorldDir);", StringComparison.Ordinal)
            || source.Contains("vec3 dir = SafeNormalize3(FragWorldDir);", StringComparison.Ordinal);

        source.ShouldContain("Skybox shaders reconstruct and rotate view rays in the vertex stage");
        source.ShouldContain("layout(location = 1) out vec3 FragWorldDir;");
        source.ShouldContain("FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));");
        source.ShouldContain("layout (location = 1) in vec3 FragWorldDir;");
        normalizesWorldDirection.ShouldBeTrue();
    }

    [Test]
    public void SkyboxComponent_FallbackDynamicSource_AvoidsZeroVectorNormalization()
    {
        string source = ReadWorkspaceFile(Path.Combine("XRENGINE", "Scene", "Components", "Misc", "SkyboxComponent.cs"));

        source.ShouldContain("vec2 SafeNormalize2(vec2 v)");
        source.ShouldContain("vec3 SafeNormalize3(vec3 v)");
        source.ShouldContain("vec3 dir = SafeNormalize3(FragWorldDir);");
        source.ShouldNotContain("vec2 st = normalize(dir.xz)");
        source.ShouldNotContain("vec2 cloudUv = normalize(max(abs(dir.y), 0.06) * dir.xz)");
        source.ShouldNotContain("SafeNormalize2(dir.xz)");
        source.ShouldNotContain("SafeNormalize2(max(abs(dir.y), 0.06) * dir.xz)");
    }

    [Test]
    public void SkyboxComponent_FallbackDynamicSource_UsesSeamlessSphericalSampling()
    {
        string source = ReadWorkspaceFile(Path.Combine("XRENGINE", "Scene", "Components", "Misc", "SkyboxComponent.cs"));

        source.ShouldContain("vec3 starP = dir * starDensity;");
        source.ShouldContain("float starHash = Hash3(starCell);");
        source.ShouldContain("vec3 cloudP = dir * SkyCloudScale");
        source.ShouldContain("float cloudBase = Fbm3_6(warped);");
        source.ShouldContain("float cloudMask = smoothstep(-0.08, 0.12, dir.y);");

        source.ShouldNotContain("vec2 starUv = DirectionToOctahedralPlane(dir)");
        source.ShouldNotContain("vec2 cloudUv = DirectionToOctahedralPlane(dir)");
    }

    private static string LoadShaderSource(string shaderRelativePath)
        => ReadWorkspaceFile(Path.Combine("Build", "CommonAssets", "Shaders", shaderRelativePath));

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}