using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanShaderCompilationRegressionTests
{
    private static readonly string[] FragmentShaders =
    [
        "Scene3D/DeferredLightingDir.fs",
        "Scene3D/PostProcess.fs",
        "Common/UITextBatched.fs",
        "Common/LitColoredForward.fs",
        "Common/LitTexturedForward.fs",
        "Common/LitTexturedNormalForward.fs",
        "Common/LitTexturedNormalSpecAlphaForward.fs",
        "Common/LitTexturedSpecAlphaForward.fs"
    ];

    private static readonly string[] VertexShaders =
    [
        "Common/UIQuadBatched.vs",
        "Common/UITextBatched.vs",
    ];

    [TestCaseSource(nameof(VertexShaders))]
    public void VertexShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Vertex, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);

        // Verify the rewritten source still contains SSBO declarations
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("buffer");

        // For UITextBatched, verify gl_InstanceID was rewritten to gl_InstanceIndex
        if (shaderRelativePath.Contains("UITextBatched"))
            rewrittenSource.ShouldContain("gl_InstanceIndex");
    }

    [TestCaseSource(nameof(FragmentShaders))]
    public void FragmentShader_CompilesToSpirv_ForVulkan(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        var shaderSource = new TextFile
        {
            FilePath = loadedShader.FullPath,
            Text = loadedShader.Source
        };

        XRShader shader = new(EShaderType.Fragment, shaderSource);

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
    }

    private static LoadedShaderSource LoadShaderSource(string shaderRelativePath)
    {
        string shaderRoot = ResolveShaderRoot();
        string normalizedRelativePath = shaderRelativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(shaderRoot, normalizedRelativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Shader file not found: {fullPath}", fullPath);

        return new LoadedShaderSource(fullPath, File.ReadAllText(fullPath));
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

    private readonly record struct LoadedShaderSource(string FullPath, string Source);
}
