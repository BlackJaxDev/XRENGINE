using System;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanZeroReadbackSubmissionContractTests
{
    [Test]
    public void ProductionDefaults_SelectCompactBindlessMaterialTable()
    {
        foreach (string path in new[]
        {
            "XRENGINE/Settings/EditorPreferences.cs",
            "XREngine.Runtime.Rendering/Runtime/RuntimeEffectiveSettings.cs",
            "XREngine.Runtime.Rendering/Runtime/RuntimeDebugPreferences.cs",
            "XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs",
        })
        {
            string source = Read(path);
            source.ShouldContain(
                "EZeroReadbackMaterialDrawPath.BindlessMaterialTable");
        }

        EZeroReadbackMaterialDrawPath.FullBucketScanDiagnostic
            .ShouldNotBe(EZeroReadbackMaterialDrawPath.BindlessMaterialTable);
        EZeroReadbackMaterialDrawPath.ActiveBucketListReadbackDiagnostic
            .ShouldNotBe(EZeroReadbackMaterialDrawPath.BindlessMaterialTable);
    }

    [Test]
    public void CompactProductionSubmission_UsesThreeGpuOwnedTierGroups()
    {
        string source = Read("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string body = Extract(
            source,
            "private void RenderZeroReadbackMaterialTableBuckets(",
            "private static void RejectCompactMaterialTableSubmission(");

        body.ShouldContain("renderPasses.MaterialTierBucketCount != GPUBatchingBindings.MaterialTierCount");
        body.ShouldContain("for (uint tier = 0u; tier < GPUBatchingBindings.MaterialTierCount; ++tier)");
        body.ShouldContain("nuint countByteOffset = (nuint)(tier * sizeof(uint));");
        body.ShouldContain("allowMaxDrawFallback: false");
        body.ShouldContain("EMemoryBarrierMask.ShaderStorage");
        body.ShouldContain("EMemoryBarrierMask.Command");
        body.ShouldContain("RecordMaterialTopology(");
        body.ShouldContain("UseDepthNormalMaterialVariants");
        body.ShouldContain("maskedDepthNormalPrePass");
        body.ShouldNotContain("ReadActiveMaterialTierBuckets");
        body.ShouldNotContain("RenderZeroReadbackMaterialTiers");
        body.ShouldNotContain("foreach (uint bucketIndex");
        body.ShouldNotContain("ClientMappedBuffer");
        body.ShouldNotContain("allowMaxDrawFallback: true");
    }

    [Test]
    public void CompactProductionBuffers_DoNotAllocateCpuActiveBucketLists()
    {
        string source = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");
        string indirectSource = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");

        source.ShouldContain("UsesCompactMaterialTableSubmission");
        source.ShouldContain("? GPUBatchingBindings.MaterialTierCount");
        source.ShouldContain("if (RequiresActiveMaterialBucketList(ZeroReadbackMaterialDrawPath))");
        indirectSource.ShouldContain(
            "path == EZeroReadbackMaterialDrawPath.ActiveBucketListReadbackDiagnostic");
        indirectSource.ShouldContain("\"CompactMaterialTableOutput\"");
        indirectSource.ShouldContain(
            "UsesCompactMaterialTableSubmission(ZeroReadbackMaterialDrawPath) ? 1u : 0u");
    }

    [Test]
    public void CompactShader_UsesOneClampedReservationPerWorkgroupAndTier()
    {
        string shader = Read(
            "Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp");
        string compactBody = Extract(
            shader,
            "uint ReserveClampedGroupSpan(",
            "void main()");

        shader.ShouldContain("shared uint compactScan[MATERIAL_TIER_COUNT][64];");
        shader.ShouldContain("uniform uint CompactMaterialTableOutput;");
        shader.ShouldContain("? tier\n        : slotIndex * MATERIAL_TIER_COUNT + tier;");
        compactBody.ShouldContain("uint accepted = min(requested, available);");
        compactBody.ShouldContain("atomicCompSwap(drawCounts[tier], current, desired)");
        compactBody.ShouldContain("compactAccepted[tier] = accepted;");
        compactBody.ShouldContain("if (accepted != requested)");
        compactBody.ShouldContain("atomicOr(overflowFlag, 1u);");
        compactBody.ShouldNotContain("TryReserveBucketDraw(candidate.BucketIndex");
    }

    [Test]
    public void UnsupportedCompactCapability_IsVisibleAndNeverFallsBackToCpu()
    {
        string source = Read("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string body = Extract(
            source,
            "private static void RejectCompactMaterialTableSubmission(",
            "private XRMeshRenderer? ConfigureIndirectRendererForTier(");

        body.ShouldContain("EMaterialTextureBindingRung.Unsupported");
        body.ShouldContain("RecordForbiddenGpuFallback(1)");
        body.ShouldContain("RenderingWarningEvery(");
        body.ShouldNotContain("RenderTraditional(");
        body.ShouldNotContain("RenderZeroReadbackMaterialTiers(");
    }

    [Test]
    public void BindingRungAndOptionalVisibilityBoundary_AreReportedContracts()
    {
        string rung = Read("XREngine.Data/Rendering/Enums/EMaterialTextureBindingRung.cs");
        string stats = Read(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.GpuDriven.cs");
        string profile = Read("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string visibility = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/IGpuCompactVisibilityInput.cs");

        rung.ShouldContain("Bindless");
        rung.ShouldContain("TextureArray");
        rung.ShouldContain("CoarseBucket");
        stats.ShouldContain("UpdateMaterialBindingRung(");
        stats.ShouldContain("UpdateGpuCompactionRung(");
        profile.ShouldContain("\"gpu_material_binding_rung\"");
        profile.ShouldContain("\"gpu_compaction_rung\"");
        profile.ShouldContain("\"gpu_driven_configured_material_slots\"");
        profile.ShouldContain("\"gpu_driven_material_pass_groups\"");
        profile.ShouldContain("\"gpu_driven_unsupported_compact_passes\"");
        visibility.ShouldContain("XRDataBuffer CommandIds");
        visibility.ShouldContain("XRDataBuffer CommandCount");
        visibility.ShouldContain("IsConservativeBypass");
    }

    [Test]
    public void ForwardDepthNormalPrepass_HasACompactMaterialTableVariant()
    {
        string source = Read("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string shaderFactory = Extract(
            source,
            "private static XRShader CreateMaterialTableFragmentShader(",
            "private static XRShader CreateMeshletMaterialTableFragmentShader(");

        shaderFactory.ShouldContain("GPUIndirect_MaterialTableDepthNormalFS");
        shaderFactory.ShouldContain("layout(location=0) out vec2 Normal;");
        shaderFactory.ShouldContain("SampleBindlessTexture(material.NormalHandleIndex");
        shaderFactory.ShouldContain("SampleBindlessTexture(material.AlbedoHandleIndex");
        shaderFactory.ShouldContain("discard;");
        shaderFactory.ShouldContain("XRENGINE_EncodeNormal(worldNormal)");
    }

    [Test]
    public void ValidationCapacityMultiplier_IsBoundedAndSaturating()
    {
        GpuDrivenValidationCapacity.ResolveMultiplier(null).ShouldBe(1u);
        GpuDrivenValidationCapacity.ResolveMultiplier("1").ShouldBe(1u);
        GpuDrivenValidationCapacity.ResolveMultiplier("4").ShouldBe(4u);
        GpuDrivenValidationCapacity.ResolveMultiplier("16").ShouldBe(16u);
        GpuDrivenValidationCapacity.Scale(49u, 4u).ShouldBe(196u);
        GpuDrivenValidationCapacity.Scale(uint.MaxValue, 16u)
            .ShouldBe((uint)int.MaxValue);

        Should.Throw<InvalidOperationException>(
            () => GpuDrivenValidationCapacity.ResolveMultiplier("2"));
    }

    [Test]
    public void CapacityValidation_InflatesAllocationWithoutInflatingActiveTopology()
    {
        string environment = Read(
            "XREngine.Data/Environment/XREngineEnvironmentVariables.cs");
        string scene = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs");
        string scatter = Read(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs");
        string manager = Read(
            "XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string profile = Read("XRENGINE/Engine/Engine.ProfileCapture.cs");

        environment.ShouldContain(
            "XRE_GPU_DRIVEN_VALIDATION_CAPACITY_MULTIPLIER");
        scene.ShouldContain(
            "GpuDrivenValidationCapacity.Apply(UpdatingCommandCount + (uint)subMeshes.Length)");
        scene.ShouldContain("if (nextPowerOfTwo <= currentCapacity)");
        scatter.ShouldContain(
            "GpuDrivenValidationCapacity.Apply(Math.Max(materialSlotLookupCount, 1u))");
        manager.ShouldContain(
            "checked((int)scene.TotalCommandCount)");
        manager.ShouldContain(
            "renderPasses.MaterialSlotIds.Count");
        profile.ShouldContain("\"gpu_driven_command_capacity\"");
        profile.ShouldContain("\"gpu_driven_active_command_count\"");
        profile.ShouldContain("\"gpu_driven_material_lookup_capacity\"");
        profile.ShouldContain("\"gpu_driven_active_material_slots\"");
        profile.ShouldContain(
            "\"gpu_driven_validation_capacity_multiplier\"");
    }

    [Test]
    public void VulkanIntegerAttachmentReadback_ProducesExactCaptureFingerprint()
    {
        string blit = Read(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string capture = Read(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.RenderPipeline.cs");

        blit.ShouldContain("case Format.R32Uint:");
        blit.ShouldContain("float value = *(uint*)(src + srcIndex);");
        blit.ShouldContain("Format.R32Uint => 4");
        capture.ShouldContain("ComputeRgbaFloatSha256(rgbaFloats)");
        capture.ShouldContain("rgba_float_sha256 = result.RgbaFloatSha256");
    }

    private static string Read(string relativePath)
        => global::XREngine.UnitTests.SourceContractWorkspace
            .ReadFile(relativePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Extract(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        startIndex.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker '{start}'.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        endIndex.ShouldBeGreaterThan(startIndex, $"Missing end marker '{end}'.");
        return source[startIndex..endIndex];
    }
}
