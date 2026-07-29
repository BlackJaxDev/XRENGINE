using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedMaterialDatabaseContractTests
{
    [Test]
    public void ShaderVisibleMaterialRecords_MatchTheDeclaredPackedLayout()
    {
        Should.NotThrow(AdvancedShaderRecordLayout.ValidateCpuLayouts);
        Marshal.SizeOf<AdvancedMaterialRecord>().ShouldBe(64);
        Marshal.SizeOf<AdvancedShadingKernelRecord>().ShouldBe(64);
        Marshal.SizeOf<AdvancedMaterialLayoutRecord>().ShouldBe(48);
        Marshal.SizeOf<AdvancedMaterialLayoutMember>().ShouldBe(32);
        Marshal.SizeOf<AdvancedMaterialTextureBinding>().ShouldBe(32);
        Marshal.OffsetOf<AdvancedMaterialRecord>(
            nameof(AdvancedMaterialRecord.TextureReferenceOffset)).ToInt32().ShouldBe(36);
    }

    [Test]
    public void MaterialRows_ShareKernelIdentityAndAppendPackedPayloads()
    {
        AdvancedMaterialDatabase database = CreateDatabase();
        AdvancedGpuHandle layout = AddLayout(database);
        AdvancedGpuHandle kernel = AddKernel(database, layout);
        AdvancedMaterialValueDescriptor[] values = CreateValues();
        uint[] constants = [1u, 2u, 3u, 4u];
        AdvancedMaterialTextureBinding[] textures = [CreateTextureBinding(21u, 4u)];
        AdvancedMaterialRecord source = CreateMaterialSource();

        database.TryAddMaterial(
            layout,
            kernel,
            source,
            values,
            constants,
            textures,
            out AdvancedGpuHandle first).ShouldBeTrue();
        database.TryAddMaterial(
            layout,
            kernel,
            source,
            values,
            constants,
            textures,
            out AdvancedGpuHandle second).ShouldBeTrue();

        database.Materials.TryGet(first, out AdvancedMaterialRecord firstRow).ShouldBeTrue();
        database.Materials.TryGet(second, out AdvancedMaterialRecord secondRow).ShouldBeTrue();
        firstRow.StableRowId.ShouldBe(first.Index);
        secondRow.StableRowId.ShouldBe(second.Index);
        firstRow.ShadingKernelId.ShouldBe(kernel.Index);
        secondRow.ShadingKernelId.ShouldBe(kernel.Index);
        firstRow.ShadingKernelGeneration.ShouldBe(kernel.Generation);
        secondRow.ShadingKernelGeneration.ShouldBe(kernel.Generation);
        firstRow.ConstantWordOffset.ShouldBe(0u);
        secondRow.ConstantWordOffset.ShouldBe(4u);
        firstRow.TextureReferenceOffset.ShouldBe(0u);
        secondRow.TextureReferenceOffset.ShouldBe(1u);
        database.ConstantWords.ToArray().ShouldBe(
            new uint[] { 1u, 2u, 3u, 4u, 1u, 2u, 3u, 4u });
        database.TextureBindings.Length.ShouldBe(2);

        database.TryConsumeMaterialDirtyRange(out AdvancedMaterialDirtyRange materialDirty)
            .ShouldBeTrue();
        materialDirty.FirstRow.ShouldBe(0u);
        materialDirty.RowCount.ShouldBe(2u);
        materialDirty.Generation.ShouldBe(2ul);
        database.TryConsumeConstantDirtyRange(out AdvancedMaterialDirtyRange constantDirty)
            .ShouldBeTrue();
        constantDirty.ShouldBe(new AdvancedMaterialDirtyRange(0u, 8u, 2ul));
        database.TryConsumeTextureBindingDirtyRange(out AdvancedMaterialDirtyRange textureDirty)
            .ShouldBeTrue();
        textureDirty.ShouldBe(new AdvancedMaterialDirtyRange(0u, 2u, 2ul));
    }

    [Test]
    public void AuthoredValues_RejectUnknownDuplicateAndMismatchedSemantics()
    {
        AdvancedMaterialDatabase database = CreateDatabase();
        AdvancedGpuHandle layout = AddLayout(database);

        AdvancedMaterialValidationResult unknown = database.ValidateValues(
            layout,
            [new AdvancedMaterialValueDescriptor(0xDEADul, EAdvancedMaterialValueKind.Float, 1u)]);
        AdvancedMaterialValidationResult mismatched = database.ValidateValues(
            layout,
            [new AdvancedMaterialValueDescriptor(0xA11ul, EAdvancedMaterialValueKind.Vector4, 1u)]);
        AdvancedMaterialValidationResult duplicate = database.ValidateValues(
            layout,
            [
                new AdvancedMaterialValueDescriptor(0xA11ul, EAdvancedMaterialValueKind.Float, 1u),
                new AdvancedMaterialValueDescriptor(0xA11ul, EAdvancedMaterialValueKind.Float, 1u),
            ]);

        unknown.Failure.ShouldBe(EAdvancedMaterialValidationFailure.UndeclaredValue);
        mismatched.Failure.ShouldBe(EAdvancedMaterialValidationFailure.ValueKindMismatch);
        duplicate.Failure.ShouldBe(EAdvancedMaterialValidationFailure.DuplicateValue);

        AdvancedGpuHandle kernel = AddKernel(database, layout);
        database.TryAddMaterial(
            layout,
            kernel,
            CreateMaterialSource(),
            [new AdvancedMaterialValueDescriptor(0xDEADul, EAdvancedMaterialValueKind.Float, 1u)],
            [1u, 2u, 3u, 4u],
            [CreateTextureBinding(21u, 4u)],
            out _).ShouldBeFalse();
        database.Materials.Count.ShouldBe(0u);
        database.ConstantWords.Length.ShouldBe(0);
        database.TextureBindings.Length.ShouldBe(0);
    }

    [Test]
    public void RemovedMaterialHandle_BecomesStaleAndReusedSlotAdvancesGeneration()
    {
        AdvancedMaterialDatabase database = CreateDatabase();
        AdvancedGpuHandle layout = AddLayout(database);
        AdvancedGpuHandle kernel = AddKernel(database, layout);
        AdvancedMaterialRecord source = CreateMaterialSource();

        database.TryAddMaterial(
            layout,
            kernel,
            source,
            CreateValues(),
            [1u, 2u, 3u, 4u],
            [CreateTextureBinding(21u, 4u)],
            out AdvancedGpuHandle original).ShouldBeTrue();
        database.RemoveMaterial(original).ShouldBeTrue();
        database.Materials.IsCurrent(original).ShouldBeFalse();

        database.TryAddMaterial(
            layout,
            kernel,
            source,
            CreateValues(),
            [5u, 6u, 7u, 8u],
            [CreateTextureBinding(22u, 5u)],
            out AdvancedGpuHandle replacement).ShouldBeTrue();

        replacement.Index.ShouldBe(original.Index);
        replacement.Generation.ShouldNotBe(original.Generation);
        database.Materials.TryGet(original, out _).ShouldBeFalse();
        database.Materials.TryGet(replacement, out _).ShouldBeTrue();
    }

    [Test]
    public void MaterialReplacement_PublishesBoundedDirtyRangesWithoutAllocating()
    {
        AdvancedMaterialDatabase database = CreateDatabase();
        AdvancedGpuHandle layout = AddLayout(database);
        AdvancedGpuHandle kernel = AddKernel(database, layout);
        AdvancedMaterialValueDescriptor[] values = CreateValues();
        uint[] initialConstants = [1u, 2u, 3u, 4u];
        uint[] replacementConstants = [5u, 6u, 7u, 8u];
        AdvancedMaterialTextureBinding[] initialTextures =
            [CreateTextureBinding(21u, 4u)];
        AdvancedMaterialTextureBinding[] replacementTextures =
            [CreateTextureBinding(22u, 5u)];
        AdvancedMaterialRecord source = CreateMaterialSource();

        database.TryAddMaterial(
                layout,
                kernel,
                source,
                values,
                initialConstants,
                initialTextures,
                out AdvancedGpuHandle material)
            .ShouldBeTrue();
        database.TryConsumeMaterialDirtyRange(out _).ShouldBeTrue();
        database.TryConsumeConstantDirtyRange(out _).ShouldBeTrue();
        database.TryConsumeTextureBindingDirtyRange(out _).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool replaced = database.TryReplaceMaterial(
            material,
            layout,
            kernel,
            source,
            values,
            replacementConstants,
            replacementTextures);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        replaced.ShouldBeTrue();
        allocated.ShouldBe(0L);
        database.TryConsumeMaterialDirtyRange(
                out AdvancedMaterialDirtyRange materialDirty)
            .ShouldBeTrue();
        materialDirty.ShouldBe(new AdvancedMaterialDirtyRange(0u, 1u, 2ul));
        database.TryConsumeConstantDirtyRange(
                out AdvancedMaterialDirtyRange constantDirty)
            .ShouldBeTrue();
        constantDirty.ShouldBe(new AdvancedMaterialDirtyRange(4u, 4u, 2ul));
        database.TryConsumeTextureBindingDirtyRange(
                out AdvancedMaterialDirtyRange textureDirty)
            .ShouldBeTrue();
        textureDirty.ShouldBe(new AdvancedMaterialDirtyRange(1u, 1u, 2ul));
        database.Materials.TryGet(material, out AdvancedMaterialRecord row)
            .ShouldBeTrue();
        row.ConstantWordOffset.ShouldBe(4u);
        row.TextureReferenceOffset.ShouldBe(1u);
    }

    [Test]
    public void ShaderCacheKey_ContainsPipelineAxesButNoMaterialInstanceIdentity()
    {
        AdvancedGpuHandle kernel = new(5u, 2u);
        AdvancedShaderCacheKey first = new(
            kernel,
            0x1234ul,
            17u,
            EAdvancedMaterialCoverageMode.Masked,
            EAdvancedShaderViewMode.DesktopSingleView,
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing);
        AdvancedShaderCacheKey second = new(
            kernel,
            0x1234ul,
            17u,
            EAdvancedMaterialCoverageMode.Masked,
            EAdvancedShaderViewMode.DesktopSingleView,
            RuntimeGraphicsApiKind.Vulkan,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing);

        first.ShouldBe(second);
        (first with { Kernel = new AdvancedGpuHandle(6u, 2u) }).ShouldNotBe(first);
        (first with { MaterialLayoutHash = 0x5678ul }).ShouldNotBe(first);
        (first with { VertexFormatId = 18u }).ShouldNotBe(first);
        (first with { CoverageMode = EAdvancedMaterialCoverageMode.Opaque }).ShouldNotBe(first);
        (first with { ViewMode = EAdvancedShaderViewMode.StereoArray }).ShouldNotBe(first);
        (first with { Backend = RuntimeGraphicsApiKind.OpenGL }).ShouldNotBe(first);
        (first with { TextureEncoding = EAdvancedTextureIndirectionMode.VulkanDescriptorHeap })
            .ShouldNotBe(first);
        typeof(AdvancedShaderCacheKey).GetProperties()
            .Select(static property => property.Name)
            .ShouldNotContain("Material");
    }

    private static AdvancedMaterialDatabase CreateDatabase()
        => new(
            materialCapacity: 8u,
            kernelCapacity: 4u,
            layoutCapacity: 4u,
            layoutMemberCapacity: 8u,
            constantWordCapacity: 64u,
            textureBindingCapacity: 16u);

    private static AdvancedGpuHandle AddLayout(AdvancedMaterialDatabase database)
    {
        AdvancedMaterialLayoutRecord record = new()
        {
            LayoutHash = 0xACED1234ul,
            ConstantWordCount = 4u,
            TextureReferenceCount = 1u,
            RequiredAttributeMask =
                EAdvancedMaterialRequiredAttributeMask.Position |
                EAdvancedMaterialRequiredAttributeMask.Normal,
        };
        AdvancedMaterialLayoutMember[] members =
        [
            new(0xA11ul, EAdvancedMaterialValueKind.Float, 0u, 1u),
            new(0xB22ul, EAdvancedMaterialValueKind.Texture, 0u, 1u),
        ];
        database.TryAddLayout(record, members, out AdvancedGpuHandle handle)
            .ShouldBeTrue();
        return handle;
    }

    private static AdvancedGpuHandle AddKernel(
        AdvancedMaterialDatabase database,
        AdvancedGpuHandle layout)
    {
        AdvancedShadingKernelRecord record = new()
        {
            SupportedCoverageMask =
                (1u << (int)EAdvancedMaterialCoverageMode.Opaque) |
                (1u << (int)EAdvancedMaterialCoverageMode.Masked),
            SupportedEligibility =
                EAdvancedMaterialEligibilityFlags.NativeOpaque |
                EAdvancedMaterialEligibilityFlags.NativeMasked,
            SupportedFeatures =
                EAdvancedMaterialFeatureFlags.BaseColorTexture |
                EAdvancedMaterialFeatureFlags.ReceivesShadows,
            ShaderIdentityHash = 0xF00Dul,
        };
        database.TryAddKernel(layout, record, out AdvancedGpuHandle handle)
            .ShouldBeTrue();
        return handle;
    }

    private static AdvancedMaterialRecord CreateMaterialSource()
        => new()
        {
            RenderStateClass = EAdvancedMaterialRenderStateClass.OpaqueSingleSided,
            CoverageMode = EAdvancedMaterialCoverageMode.Opaque,
            FeatureFlags =
                EAdvancedMaterialFeatureFlags.BaseColorTexture |
                EAdvancedMaterialFeatureFlags.ReceivesShadows,
            EligibilityFlags = EAdvancedMaterialEligibilityFlags.NativeOpaque,
        };

    private static AdvancedMaterialValueDescriptor[] CreateValues()
        =>
        [
            new(0xA11ul, EAdvancedMaterialValueKind.Float, 1u),
            new(0xB22ul, EAdvancedMaterialValueKind.Texture, 1u),
        ];

    private static AdvancedMaterialTextureBinding CreateTextureBinding(
        uint textureIndex,
        uint samplerIndex)
        => new(
            new AdvancedTextureReference(
                new AdvancedGpuHandle(textureIndex, 1u),
                EAdvancedResourceFallback.White,
                0u),
            new AdvancedSamplerReference(
                new AdvancedGpuHandle(samplerIndex, 1u),
                EAdvancedResourceFallback.Zero,
                0u));
}
