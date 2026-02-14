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

    [Test]
    public void Rewrite_PreservesImageFormatQualifier_WhenInjectingSetAndBinding()
    {
        const string source = """
#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(r32f, binding = 1) uniform image2D ExposureOut;
void main() { }
""";

        string rewritten = RewriteForVulkanCompute(source);
        string lowered = rewritten.ToLowerInvariant();

        lowered.ShouldContain("layout(r32f");
        lowered.ShouldContain("uniform image2d exposureout");
        lowered.ShouldContain("set = 2");
        lowered.ShouldContain("binding = 1");
    }

    [Test]
    public void ComputeShader_WithR32fImage_CompilesAfterRewrite()
    {
        const string source = """
#version 460
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;
layout(binding = 0) uniform sampler2D SourceTex;
layout(r32f, binding = 1) uniform image2D ExposureOut;
void main()
{
    vec4 c = texelFetch(SourceTex, ivec2(0,0), 0);
    imageStore(ExposureOut, ivec2(0,0), vec4(c.r, 0.0, 0.0, 0.0));
}
""";

        var shaderSource = new TextFile
        {
            FilePath = "VulkanAutoExposure2D.comp",
            Text = source
        };

        XRShader shader = new(EShaderType.Compute, shaderSource)
        {
            Name = "VulkanAutoExposure2D.comp"
        };

        byte[] spirv = VulkanShaderCompiler.Compile(shader, out string entryPoint, out _, out _);

        entryPoint.ShouldBe("main");
        spirv.ShouldNotBeNull();
        spirv.Length.ShouldBeGreaterThan(0);
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

    private static string RewriteForVulkanCompute(string source)
    {
        Type? autoUniformType = typeof(VulkanShaderCompiler).Assembly
            .GetType("XREngine.Rendering.Vulkan.VulkanShaderAutoUniforms", throwOnError: true);

        MethodInfo? rewriteMethod = autoUniformType!.GetMethod(
            "Rewrite",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        rewriteMethod.ShouldNotBeNull();

        object? result = rewriteMethod!.Invoke(null, [source, EShaderType.Compute]);
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
