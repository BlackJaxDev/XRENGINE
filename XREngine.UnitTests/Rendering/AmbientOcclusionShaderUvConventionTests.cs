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
