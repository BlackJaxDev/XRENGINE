using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GLShaderAttributeLayoutResolverTests
{
    [Test]
    public void ResolveVertexInputLocations_ParsesUberStyleExplicitLayouts()
    {
        string source = "#version 450 core\n" +
            "layout(location = 0) in vec3 Position;\n" +
            "layout(location = 1) in vec3 Normal;\n" +
            "layout(location = 2) in vec4 Tangent;\n" +
            "layout(location = 3) in vec2 TexCoord0;\n" +
            "layout(location = 7) in vec4 Color0;\n";

        IReadOnlyDictionary<string, int> locations = GLShaderAttributeLayoutResolver.ResolveVertexInputLocations(source);

        locations["Position"].ShouldBe(0);
        locations["Normal"].ShouldBe(1);
        locations["Tangent"].ShouldBe(2);
        locations["TexCoord0"].ShouldBe(3);
        locations["Color0"].ShouldBe(7);
    }

    [Test]
    public void ResolveVertexInputLocations_IgnoresCommentedAndInitializedDeclarations()
    {
        string source = "#version 450 core\n" +
            "// layout(location = 5) in vec2 CommentedOut;\n" +
            "/* layout(location = 6) in vec2 BlockCommented; */\n" +
            "layout(location=4) in vec2 TexCoord1 = vec2(0.0);\n" +
            "layout(location = 8) in vec4 BlendWeights[2];\n";

        IReadOnlyDictionary<string, int> locations = GLShaderAttributeLayoutResolver.ResolveVertexInputLocations(source);

        locations.ContainsKey("CommentedOut").ShouldBeFalse();
        locations.ContainsKey("BlockCommented").ShouldBeFalse();
        locations["TexCoord1"].ShouldBe(4);
        locations["BlendWeights"].ShouldBe(8);
    }
}