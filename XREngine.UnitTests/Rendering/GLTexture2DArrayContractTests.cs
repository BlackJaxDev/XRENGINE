using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLTexture2DArrayContractTests
{
    [Test]
    public void GLTexture2DArray_AllocatesStorageForArraysWithoutSourceSlices()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs");

        source.ShouldContain("if (firstSource is null)");
        source.ShouldContain("EnsureStorage(desiredInternalFormat, targetWidth, targetHeight, targetDepth, targetLevels);");
        source.ShouldContain("ApplyMipRangeParameters();");
    }

    [Test]
    public void GLTexture2DArray_SkipsUndefinedSourceMipLevelsBeforeCopy()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs");

        source.ShouldContain("Api.GetTextureLevelParameter(srcId, mip, GLEnum.TextureWidth, out int sourceMipWidth);");
        source.ShouldContain("Api.GetTextureLevelParameter(srcId, mip, GLEnum.TextureHeight, out int sourceMipHeight);");
        source.ShouldContain("if (sourceMipWidth <= 0 || sourceMipHeight <= 0)");
        source.ShouldContain("Api.CopyImageSubData(");
    }

    [Test]
    public void GLTexture2DArray_ClampsMaxMipLevelToAllocatedStorage()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs");

        source.ShouldContain("int allocatedMaxLevel = _allocatedLevels > 0");
        source.ShouldContain("return Math.Max(baseLevel, Math.Min(allocatedMaxLevel, configuredMaxLevel));");
        source.ShouldContain("Api.TextureParameterI(BindingId, GLEnum.TextureMaxLevel, in maxLevel);");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}