using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanShaderRewriteDiagnosticsTests
{
    [TestCase("Scene3D/DeferredLightingDir.fs")]
    [TestCase("Scene3D/PostProcess.fs")]
    public void Rewrite_DoesNotEmitKnownBrokenTokens(string shaderRelativePath)
    {
        LoadedShaderSource loadedShader = LoadShaderSource(shaderRelativePath);
        string rewritten = RewriteForVulkanFragment(loadedShader.Source);

        rewritten.ShouldNotContain("layout(...)uniform");
        rewritten.ShouldNotContain("syntax error");
        rewritten.ShouldNotContain("XREngine_AutoUniforms_Fragment_Instance.XREngine_AutoUniforms_Fragment_Instance");

        TestContext.WriteLine($"--- Rewritten: {shaderRelativePath} ---");
        string[] lines = rewritten.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < Math.Min(lines.Length, 180); i++)
            TestContext.WriteLine($"{i + 1,4}: {lines[i]}");
    }

    private static string RewriteForVulkanFragment(string source)
    {
        Type? autoUniformType = typeof(VulkanShaderCompiler).Assembly
            .GetType("XREngine.Rendering.Vulkan.VulkanShaderAutoUniforms", throwOnError: true);

        MethodInfo? rewriteMethod = autoUniformType!.GetMethod(
            "Rewrite",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        rewriteMethod.ShouldNotBeNull();

        object? result = rewriteMethod!.Invoke(null, [source, EShaderType.Fragment]);
        result.ShouldNotBeNull();

        PropertyInfo? sourceProperty = result!.GetType().GetProperty("Source", BindingFlags.Instance | BindingFlags.Public);
        sourceProperty.ShouldNotBeNull();

        string? rewrittenSource = sourceProperty!.GetValue(result) as string;
        rewrittenSource.ShouldNotBeNull();
        return rewrittenSource!;
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
