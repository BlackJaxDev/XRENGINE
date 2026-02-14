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
}
