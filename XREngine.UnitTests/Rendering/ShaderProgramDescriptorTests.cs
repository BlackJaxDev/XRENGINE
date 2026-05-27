using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

public sealed class ShaderProgramDescriptorTests
{
    [Test]
    public void FromShaders_ProducesStableKeyForEquivalentShaderSources()
    {
        XRShader vertexA = new(EShaderType.Vertex, "void main() { gl_Position = vec4(0.0); }") { Name = "GeneratedVertex" };
        XRShader fragmentA = new(EShaderType.Fragment, "out vec4 color; void main() { color = vec4(1.0); }") { Name = "UberFragment" };
        XRShader vertexB = new(EShaderType.Vertex, "void main() { gl_Position = vec4(0.0); }") { Name = "GeneratedVertex" };
        XRShader fragmentB = new(EShaderType.Fragment, "out vec4 color; void main() { color = vec4(1.0); }") { Name = "UberFragment" };

        XRRenderProgramDescriptor descriptorA = XRRenderProgramDescriptor.FromShaders(
            [fragmentA, vertexA],
            separable: false,
            renderSettingsVersion: 12,
            generatedVertexIdentity: XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vertexA.GetResolvedSource()),
            materialVariantKind: "MaterialVariant",
            materialVariantHash: 0x1234UL,
            vertexLayoutIdentity: "StaticMesh|Main",
            topologyKind: "CombinedMesh");

        XRRenderProgramDescriptor descriptorB = XRRenderProgramDescriptor.FromShaders(
            [fragmentB, vertexB],
            separable: false,
            renderSettingsVersion: 12,
            generatedVertexIdentity: XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vertexB.GetResolvedSource()),
            materialVariantKind: "MaterialVariant",
            materialVariantHash: 0x1234UL,
            vertexLayoutIdentity: "StaticMesh|Main",
            topologyKind: "CombinedMesh");

        descriptorB.ShouldBe(descriptorA);
        descriptorA.StableKey.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void FromShaders_ChangesKeyWhenSourceOrSettingsChange()
    {
        XRShader vertex = new(EShaderType.Vertex, "void main() { gl_Position = vec4(0.0); }") { Name = "GeneratedVertex" };
        XRShader fragment = new(EShaderType.Fragment, "out vec4 color; void main() { color = vec4(1.0); }") { Name = "UberFragment" };
        XRShader changedFragment = new(EShaderType.Fragment, "out vec4 color; void main() { color = vec4(0.5); }") { Name = "UberFragment" };

        XRRenderProgramDescriptor baseline = XRRenderProgramDescriptor.FromShaders(
            [fragment, vertex],
            separable: false,
            renderSettingsVersion: 12,
            generatedVertexIdentity: XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vertex.GetResolvedSource()),
            vertexLayoutIdentity: "StaticMesh|Main",
            topologyKind: "CombinedMesh");

        XRRenderProgramDescriptor sourceChanged = XRRenderProgramDescriptor.FromShaders(
            [changedFragment, vertex],
            separable: false,
            renderSettingsVersion: 12,
            generatedVertexIdentity: XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vertex.GetResolvedSource()),
            vertexLayoutIdentity: "StaticMesh|Main",
            topologyKind: "CombinedMesh");

        XRRenderProgramDescriptor settingsChanged = XRRenderProgramDescriptor.FromShaders(
            [fragment, vertex],
            separable: false,
            renderSettingsVersion: 13,
            generatedVertexIdentity: XRRenderProgramDescriptor.BuildGeneratedSourceIdentity(vertex.GetResolvedSource()),
            vertexLayoutIdentity: "StaticMesh|Main",
            topologyKind: "CombinedMesh");

        sourceChanged.StableKey.ShouldNotBe(baseline.StableKey);
        settingsChanged.StableKey.ShouldNotBe(baseline.StableKey);
    }

    [Test]
    public void ApplyShaderProgramMetadata_PreservesCombinedProgramName()
    {
        XRMaterial material = new() { Name = "SharedMat" };
        XRShader fragment = new(EShaderType.Fragment, "out vec4 color; void main() { color = vec4(1.0); }");
        XRRenderProgram program = new(false, false, fragment)
        {
            Name = "Combined:SharedMat",
            ProgramDescriptor = XRRenderProgramDescriptor.FromShaders(
                [fragment],
                separable: false,
                topologyKind: "CombinedMesh"),
        };

        material.ApplyShaderProgramMetadata(program);

        program.Name.ShouldBe("Combined:SharedMat");
        program.DiagnosticMetadata.MaterialName.ShouldBe("SharedMat");
        program.DiagnosticMetadata.TopologyKind.ShouldBe("CombinedMaterial");
    }
}
