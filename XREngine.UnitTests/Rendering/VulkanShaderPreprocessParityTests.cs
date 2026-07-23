using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanShaderPreprocessParityTests
{
    [Test]
    public void PhysicsChainDebugDrawShader_CompilesAfterAutoUniformRewrite()
    {
        string shaderPath = ResolveWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainDebugDraw.comp");
        var shaderSource = new TextFile
        {
            FilePath = shaderPath,
            Text = File.ReadAllText(shaderPath)
        };
        XRShader shader = new(EShaderType.Compute, shaderSource)
        {
            Name = "PhysicsChainDebugDraw.comp"
        };

        byte[] spirv = VulkanShaderCompiler.Compile(
            shader,
            out string entryPoint,
            out _,
            out string? rewrittenSource);

        entryPoint.ShouldBe("main");
        spirv.Length.ShouldBeGreaterThan(0);
        rewrittenSource.ShouldNotBeNull();
        rewrittenSource.ShouldContain("item.InterpolationAlpha");
        rewrittenSource.ShouldContain("item.InterpolationMode");
        rewrittenSource.ShouldNotContain("XREngine_AutoUniforms_Compute_Instance.InterpolationAlpha");
        rewrittenSource.ShouldNotContain("XREngine_AutoUniforms_Compute_Instance.InterpolationMode");
    }

    [Test]
    public void VulkanCompiler_ResolvesIncludeThenSnippet_FromIncludedFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "xre-vk-preprocess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string shaderPath = Path.Combine(tempDir, "main.frag");
        string includePath = Path.Combine(tempDir, "part.glsl");
        const string snippetName = "UnitTest_VulkanIncludeSnippet";

        try
        {
            ShaderSnippets.Register(snippetName, "const vec3 XRE_UNITTEST_VALUE = vec3(0.2, 0.3, 0.4);");

            File.WriteAllText(includePath, $"#pragma snippet \"{snippetName}\"{Environment.NewLine}");
            File.WriteAllText(shaderPath,
                "#version 450\n" +
                "#include \"part.glsl\"\n" +
                "layout(location = 0) out vec4 OutColor;\n" +
                "void main()\n" +
                "{\n" +
                "    OutColor = vec4(XRE_UNITTEST_VALUE, 1.0);\n" +
                "}\n");

            var shaderSource = new TextFile
            {
                FilePath = shaderPath,
                Text = File.ReadAllText(shaderPath)
            };
            XRShader shader = new(EShaderType.Fragment, shaderSource);

            byte[] spirv = VulkanShaderCompiler.Compile(shader, out string entryPoint, out _, out string? rewrittenSource);

            entryPoint.ShouldBe("main");
            spirv.Length.ShouldBeGreaterThan(0);
            rewrittenSource.ShouldNotBeNull();
            rewrittenSource.ShouldContain("XRE_UNITTEST_VALUE");
            rewrittenSource.ShouldNotContain("#pragma snippet");
        }
        finally
        {
            ShaderSnippets.Unregister(snippetName);

            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                // best-effort cleanup only
            }
        }
    }

    private static string ResolveWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(
                directory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            $"Could not locate workspace file '{relativePath}' from the test directory.");
    }
}
