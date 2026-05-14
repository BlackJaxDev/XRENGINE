using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialBindingLayoutTests
{
    [Test]
    public void OpaqueDeferredLayout_MatchesGpuMaterialTableRow()
    {
        MaterialBindingLayout layout = MaterialBindingLayouts.OpaqueDeferred;

        layout.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
        layout.RowWordCount.ShouldBe(12u);
        layout.RowByteCount.ShouldBe(48u);
        GPUMaterialTable.MaterialEntryUIntCount.ShouldBe(12u);

        layout.PackedMembers[0].Name.ShouldBe("AlbedoHandleIndex");
        layout.PackedMembers[0].WordOffset.ShouldBe(0u);
        layout.PackedMembers[3].Name.ShouldBe("Flags");
        layout.PackedMembers[3].WordOffset.ShouldBe(3u);
        layout.PackedMembers[4].Name.ShouldBe("BaseColorOpacity");
        layout.PackedMembers[4].WordOffset.ShouldBe(4u);
        layout.PackedMembers[5].Name.ShouldBe("RMSE");
        layout.PackedMembers[5].WordOffset.ShouldBe(8u);
    }

    [Test]
    public void OpaqueDeferredLayoutHash_ChangesWhenFieldsChange()
    {
        MaterialBindingLayout baseline = MaterialBindingLayouts.OpaqueDeferred;
        MaterialBindingLayout changed = new(
            baseline.Name,
            baseline.RenderPass,
            baseline.Outputs,
            [
                .. baseline.Fields,
                new MaterialBindingField("Clearcoat", "float", "clearcoat", "0.0"),
            ],
            baseline.Textures,
            supportsGeneratedMaterialTableDispatch: baseline.SupportsGeneratedMaterialTableDispatch);

        changed.LayoutHash.ShouldNotBe(baseline.LayoutHash);
        changed.RowWordCount.ShouldBeGreaterThan(baseline.RowWordCount);
    }

    [Test]
    public void GlslGenerator_EmitsSingleGeneratedRowShape()
    {
        string glsl = MaterialBindingGlslGenerator.GenerateMaterialTableDefinitions(
            MaterialBindingLayouts.OpaqueDeferred,
            bindless: true);

        glsl.ShouldContain("struct XR_MaterialRecord");
        glsl.ShouldContain("vec4 BaseColorOpacity;");
        glsl.ShouldContain("vec4 RMSE;");
        glsl.ShouldContain("layout(std430, binding = 11)");
        glsl.ShouldContain("layout(std430, binding = 17)");
        glsl.ShouldContain("void XR_LoadMaterial");
        glsl.ShouldContain("vec4 XR_TEXTURE2D");
        glsl.ShouldContain("#define MaterialEntry XR_MaterialRecord");
    }

    [Test]
    public void LayoutValidation_RejectsDuplicateFieldSemantic()
    {
        Should.Throw<ArgumentException>(() => new MaterialBindingLayout(
                "Invalid",
                (int)EDefaultRenderPass.OpaqueDeferred,
                MaterialBindingLayouts.OpaqueDeferred.Outputs,
                [
                    new MaterialBindingField("BaseColorOpacity", "vec4", "baseColorOpacity", "vec4(1.0)"),
                    new MaterialBindingField("Tint", "vec4", "baseColorOpacity", "vec4(1.0)"),
                ],
                MaterialBindingLayouts.OpaqueDeferred.Textures))
            .Message.ShouldContain("duplicate field semantic");
    }

    [Test]
    public void LayoutValidation_RejectsInvalidDefaultLiteral()
    {
        Should.Throw<ArgumentException>(() => new MaterialBindingLayout(
                "Invalid",
                (int)EDefaultRenderPass.OpaqueDeferred,
                MaterialBindingLayouts.OpaqueDeferred.Outputs,
                [
                    new MaterialBindingField("BaseColorOpacity", "vec4", "baseColorOpacity", "1.0"),
                ],
                []))
            .Message.ShouldContain("invalid default literal");
    }

    [Test]
    public void LayoutValidation_RejectsUnsupportedTextureDimensionality()
    {
        Should.Throw<ArgumentException>(() => new MaterialBindingLayout(
                "Invalid",
                (int)EDefaultRenderPass.OpaqueDeferred,
                MaterialBindingLayouts.OpaqueDeferred.Outputs,
                MaterialBindingLayouts.OpaqueDeferred.Fields,
                [
                    new MaterialTextureBinding("Albedo", "albedo", "Rectangle"),
                ]))
            .Message.ShouldContain("unsupported dimensionality");
    }

    [Test]
    public void RowPacker_WritesOpaqueDeferredUsingLayoutOffsets()
    {
        MaterialBindingLayout layout = MaterialBindingLayouts.OpaqueDeferred;
        Span<uint> row = stackalloc uint[(int)layout.RowWordCount];
        GPUMaterialEntry entry = new()
        {
            AlbedoHandleIndex = 2u,
            NormalHandleIndex = 3u,
            RMHandleIndex = 4u,
            Flags = 0x80000007u,
            BaseColorOpacity = new(0.25f, 0.5f, 0.75f, 0.9f),
            RMSE = new(0.1f, 0.2f, 0.3f, 0.4f),
        };

        MaterialBindingRowPacker.TryWriteOpaqueDeferred(layout, entry, row, out string error).ShouldBeTrue(error);

        row[0].ShouldBe(2u);
        row[1].ShouldBe(3u);
        row[2].ShouldBe(4u);
        row[3].ShouldBe(0x80000007u);
        UIntToFloat(row[4]).ShouldBe(0.25f, 0.000001f);
        UIntToFloat(row[7]).ShouldBe(0.9f, 0.000001f);
        UIntToFloat(row[8]).ShouldBe(0.1f, 0.000001f);
        UIntToFloat(row[11]).ShouldBe(0.4f, 0.000001f);
    }

    [Test]
    public void RowPacker_WritesLayoutDefaults()
    {
        MaterialBindingLayout layout = MaterialBindingLayouts.OpaqueDeferred;
        Span<uint> row = stackalloc uint[(int)layout.RowWordCount];

        MaterialBindingRowPacker.WriteDefaultRow(layout, row);

        row[0].ShouldBe(0u);
        row[3].ShouldBe(0u);
        UIntToFloat(row[4]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[7]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[8]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[9]).ShouldBe(0.0f, 0.000001f);
        UIntToFloat(row[10]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[11]).ShouldBe(0.0f, 0.000001f);
    }

    [Test]
    public void Resolver_RejectsUnknownMaterialSemantic()
    {
        const string source = """
            #version 450 core
            //@binding(name="Clearcoat", scope=material, semantic=clearcoat, storage=field)
            uniform float Clearcoat;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);
        MaterialBindingResolverResult result = MaterialBindingVariantResolver.Resolve(
            MaterialBindingLayouts.OpaqueDeferred,
            manifest);

        result.Outcome.ShouldBe(EMaterialBindingResolverOutcome.Invalid);
        result.Reason.ShouldContain("clearcoat");
    }

    [Test]
    public void Resolver_MissingSemantic_RequiresPerMaterialPath()
    {
        const string source = """
            #version 450 core
            //@binding(name="Tint", scope=material, storage=field)
            uniform vec4 Tint;
            """;

        ShaderUiManifest manifest = ShaderUiManifestParser.Parse(source);
        MaterialBindingResolverResult result = MaterialBindingVariantResolver.Resolve(
            MaterialBindingLayouts.OpaqueDeferred,
            manifest);

        result.Outcome.ShouldBe(EMaterialBindingResolverOutcome.PerMaterialRequired);
        result.Reason.ShouldContain("no material semantic");
    }

    private static float UIntToFloat(uint value)
        => BitConverter.UInt32BitsToSingle(value);
}
