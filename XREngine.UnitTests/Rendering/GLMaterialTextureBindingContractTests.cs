using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLMaterialTextureBindingContractTests
{
    [Test]
    public void GLMaterial_BindsSparseTextureListsToCompactTextureUnits()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs");

        source.ShouldContain("int textureUnit = 0;");
        source.ShouldContain("SetTextureUniform(program, textureIndex, texture, textureUnit);");
        source.ShouldContain("textureUnit++;");
        source.ShouldContain("if (i == textureIndex)");
        source.ShouldContain("textureUnit = nextTextureUnit;");
        source.ShouldNotContain("program?.Sampler(texture.ResolveSamplerName(textureIndex, samplerNameOverride), texture, textureIndex);");
    }

    [Test]
    public void FallbackSamplerBinding_PreservesExistingLayoutBoundSamplerAssignments()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs");

        source.ShouldContain("Api.GetUniform(BindingId, location, out int assignedUnit);");
        source.ShouldContain("if (assignedUnit >= 0 && _boundSamplerUnits.ContainsKey(assignedUnit))");
        source.ShouldContain("continue;");
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