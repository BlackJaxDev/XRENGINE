using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLShaderSourceCompatibilityTests
{
    [Test]
    public void VertexShader_InjectsMissingOutputBlock_ForSeparablePrograms()
    {
        string source = "#version 450\nlayout(location = 0) in vec3 Position;\nvoid main()\n{\n    gl_Position = vec4(Position, 1.0);\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.Vertex, separableProgram: true);

        rewritten.ShouldContain("out gl_PerVertex");
        rewritten.ShouldContain("vec4 gl_Position;");
        rewritten.ShouldContain("float gl_PointSize;");
        rewritten.ShouldContain("float gl_ClipDistance[];");
        rewritten.ShouldContain("layout(location = 0) in vec3 Position;");
    }

    [Test]
    public void GeometryShader_InjectsMissingInputAndOutputBlocks_ForSeparablePrograms()
    {
        string source = "#version 450\nlayout(triangles) in;\nlayout(triangle_strip, max_vertices = 3) out;\nvoid main()\n{\n    gl_Position = gl_in[0].gl_Position;\n    EmitVertex();\n    EndPrimitive();\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.Geometry, separableProgram: true);

        rewritten.ShouldContain("in gl_PerVertex");
        rewritten.ShouldContain("} gl_in[];");
        rewritten.ShouldContain("out gl_PerVertex");
        rewritten.ShouldContain("};");
    }

    [Test]
    public void TessControlShader_InjectsArrayOutputBlock_ForSeparablePrograms()
    {
        string source = "#version 450\nlayout(vertices = 3) out;\nvoid main()\n{\n    gl_out[gl_InvocationID].gl_Position = gl_in[gl_InvocationID].gl_Position;\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.TessControl, separableProgram: true);

        rewritten.ShouldContain("in gl_PerVertex");
        rewritten.ShouldContain("} gl_in[];");
        rewritten.ShouldContain("out gl_PerVertex");
        rewritten.ShouldContain("} gl_out[];");
    }

    [Test]
    public void TessEvaluationShader_InjectsMissingInputAndOutputBlocks_ForSeparablePrograms()
    {
        string source = "#version 450\nlayout(triangles, equal_spacing, ccw) in;\nvoid main()\n{\n    vec4 position = gl_in[0].gl_Position;\n    gl_Position = position;\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.TessEvaluation, separableProgram: true);

        rewritten.ShouldContain("in gl_PerVertex");
        rewritten.ShouldContain("} gl_in[];");
        rewritten.ShouldContain("out gl_PerVertex");
        rewritten.ShouldContain("vec4 position = gl_in[0].gl_Position;");
    }

    [Test]
    public void ExistingBlocks_AreNotDuplicated()
    {
        string source = "#version 450\nout gl_PerVertex\n{\n    vec4 gl_Position;\n    float gl_PointSize;\n    float gl_ClipDistance[];\n};\nvoid main()\n{\n    gl_Position = vec4(0.0);\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.Vertex, separableProgram: true);

        rewritten.ShouldBe(source);
    }

    [Test]
    public void NonSeparablePrograms_AreLeftUntouched()
    {
        string source = "#version 450\nvoid main()\n{\n    gl_Position = vec4(0.0);\n}\n";

        string rewritten = GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(source, EShaderType.Vertex, separableProgram: false);

        rewritten.ShouldBe(source);
    }
}