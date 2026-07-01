using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AmbientOcclusionShaderUvConventionTests
{
    private static readonly string[] ScreenSpaceAoShaders =
    [
        "Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs",
        "Build/CommonAssets/Shaders/Scene3D/GTAOGenStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/GTAOBlur.fs",
        "Build/CommonAssets/Shaders/Scene3D/GTAOBlurStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/SSAOGen.fs",
        "Build/CommonAssets/Shaders/Scene3D/SSAOGenStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/SSAOBlur.fs",
        "Build/CommonAssets/Shaders/Scene3D/SSAOBlurStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/HBAOPlusGen.fs",
        "Build/CommonAssets/Shaders/Scene3D/HBAOPlusGenStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/HBAOPlusBlur.fs",
        "Build/CommonAssets/Shaders/Scene3D/HBAOPlusBlurStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/MVAOGen.fs",
        "Build/CommonAssets/Shaders/Scene3D/MVAOBlur.fs",
        "Build/CommonAssets/Shaders/Scene3D/MSVOGen.fs",
        "Build/CommonAssets/Shaders/Scene3D/MSVOGenStereo.fs",
        "Build/CommonAssets/Shaders/Scene3D/SpatialHashAOGen.fs",
    ];

    [TestCaseSource(nameof(ScreenSpaceAoShaders))]
    public void ScreenSpaceAoShaders_UseSharedTextureUvConvention(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("#include \"AOCommon.glsl\"");
        source.ShouldContain("AOTextureUVFromFragPos(FragPos)");
        source.ShouldNotContain("uv = uv * 0.5f + 0.5f");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/SSAOGen.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/SSAOGenStereo.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/MVAOGen.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/SpatialHashAOGen.fs")]
    public void ProjectedAoSampleUvs_UseSharedClipUvConvention(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("AOTextureUVFromClipXY(");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/MSVOGen.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/MSVOGenStereo.fs")]
    public void MultiScaleAoDepthReconstruction_UsesSharedClipDepthPolicy(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("AOViewPosFromDepth(depth, uv");
        source.ShouldNotContain("vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f)");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOGenStereo.fs")]
    public void GroundTruthAoDepthReconstruction_UsesSharedClipDepthPolicy(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("AOViewPosFromDepth(depth, uv");
        source.ShouldContain("AOViewPosFromDepth(fDepth, fUV");
        source.ShouldContain("AOViewPosFromDepth(bDepth, bUV");
        source.ShouldNotContain("XRENGINE_ViewPosFromDepthRaw");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOGen.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOGenStereo.fs")]
    public void GroundTruthAoSliceTangent_UsesTextureToClipDirectionPolicy(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("vec2 clipDir = AOClipDirectionFromTextureDirection(screenDir);");
        source.ShouldContain("vec3 sliceTangent = normalize(vec3(clipDir.x * invProjX, clipDir.y * invProjY, 0.0f));");
        source.ShouldNotContain("vec3 sliceTangent = normalize(vec3(screenDir.x * invProjX, screenDir.y * invProjY, 0.0f));");
    }

    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOBlur.fs")]
    [TestCase("Build/CommonAssets/Shaders/Scene3D/GTAOBlurStereo.fs")]
    public void GroundTruthAoBlur_SkipsOutOfFrameTapsInsteadOfClampingEdges(string relativePath)
    {
        string source = ReadWorkspaceFile(relativePath).Replace("\r\n", "\n");

        source.ShouldContain("vec2 sampleUV = uv + BlurDirection * texelSize * float(offset);");
        source.ShouldContain("if (sampleUV.x < 0.0f || sampleUV.x > 1.0f || sampleUV.y < 0.0f || sampleUV.y > 1.0f)");
        source.ShouldNotContain("sampleUV = clamp");
    }

    [Test]
    public void AoCommon_UsesRuntimeClipSpacePolicy()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/AOCommon.glsl").Replace("\r\n", "\n");

        source.ShouldContain("uniform float ScreenWidth;");
        source.ShouldContain("uniform float ScreenHeight;");
        source.ShouldContain("uniform vec2 ScreenOrigin;");
        source.ShouldContain("uniform int ClipSpaceYDirection;");
        source.ShouldContain("uniform int ClipDepthRange;");
        source.ShouldContain("float AODepthToClipZ(float depth)");
        source.ShouldContain("ClipDepthRange == 1 ? depth * 2.0f - 1.0f : depth");
        source.ShouldContain("if (FramebufferTextureYDirection == 1)");
        source.ShouldContain("vec2 AOClipDirectionFromTextureDirection(vec2 textureDirection)");
        source.ShouldContain("textureDirection.y = -textureDirection.y;");
        source.ShouldContain("(gl_FragCoord.xy - ScreenOrigin) / max(vec2(ScreenWidth, ScreenHeight), vec2(1.0f))");
        source.ShouldContain("AODepthToClipZ(depth)");
        source.ShouldNotContain("vec4(vec3(uv, depth) * 2.0f - 1.0f, 1.0f)");
    }

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
