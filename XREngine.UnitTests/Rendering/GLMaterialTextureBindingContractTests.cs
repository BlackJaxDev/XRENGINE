using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLMaterialTextureBindingContractTests
{
    [Test]
    public void GLMaterial_PreservesTextureIndexBindingForSparseTextureLists()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs");

        source.ShouldContain("SetTextureUniform(program, material, textureIndex, texture, textureIndex);");
        source.ShouldContain("textureUnit = textureIndex;");
        source.ShouldNotContain("int textureUnit = 0;");
        source.ShouldNotContain("textureUnit++;");
        source.ShouldNotContain("textureUnit = nextTextureUnit;");
    }

    [Test]
    public void GLMaterial_BindsIndexedTextureAliasWhenShaderExpectsTextureSlots()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs");

        source.ShouldContain("string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);");
        source.ShouldContain("if (program.GetUniformLocation(resolvedSamplerName) >= 0)");
        source.ShouldContain("if (program.GetUniformLocation(indexedSamplerName) >= 0)");
        source.ShouldContain("program.Sampler(indexedSamplerName, texture, textureUnit);");
    }

    [Test]
    public void FallbackSamplerBinding_PreservesExistingLayoutBoundSamplerAssignments()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs");

        source.ShouldContain("Api.GetUniform(BindingId, location, out int assignedUnit);");
        source.ShouldContain("if (assignedUnit >= 0 && _boundSamplerUnits.ContainsKey(assignedUnit))");
        source.ShouldContain("continue;");
    }

    [Test]
    public void SamplerBinding_RelocatesNamedEngineSamplersWhenMaterialSlotsAlreadyUseFixedUnits()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.Samplers.cs");

        source.ShouldContain("TryResolveSamplerTextureUnit(location, texture, textureUnit, out int resolvedTextureUnit)");
        source.ShouldContain("if (location < 0)");
        source.ShouldContain("return false;");
        source.ShouldContain("TryFindFreeSamplerTextureUnit(out resolvedTextureUnit)");
        source.ShouldContain("Uniform(location, resolvedTextureUnit);");
        source.ShouldNotContain("_boundSamplerUnits.Add(textureUnit);");
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
