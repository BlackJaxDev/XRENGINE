using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialInspectorShaderSlotContractTests
{
    [Test]
    public void SetShader_ReplacesExistingStageAssignment()
    {
        XRShader fragmentA = new(EShaderType.Fragment);
        XRShader fragmentB = new(EShaderType.Fragment);
        XRShader vertex = new(EShaderType.Vertex);
        XRMaterial material = new(fragmentA, vertex);

        material.SetShader(EShaderType.Fragment, fragmentB);

        material.FragmentShaders.Count.ShouldBe(1);
        material.FragmentShaders[0].ShouldBeSameAs(fragmentB);
        material.VertexShaders.Count.ShouldBe(1);
        material.VertexShaders[0].ShouldBeSameAs(vertex);
        material.Shaders.Count(shader => shader.Type == EShaderType.Fragment).ShouldBe(1);
    }

    [Test]
    public void SetShader_CoercesAssignedShaderTypeWhenRequested()
    {
        XRShader shader = new(EShaderType.Vertex);
        XRMaterial material = new();

        material.SetShader(EShaderType.Fragment, shader, coerceShaderType: true);

        shader.Type.ShouldBe(EShaderType.Fragment);
        material.FragmentShaders.Count.ShouldBe(1);
        material.FragmentShaders[0].ShouldBeSameAs(shader);
        material.VertexShaders.Count.ShouldBe(0);
    }

    [Test]
    public void NormalizeShaderStages_PrefersLastAssignmentPerStage()
    {
        XRShader firstFragment = new(EShaderType.Fragment);
        XRShader vertex = new(EShaderType.Vertex);
        XRShader secondFragment = new(EShaderType.Fragment);
        XRMaterial material = new(firstFragment, vertex, secondFragment);

        int removed = material.NormalizeShaderStages();

        removed.ShouldBe(1);
        material.FragmentShaders.Count.ShouldBe(1);
        material.FragmentShaders[0].ShouldBeSameAs(secondFragment);
        material.VertexShaders.Count.ShouldBe(1);
        material.Shaders.Count.ShouldBe(2);
    }

    [Test]
    public void EnhancedMaterialInspector_UsesStageSlotsAndDriveHints()
    {
        string inspectorSource = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.cs");
        string enhancedSource = ReadWorkspaceFile("XREngine.Editor/AssetEditors/XRMaterialInspector.Enhanced.cs");

        enhancedSource.ShouldContain("Fragment shader is required for render materials.");
        enhancedSource.ShouldContain("Normalize Duplicate Stages");
        enhancedSource.ShouldContain("material.SetShader(slot.Type, shader, coerceShaderType: true);");
        enhancedSource.ShouldContain("Copy Anim Path");
        enhancedSource.ShouldContain("texture.SamplerName = samplerName;");

        inspectorSource.ShouldContain("Create##Uniform_");
        inspectorSource.ShouldContain("DrawSamplerDriveCell(material, binding)");
        inspectorSource.ShouldContain("DrawParameterDriveCell(material, param");
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