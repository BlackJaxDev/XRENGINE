using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShaderSourceResolverCachingTests
{
    [TearDown]
    public void TearDown()
        => ShaderSourceResolver.ClearCaches(clearRegisteredSnippets: true);

    [Test]
    public void XRShader_ResolvedSourceCache_InvalidatesWhenIncludeFileChanges()
    {
        string tempDir = CreateTempShaderRoot();
        string shaderPath = Path.Combine(tempDir, "Main.frag");
        string includePath = Path.Combine(tempDir, "Includes", "SharedLighting.glsl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(includePath)!);

            WriteTextWithAdvancedTimestamp(includePath, "const float XRE_INCLUDE_VALUE = 1.0;\n");
            WriteTextWithAdvancedTimestamp(
                shaderPath,
                "#version 450\n" +
                "#include \"Includes/SharedLighting.glsl\"\n" +
                "void main() {}\n");

            TextFile textFile = new()
            {
                FilePath = shaderPath,
                Text = File.ReadAllText(shaderPath),
            };
            XRShader shader = new(EShaderType.Fragment, textFile);

            string firstResolved = shader.GetResolvedSource();
            firstResolved.ShouldContain("XRE_INCLUDE_VALUE = 1.0");

            WriteTextWithAdvancedTimestamp(includePath, "const float XRE_INCLUDE_VALUE = 2.0;\n");

            string secondResolved = shader.GetResolvedSource();
            secondResolved.ShouldContain("XRE_INCLUDE_VALUE = 2.0");
        }
        finally
        {
            DeleteDirectoryBestEffort(Path.GetDirectoryName(tempDir)!);
        }
    }

    [Test]
    public void Resolver_InvalidatesSnippetExpansionWhenSnippetFileChanges()
    {
        string tempDir = CreateTempShaderRoot();
        string shaderPath = Path.Combine(tempDir, "Main.frag");
        string snippetPath = Path.Combine(tempDir, "Snippets", "UnitSnippet.glsl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snippetPath)!);

            WriteTextWithAdvancedTimestamp(snippetPath, "const float XRE_SNIPPET_VALUE = 1.0;\n");
            WriteTextWithAdvancedTimestamp(
                shaderPath,
                "#version 450\n" +
                "#pragma snippet \"UnitSnippet\"\n" +
                "void main() { float v = XRE_SNIPPET_VALUE; }\n");

            string firstResolved = ShaderSourceResolver.ResolveSource(File.ReadAllText(shaderPath), shaderPath);
            firstResolved.ShouldContain("XRE_SNIPPET_VALUE = 1.0");

            WriteTextWithAdvancedTimestamp(snippetPath, "const float XRE_SNIPPET_VALUE = 2.0;\n");

            string secondResolved = ShaderSourceResolver.ResolveSource(File.ReadAllText(shaderPath), shaderPath);
            secondResolved.ShouldContain("XRE_SNIPPET_VALUE = 2.0");
        }
        finally
        {
            DeleteDirectoryBestEffort(Path.GetDirectoryName(tempDir)!);
        }
    }

    [Test]
    public void Resolver_DefaultSnippetExpansionKeepsRuntimeDeclarationsAfterVariantPruning()
    {
        const string snippetName = "UnitTest_RuntimeSnippetGlobals";
        const string shaderSource =
            "#version 450\n" +
            "#pragma snippet \"UnitTest_RuntimeSnippetGlobals\"\n" +
            "void main() { float v = UnitSnippetValue(); }\n";

        try
        {
            ShaderSourceResolver.RegisterSnippet(
                snippetName,
                "const float XRE_USED_SNIPPET_VALUE = 1.0;\n" +
                "const float XRE_RUNTIME_SNIPPET_VALUE = 2.0;\n" +
                "float UnitSnippetValue() { return XRE_USED_SNIPPET_VALUE; }\n");

            string variantResolved = ShaderSourceResolver.ResolveSource(
                shaderSource,
                sourcePath: null,
                options: new ShaderSourceResolverOptions { EnableSnippetDeadCodeElimination = true });
            variantResolved.ShouldContain("UnitSnippetValue");
            variantResolved.ShouldNotContain("XRE_RUNTIME_SNIPPET_VALUE");

            string runtimeResolved = ShaderSourceResolver.ResolveSource(shaderSource, sourcePath: null);
            runtimeResolved.ShouldContain("XRE_RUNTIME_SNIPPET_VALUE");
        }
        finally
        {
            ShaderSourceResolver.UnregisterSnippet(snippetName);
        }
    }

    [Test]
    public void SnippetDeadCodeEliminator_RetainsReferencedLayoutAndInitializerDeclarations()
    {
        const string source =
            """
            #version 450 core
            // ===== BEGIN SNIPPET: Unit =====
            layout(binding = 6) uniform sampler2D BRDF;
            layout(location = 22) in float FragViewIndex;
            const vec2 XRENGINE_ShadowPoissonDisk[1] = vec2[](vec2(0.0));
            mat4 XRENGINE_ResolvedForwardViewMatrix = mat4(1.0);
            vec4 UsedHelper()
            {
                return vec4(
                    texture(BRDF, XRENGINE_ShadowPoissonDisk[0]).r +
                    FragViewIndex +
                    XRENGINE_ResolvedForwardViewMatrix[0][0]);
            }
            vec4 UnusedHelper() { return vec4(0.0); }
            // ===== END SNIPPET: Unit =====
            void main() { vec4 color = UsedHelper(); }
            """;

        string trimmed = GlslSnippetDeadCodeEliminator.Trim(source);

        trimmed.ShouldContain("layout(binding = 6) uniform sampler2D BRDF;");
        trimmed.ShouldContain("layout(location = 22) in float FragViewIndex;");
        trimmed.ShouldContain("const vec2 XRENGINE_ShadowPoissonDisk[1]");
        trimmed.ShouldContain("mat4 XRENGINE_ResolvedForwardViewMatrix");
        trimmed.ShouldContain("UsedHelper");
        trimmed.ShouldNotContain("UnusedHelper");
    }

    [Test]
    public void LegacyPreprocessor_ResolvesRegisteredSnippetsThroughSharedResolver()
    {
        const string snippetName = "UnitTest_RegisteredSnippet";

        try
        {
            ShaderSnippets.Register(snippetName, "const vec3 XRE_REGISTERED_SNIPPET = vec3(0.1, 0.2, 0.3);\n");

            string resolved = ShaderSourcePreprocessor.ResolveSource(
                "#version 450\n#pragma snippet \"UnitTest_RegisteredSnippet\"\nvoid main() { vec3 v = XRE_REGISTERED_SNIPPET; }\n",
                sourcePath: null);

            resolved.ShouldContain("XRE_REGISTERED_SNIPPET");
            resolved.ShouldNotContain("#pragma snippet");
        }
        finally
        {
            ShaderSnippets.Unregister(snippetName);
        }
    }

    [Test]
    public void Resolver_ExpandsIncludeWithTrailingLineComment()
    {
        string tempDir = CreateTempShaderRoot();
        string shaderPath = Path.Combine(tempDir, "Main.frag");
        string includePath = Path.Combine(tempDir, "Includes", "Helper.glsl");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(includePath)!);
            WriteTextWithAdvancedTimestamp(includePath, "const float XRE_HELPER_VALUE = 42.0;\n");

            // The include line has a trailing `//` comment. The resolver must still expand it
            // and must not leave a literal `#include` in the resolved source.
            WriteTextWithAdvancedTimestamp(
                shaderPath,
                "#version 450\n" +
                "#include \"Includes/Helper.glsl\"   // helper comment\n" +
                "void main() {}\n");

            TextFile textFile = new()
            {
                FilePath = shaderPath,
                Text = File.ReadAllText(shaderPath),
            };
            XRShader shader = new(EShaderType.Fragment, textFile);

            string resolved = shader.GetResolvedSource();
            resolved.ShouldContain("XRE_HELPER_VALUE = 42.0");
            resolved.ShouldNotContain("#include");
        }
        finally
        {
            DeleteDirectoryBestEffort(Path.GetDirectoryName(tempDir)!);
        }
    }

    private static string CreateTempShaderRoot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "xre-shader-cache-" + Guid.NewGuid().ToString("N"));
        string shaderRoot = Path.Combine(tempDir, "Shaders");
        Directory.CreateDirectory(shaderRoot);
        return shaderRoot;
    }

    private static void WriteTextWithAdvancedTimestamp(string path, string text)
    {
        DateTime writeTimeUtc = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path).AddSeconds(1)
            : DateTime.UtcNow;

        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
    }

    private static void DeleteDirectoryBestEffort(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, recursive: true);
        }
        catch
        {
        }
    }
}
