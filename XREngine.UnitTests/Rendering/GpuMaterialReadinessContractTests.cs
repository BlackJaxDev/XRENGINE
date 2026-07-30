using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuMaterialReadinessContractTests
{
    [Test]
    public void ReadyResolution_RequiresANonFallbackBackendReference()
    {
        GPUMaterialTextureReference reference =
            GPUMaterialTextureReference.FromVulkanDescriptorIndex(7u);

        MaterialTextureReferenceResolution.Ready(reference, 13ul).IsReady.ShouldBeTrue();
        MaterialTextureReferenceResolution.Ready(GPUMaterialTextureReference.None).IsReady.ShouldBeFalse();
        MaterialTextureReferenceResolution.Pending("upload pending").IsReady.ShouldBeFalse();
        MaterialTextureReferenceResolution.Unsupported("unsupported").IsReady.ShouldBeFalse();
        MaterialTextureReferenceResolution.Failed("failed").IsReady.ShouldBeFalse();
    }

    [Test]
    public void GpuMaterialRows_AreSubmittedOnlyAfterDescriptorPublication()
    {
        string passSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string capabilitySource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Materials/IMaterialTableBackendCapability.cs");
        string vulkanTableSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.BindlessMaterialTextureTable.cs");
        string stateSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanBindlessMaterialTextureTableState.cs");

        capabilitySource.ShouldContain("MaterialTextureReferenceResolution ResolveMaterialTextureReference");
        stateSource.ShouldContain("internal sealed class VulkanBindlessMaterialTextureTableState");
        vulkanTableSource.ShouldContain("pendingPublication = slot.Dirty;");
        vulkanTableSource.ShouldContain("EMaterialTextureReferenceStatus.Pending");
        vulkanTableSource.ShouldContain("Descriptor index zero is reserved for shader fallback");
        passSource.ShouldContain("if (residencyStatus == EMaterialTextureReferenceStatus.Ready)");
        passSource.ShouldContain("flags |= 1u << 31;");
        passSource.ShouldContain("_skipGpuSubmissionThisPass = true;");
        passSource.ShouldContain("fallbackSubmittedRows: 0");
    }

    [Test]
    public void GpuMaterialPreparation_ReusesScratchAndPublishesReadinessTelemetry()
    {
        string passSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string tableSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Materials/GPUMaterialTable.cs");
        string measurementSource = SourceContractWorkspace.ReadFile(
            "Tools/Measure-GameLoopRenderPipeline.ps1");

        passSource.ShouldContain("_currentMaterialTableIdsScratch");
        passSource.ShouldNotContain("HashSet<uint> currentIds = [.. scene.MaterialMap.Keys]");
        tableSource.ShouldContain("if (MaterialStateMatches(materialID, entry, textureReferences))");
        tableSource.ShouldContain("return materialID;");
        measurementSource.ShouldContain("gpu_driven_required_material_rows");
        measurementSource.ShouldContain("gpu_driven_ready_material_rows");
        measurementSource.ShouldContain("gpu_driven_non_ready_material_texture_references");
        measurementSource.ShouldContain("gpu_driven_fallback_submitted_material_rows");
        measurementSource.ShouldContain("VulkanEligiblePrimaryRecordAllocatedBytesTotal");
        measurementSource.ShouldContain("VulkanGateRecordCommandBufferAllocatedBytesTotal");
    }

    [Test]
    public void BenchmarkMaterialScene_UsesAnAuthoredCheckerTexture()
    {
        string manifestSource = SourceContractWorkspace.ReadFile(
            "XREngine.UnitTests/TestData/Gltf/gltf-corpus.manifest.json");
        string cohortSource = SourceContractWorkspace.ReadFile(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/deferred-large-scene-materials.jsonc");
        string deferredCohortSource = SourceContractWorkspace.ReadFile(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/deferred-large-scene.jsonc");
        string uberCohortSource = SourceContractWorkspace.ReadFile(
            "XREngine.Benchmarks/VulkanPerformance/Cohorts/uber-large-scene.jsonc");
        string sceneSource = SourceContractWorkspace.ReadFile(
            "XREngine.UnitTests/TestData/Gltf/large-production-scene.gltf");

        manifestSource.ShouldContain("\"relativePath\": \"XREngine.UnitTests/TestData/Gltf/large-production-scene.gltf\"");
        manifestSource.ShouldContain("\"includeInPerformanceBaseline\": true");
        cohortSource.ShouldContain("\"Path\": \"XREngine.UnitTests/TestData/Gltf/large-production-scene.gltf\"");
        cohortSource.ShouldContain("\"CameraAntiAliasingModeOverride\": \"None\"");
        cohortSource.ShouldContain("\"Locomotion\": false");
        deferredCohortSource.ShouldContain("\"Locomotion\": false");
        uberCohortSource.ShouldContain("\"Locomotion\": false");
        sceneSource.ShouldContain("\"baseColorTexture\"");
        sceneSource.ShouldContain("\"uri\": \"checker.png\"");
    }

    [Test]
    public void BloomCopyMaterial_DeclaresAStableSourceTextureSlot()
    {
        string bloomSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs");

        bloomSource.ShouldContain("XRTexture sourceTexture = ResolveBloomCopySourceTexture(instance);");
        bloomSource.ShouldContain("CreateCopyMaterial(sourceTexture");
        bloomSource.ShouldContain("new XRTexture?[] { sourceTexture }");
        bloomSource.ShouldNotContain("material.Textures[0] = inputTexture;");
        bloomSource.ShouldNotContain("Array.Empty<XRTexture?>()");
    }

    [Test]
    public void OnTopForward_IsAnExplicitCpuDirectOverlayPass()
    {
        string pipelineSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.CommandChain.cs");

        pipelineSource.ShouldNotContain("EDefaultRenderPass.OnTopForward, MeshSubmissionStrategy");
        pipelineSource.ShouldContain(
            "EDefaultRenderPass.OnTopForward, EMeshSubmissionStrategy.CpuDirect");
    }

    [Test]
    public void VulkanIndirectDrawOperations_AreFramePooled()
    {
        string indirectSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectDraw.cs");
        string opSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/IndirectDrawOp.cs");
        string frameOpSource = SourceContractWorkspace.ReadFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/FrameOp.cs");

        indirectSource.ShouldNotContain("new IndirectDrawOp(");
        indirectSource.ShouldContain("IndirectDrawOp.Rent(");
        opSource.ShouldContain("TryRentForCurrentFrame(out IndirectDrawOp? reusable)");
        frameOpSource.ShouldContain("FramePool<IndirectDrawOp>.ReleaseCurrentThread();");
    }
}
