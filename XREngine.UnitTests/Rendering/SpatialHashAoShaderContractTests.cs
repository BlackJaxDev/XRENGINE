using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SpatialHashAoShaderContractTests
{
    [Test]
    public void SpatialHashAoComputeShader_UsesDecodedNormalsDepthModeAndTransformIds()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/AO/SpatialHashAO.comp");

        source.ShouldContain("uniform usampler2D TransformIdTex;");
        source.ShouldContain("uniform float DistanceFade;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("vec2 encodedNormal = texelFetch(NormalTex, pixel, 0).xy;");
        source.ShouldContain("vec3 worldNormal = DecodeNormal(encodedNormal);");
        source.ShouldContain("uint transformId = texelFetch(TransformIdTex, pixel, 0).r;");
        source.ShouldContain("float viewDepth = max(abs(viewPos.z), 1e-4);");
        source.ShouldContain("int rayCount = ComputeRayCount(SamplesPerPixel, rngState);");
    }

    [Test]
    public void SpatialHashAoStereoComputeShader_UsesDecodedNormalsDepthModeAndTransformIds()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/AO/SpatialHashAOStereo.comp");

        source.ShouldContain("uniform usampler2DArray TransformIdTex;");
        source.ShouldContain("uniform float DistanceFade;");
        source.ShouldContain("uniform int DepthMode;");
        source.ShouldContain("vec2 encodedNormal = texelFetch(NormalTex, ivec3(pixel, eyeIndex), 0).xy;");
        source.ShouldContain("vec3 worldNormal = DecodeNormal(encodedNormal);");
        source.ShouldContain("uint transformId = texelFetch(TransformIdTex, ivec3(pixel, eyeIndex), 0).r;");
        source.ShouldContain("float viewDepth = max(abs(viewPos.z), 1e-4);");
        source.ShouldContain("int rayCount = ComputeRayCount(SamplesPerPixel, rngState);");
    }

    [Test]
    public void SpatialHashAoPass_BindsTransformIdDistanceFadeAndDepthMode()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SpatialHashAOPass.cs");

        source.ShouldContain("DispatchSpatialHashAO(state, normalTex, depthViewTex, transformIdTex");
        source.ShouldContain("_computeProgram.Sampler(\"TransformIdTex\", transformIdTex, 4);");
        source.ShouldContain("_computeProgram.Uniform(\"DistanceFade\", runtimeSettings.DistanceFade);");
        source.ShouldContain("_computeProgram.Uniform(EEngineUniform.DepthMode.ToStringFast()", Case.Insensitive);
        source.ShouldContain("_computeProgramStereo.Sampler(\"TransformIdTex\", transformIdTex, 4);");
        source.ShouldContain("_computeProgramStereo.Uniform(\"DistanceFade\", runtimeSettings.DistanceFade);");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file does not exist: {fullPath}");
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
