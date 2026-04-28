using NUnit.Framework;
using Shouldly;
using XREngine.Editor.UI.Tools;
using XREngine.Rendering;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class ShaderEditorServicesTests
{
    [Test]
    public void ParseCompilerDiagnostics_ReadsGlslangStyleErrors()
    {
        const string output = "Shader compilation failed: ERROR: 0:12: 'foo' : undeclared identifier";

        IReadOnlyList<ShaderEditorDiagnostic> diagnostics = ShaderEditorServices.ParseCompilerDiagnostics(output);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Line.ShouldBe(12);
        diagnostics[0].Severity.ShouldBe(ShaderEditorDiagnosticSeverity.Error);
        diagnostics[0].Message.ShouldContain("undeclared identifier");
    }

    [Test]
    public void ParseCompilerDiagnostics_ReadsClangStyleWarnings()
    {
        const string output = "shader.frag:7:5: warning: unused variable 'temp'";

        IReadOnlyList<ShaderEditorDiagnostic> diagnostics = ShaderEditorServices.ParseCompilerDiagnostics(output);

        diagnostics.Count.ShouldBe(1);
        diagnostics[0].Line.ShouldBe(7);
        diagnostics[0].Column.ShouldBe(5);
        diagnostics[0].Severity.ShouldBe(ShaderEditorDiagnosticSeverity.Warning);
    }

    [Test]
    public void BuildCompletionItems_IncludesShaderDeclarations()
    {
        const string source = """
            uniform sampler2D Albedo;
            in vec2 TexCoord;

            vec3 ComputeLighting(vec3 normal)
            {
                vec3 lighting = normal;
                return lighting;
            }
            """;

        IReadOnlyList<ShaderEditorCompletionItem> items = ShaderEditorServices.BuildCompletionItems(source);

        items.ShouldContain(item => item.Name == "Albedo" && item.Kind == "uniform");
        items.ShouldContain(item => item.Name == "TexCoord" && item.Kind == "in");
        items.ShouldContain(item => item.Name == "ComputeLighting" && item.Kind == "function");
        items.ShouldContain(item => item.Name == "lighting" && item.Kind == "local");
        items.ShouldContain(item => item.Name == "texture" && item.Kind == "builtin");
    }

    [Test]
    public void InsertCompletionAtEnd_ReplacesTrailingPrefix()
    {
        string result = ShaderEditorServices.InsertCompletionAtEnd("vec3 color = text", "text", "texture");

        result.ShouldBe("vec3 color = texture");
    }

    [Test]
    public void AppendDirective_AddsDirectiveOnNewLineWithoutChangingOriginalText()
    {
        const string source = "#version 460 core";

        string result = ShaderEditorServices.AppendDirective(source, "#pragma snippet \"ForwardLighting\"");

        result.ShouldBe("#version 460 core" + Environment.NewLine + "#pragma snippet \"ForwardLighting\"" + Environment.NewLine);
        source.ShouldBe("#version 460 core");
    }

    [Test]
    public void PreviewRelativeInclude_LoadsFileNextToShaderSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "XREngineShaderEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string sourcePath = Path.Combine(directory, "Test.frag");
            string includePath = Path.Combine(directory, "Lighting.glsl");
            File.WriteAllText(sourcePath, "#version 460 core");
            File.WriteAllText(includePath, "vec3 ApplyLighting(vec3 color) { return color; }");

            ShaderEditorIncludePreview preview = ShaderEditorServices.PreviewRelativeInclude("Lighting.glsl", sourcePath);

            preview.Success.ShouldBeTrue();
            preview.Directive.ShouldBe("#include \"Lighting.glsl\"");
            preview.ResolvedPath.ShouldBe(Path.GetFullPath(includePath));
            preview.Source.ShouldContain("ApplyLighting");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void BuildInstrumentedPreviewSource_InsertsHelpersAndExpressionWrite()
    {
        const string source = """
            #version 460 core
            out vec4 FragColor;
            void main()
            {
                vec3 color = vec3(1.0);
                FragColor = vec4(color, 1.0);
            }
            """;

        string instrumented = ShaderEditorServices.BuildInstrumentedPreviewSource(source, 5, "color", "FragColor");

        instrumented.ShouldContain("XreShaderEditorPreviewColor(vec3 value)");
        instrumented.ShouldContain("FragColor = XreShaderEditorPreviewColor(color);");
        instrumented.ShouldContain("return;");
    }

    [Test]
    public void GenerateVariant_InsertsShadowDefineWithoutMutatingSource()
    {
        const string source = """
            #version 460 core
            out vec4 FragColor;
            void main()
            {
                FragColor = vec4(1.0);
            }
            """;

        ShaderEditorVariantResult result = ShaderEditorServices.GenerateVariant(
            source,
            "Test.frag",
            EShaderType.Fragment,
            ShaderEditorVariantPreset.ShadowCaster);

        result.Success.ShouldBeTrue();
        result.Source.ShouldContain("#define XRENGINE_SHADOW_CASTER_PASS");
        result.Source.IndexOf("#version", StringComparison.Ordinal).ShouldBeLessThan(
            result.Source.IndexOf("#define XRENGINE_SHADOW_CASTER_PASS", StringComparison.Ordinal));
        source.ShouldNotContain("XRENGINE_SHADOW_CASTER_PASS");
    }

    [Test]
    public void GenerateVariant_DoesNotDuplicateExistingDefine()
    {
        const string source = """
            #version 460 core
            #define XRENGINE_CUSTOM_VARIANT
            void main()
            {
            }
            """;

        ShaderEditorVariantResult result = ShaderEditorServices.GenerateVariant(
            source,
            "Test.frag",
            EShaderType.Fragment,
            ShaderEditorVariantPreset.CustomDefine,
            "XRENGINE_CUSTOM_VARIANT");

        result.Success.ShouldBeTrue();
        result.Source.Split("XRENGINE_CUSTOM_VARIANT").Length.ShouldBe(2);
    }
}
