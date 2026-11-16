using NUnit.Framework;
using Shouldly;
using XREngine.Data.Colors;
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
        layout.RowWordCount.ShouldBe(40u);
        layout.RowByteCount.ShouldBe(160u);
        GPUMaterialTable.MaterialEntryUIntCount.ShouldBe(layout.RowWordCount);

        layout.PackedMembers[0].Name.ShouldBe("AlbedoHandleIndex");
        layout.PackedMembers[0].WordOffset.ShouldBe(0u);
        layout.PackedMembers[3].Name.ShouldBe("EmissiveHandleIndex");
        layout.PackedMembers[3].WordOffset.ShouldBe(3u);
        WordOffset(layout, "OpacityHandleIndex").ShouldBe(4);
        WordOffset(layout, "Flags").ShouldBe(5);
        WordOffset(layout, "BaseColorOpacity").ShouldBe(8);
        WordOffset(layout, "RMSE").ShouldBe(12);
        WordOffset(layout, "OcclusionStrength").ShouldBe(36);
        WordOffset(layout, "OcclusionControls").ShouldBe(37);
        WordOffset(layout, "OcclusionHandleIndex").ShouldBe(38);
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
        // A new field follows the occlusion fields. The occlusion handle packs after all fields,
        // so it moves to the last padding word and the row size does not change.
        WordOffset(changed, "Clearcoat").ShouldBe(38);
        WordOffset(changed, "OcclusionHandleIndex").ShouldBe(39);
        changed.RowWordCount.ShouldBe(baseline.RowWordCount);
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
    public void GlslGenerator_EmitsVulkanDescriptorIndexedTextureTable()
    {
        string glsl = MaterialBindingGlslGenerator.GenerateMaterialTableDefinitions(
            MaterialBindingLayouts.OpaqueDeferred,
            EMaterialTableTextureReferenceMode.VulkanDescriptorIndexTable);

        glsl.ShouldContain("layout(set = 2, binding = 31) uniform sampler2D XR_BindlessMaterialTextures[];");
        glsl.ShouldContain("XR_BindlessMaterialTextures[nonuniformEXT(descriptorIndex)]");
        glsl.ShouldContain("if (descriptorIndex == 0u)");
        glsl.ShouldNotContain("GL_ARB_bindless_texture");
        glsl.ShouldNotContain("uint64_t");
        glsl.ShouldNotContain("XR_TextureHandleTable");
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
        row[WordOffset(layout, "Flags")].ShouldBe(0x80000007u);
        int baseColorOffset = WordOffset(layout, "BaseColorOpacity");
        int rmseOffset = WordOffset(layout, "RMSE");
        UIntToFloat(row[baseColorOffset]).ShouldBe(0.25f, 0.000001f);
        UIntToFloat(row[baseColorOffset + 3]).ShouldBe(0.9f, 0.000001f);
        UIntToFloat(row[rmseOffset]).ShouldBe(0.1f, 0.000001f);
        UIntToFloat(row[rmseOffset + 3]).ShouldBe(0.4f, 0.000001f);
    }

    [Test]
    public void RowPacker_WritesLayoutDefaults()
    {
        MaterialBindingLayout layout = MaterialBindingLayouts.OpaqueDeferred;
        Span<uint> row = stackalloc uint[(int)layout.RowWordCount];

        MaterialBindingRowPacker.WriteDefaultRow(layout, row);

        row[0].ShouldBe(0u);
        row[WordOffset(layout, "Flags")].ShouldBe(0u);
        int baseColorOffset = WordOffset(layout, "BaseColorOpacity");
        int rmseOffset = WordOffset(layout, "RMSE");
        UIntToFloat(row[baseColorOffset]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[baseColorOffset + 3]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[rmseOffset]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[rmseOffset + 1]).ShouldBe(0.0f, 0.000001f);
        UIntToFloat(row[rmseOffset + 2]).ShouldBe(1.0f, 0.000001f);
        UIntToFloat(row[rmseOffset + 3]).ShouldBe(0.0f, 0.000001f);
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

    [Test]
    public void SourceEncoder_FlagsGltfPackedMetallicRoughness_OnlyForOneSharedTexture()
    {
        XRTexture2D packed = new(1u, 1u, ColorF4.White) { Name = "MetallicRoughness" };
        XRMaterial gltf = new()
        {
            SurfaceTextureBindings =
            [
                new MaterialSurfaceTextureBinding(EMaterialTextureSemantic.Metallic, packed, Channel: 2),
                new MaterialSurfaceTextureBinding(EMaterialTextureSemantic.Roughness, packed, Channel: 1),
            ],
        };

        uint gltfFlags = MaterialBindingSourceEncoder.Encode(gltf).Entry.Flags;
        (gltfFlags & (1u << 2)).ShouldNotBe(0u);
        (gltfFlags & GPUMaterialEntry.PackedMetallicRoughnessFlag).ShouldNotBe(0u);

        XRTexture2D metallic = new(1u, 1u, ColorF4.White) { Name = "Metallic" };
        XRTexture2D roughness = new(1u, 1u, ColorF4.White) { Name = "Roughness" };
        XRMaterial separate = new()
        {
            SurfaceTextureBindings =
            [
                new MaterialSurfaceTextureBinding(EMaterialTextureSemantic.Metallic, metallic),
                new MaterialSurfaceTextureBinding(EMaterialTextureSemantic.Roughness, roughness),
            ],
        };

        (MaterialBindingSourceEncoder.Encode(separate).Entry.Flags & GPUMaterialEntry.PackedMetallicRoughnessFlag)
            .ShouldBe(0u);
    }

    private static float UIntToFloat(uint value)
        => BitConverter.UInt32BitsToSingle(value);

    private static int WordOffset(MaterialBindingLayout layout, string name)
    {
        layout.TryGetPackedMember(name, out MaterialBindingPackedMember member).ShouldBeTrue();
        return (int)member.WordOffset;
    }
}
