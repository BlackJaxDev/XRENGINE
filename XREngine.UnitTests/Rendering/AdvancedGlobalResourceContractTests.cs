using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedGlobalResourceContractTests
{
    [Test]
    public void GlobalRecords_HaveStableStd430CompatiblePacking()
    {
        Should.NotThrow(AdvancedShaderRecordLayout.ValidateCpuLayouts);
        Marshal.SizeOf<AdvancedViewRecord>().ShouldBe(896);
        Marshal.SizeOf<AdvancedLightRecord>().ShouldBe(128);
        Marshal.SizeOf<AdvancedShadowRecord>().ShouldBe(224);
        Marshal.SizeOf<AdvancedProbeRecord>().ShouldBe(176);
        Marshal.SizeOf<AdvancedEnvironmentRecord>().ShouldBe(128);
        Marshal.SizeOf<AdvancedDecalRecord>().ShouldBe(192);
        Marshal.SizeOf<AdvancedGiResourceRecord>().ShouldBe(208);
        Marshal.SizeOf<AdvancedTextureRecord>().ShouldBe(64);
        Marshal.SizeOf<AdvancedSamplerRecord>().ShouldBe(64);
        Marshal.SizeOf<AdvancedGlobalResourceTableSet>().ShouldBe(112);
    }

    [Test]
    public void TextureReferenceEncoder_LowersEachBackendWithoutChangingLogicalIdentity()
    {
        AdvancedTextureReference logical = new(
            new AdvancedGpuHandle(7u, 3u),
            EAdvancedResourceFallback.FlatNormal,
            0u);
        AdvancedBackendTexturePayload payload = new(
            OpenGlBindlessHandle: 0xAABBCCDD11223344ul,
            VulkanDescriptorIndex: 13u,
            VulkanHeapResourceIndex: 14u,
            TextureArrayIndex: 15u,
            TextureArrayLayer: 16u,
            SamplerIndex: 17u,
            LogicalGeneration: 3u,
            Flags: EAdvancedResourceReferenceFlags.Resident);

        AdvancedResourceReferenceEncoder.EncodeTexture(
                EAdvancedTextureIndirectionMode.OpenGlBindlessHandles,
                logical,
                payload)
            .ShouldBe(new AdvancedEncodedTextureReference(
                0x11223344u,
                0xAABBCCDDu,
                17u,
                EAdvancedResourceReferenceFlags.Resident));
        AdvancedResourceReferenceEncoder.EncodeTexture(
                EAdvancedTextureIndirectionMode.TextureArray,
                logical,
                payload)
            .ShouldBe(new AdvancedEncodedTextureReference(
                15u,
                16u,
                17u,
                EAdvancedResourceReferenceFlags.Resident));
        AdvancedResourceReferenceEncoder.EncodeTexture(
                EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
                logical,
                payload)
            .ShouldBe(new AdvancedEncodedTextureReference(
                13u,
                17u,
                0u,
                EAdvancedResourceReferenceFlags.Resident));
        AdvancedResourceReferenceEncoder.EncodeTexture(
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap,
                logical,
                payload)
            .ShouldBe(new AdvancedEncodedTextureReference(
                14u,
                17u,
                0u,
                EAdvancedResourceReferenceFlags.Resident));
    }

    [Test]
    public void NonresidentAndStaleReferences_UseSlotZeroAndPublishDelayedDiagnostics()
    {
        AdvancedResourceResidencyDiagnostics diagnostics = new();
        AdvancedTextureReference logical = new(
            new AdvancedGpuHandle(7u, 3u),
            EAdvancedResourceFallback.White,
            0u);
        AdvancedBackendTexturePayload stalePayload = new(
            0ul,
            0u,
            0u,
            0u,
            0u,
            0u,
            LogicalGeneration: 2u,
            EAdvancedResourceReferenceFlags.Resident);
        AdvancedEncodedTextureReference encoded =
            AdvancedResourceReferenceEncoder.EncodeTexture(
                EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
                logical,
                stalePayload,
                diagnostics);

        encoded.Payload0.ShouldBe(AdvancedResourceReferenceEncoder.FallbackSlot);
        encoded.Payload2.ShouldBe((uint)EAdvancedResourceFallback.White);
        encoded.Flags.ShouldBe(
            EAdvancedResourceReferenceFlags.Fallback |
            EAdvancedResourceReferenceFlags.StaleGeneration);
        diagnostics.TryConsume(out _).ShouldBeFalse();

        diagnostics.PublishFrame(42ul);
        diagnostics.TryConsume(out AdvancedResourceResidencySnapshot snapshot)
            .ShouldBeTrue();
        snapshot.FrameId.ShouldBe(42ul);
        snapshot.TextureFallbacks.ShouldBe(1ul);
        snapshot.StaleTextureReferences.ShouldBe(1ul);
        diagnostics.TryConsume(out _).ShouldBeFalse();
    }

    [Test]
    public void GlobalTables_BindOncePerExactCompatibleCommandScope()
    {
        AdvancedGlobalResourceTableBinder binder = new();
        RecordingBindingBackend firstBackend = new(RuntimeGraphicsApiKind.Vulkan);
        RecordingBindingBackend secondBackend = new(RuntimeGraphicsApiKind.Vulkan);
        AdvancedGlobalResourceTableSet tables = CreateTableSet(
            new AdvancedGpuHandle(10u, 1u));

        binder.BindOnce(firstBackend, 100ul, tables).ShouldBeTrue();
        binder.BindOnce(firstBackend, 100ul, tables).ShouldBeFalse();
        firstBackend.BindCount.ShouldBe(1);

        AdvancedGlobalResourceTableSet changedHandles = tables with
        {
            Textures = new AdvancedGpuHandle(11u, 1u),
        };
        binder.BindOnce(firstBackend, 100ul, changedHandles).ShouldBeTrue();
        binder.BindOnce(secondBackend, 100ul, changedHandles).ShouldBeTrue();
        binder.BindOnce(secondBackend, 101ul, changedHandles).ShouldBeTrue();
        firstBackend.BindCount.ShouldBe(2);
        secondBackend.BindCount.ShouldBe(2);

        binder.Invalidate();
        binder.BindOnce(secondBackend, 101ul, changedHandles).ShouldBeTrue();
        secondBackend.BindCount.ShouldBe(3);
    }

    private static AdvancedGlobalResourceTableSet CreateTableSet(
        AdvancedGpuHandle textures)
        => new(
            Generation: 7ul,
            LayoutHash: 0xA5A5ul,
            TextureEncoding: EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing,
            Reserved: 0u,
            Views: new AdvancedGpuHandle(1u, 1u),
            Lights: new AdvancedGpuHandle(2u, 1u),
            Shadows: new AdvancedGpuHandle(3u, 1u),
            Probes: new AdvancedGpuHandle(4u, 1u),
            Environments: new AdvancedGpuHandle(5u, 1u),
            Decals: new AdvancedGpuHandle(6u, 1u),
            GiResources: new AdvancedGpuHandle(7u, 1u),
            Textures: textures,
            Samplers: new AdvancedGpuHandle(8u, 1u),
            EncodedTextures: new AdvancedGpuHandle(9u, 1u),
            EncodedSamplers: new AdvancedGpuHandle(10u, 1u));

    private sealed class RecordingBindingBackend(
        RuntimeGraphicsApiKind backend) : IAdvancedGlobalResourceTableBindingBackend
    {
        public RuntimeGraphicsApiKind Backend { get; } = backend;

        public int BindCount { get; private set; }

        public void BindGlobalResourceTables(in AdvancedGlobalResourceTableSet tables)
            => BindCount++;
    }
}
