using NUnit.Framework;
using Shouldly;
using System;
using System.IO;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class DirectionalShadowAtlasFallbackTests
{
    [Test]
    public void ForwardDirectionalAtlas_KeepsLegacyFallbackTexturesBound()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("perLightShadowTex = FindDirectionalShadowReceiverTexture(dirLight);");
        source.ShouldNotContain("if (useDirectionalShadowAtlas)\n                    {\n                        forwardShadowTex = null;\n                    }");
    }

    [Test]
    public void DirectionalAtlasLegacyFallback_RequiresSampleablePage()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs");

        source.ShouldContain("TryGetDirectionalAtlasPageCount");
        source.ShouldContain("ShadowAtlas.TryGetPageTexture(");
        source.ShouldContain("slot.PageIndex < pageCount");
    }

    [Test]
    public void DeferredDirectionalCascadeAtlas_RequiresEveryCascadeSlot()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs");

        source.ShouldContain("AreRequiredDirectionalAtlasTilesSampleable");
        source.ShouldContain("packed.X == 0 || packed.Y < 0 || packed.Y >= maxPageCount");
        source.ShouldNotContain("HasAnyDirectionalAtlasTileSampleable");
    }

    [Test]
    public void DirectionalPrimaryAtlas_UsesReusableStaleTileFallback()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");
        string submitBody = ExtractRegion(
            source,
            "private void SubmitDirectionalShadowAtlasRequests()",
            "private void SubmitDirectionalCascadeShadowAtlasRequests");
        string primarySubmit = ExtractRegion(
            submitBody,
            "EShadowProjectionType.DirectionalPrimary",
            "encoding: encoding);");

        primarySubmit.ShouldContain("fallback: ShadowFallbackMode.StaleTile");
        primarySubmit.ShouldNotContain("fallback: ShadowFallbackMode.Legacy");
    }

    [Test]
    public void DeferredDirectionalAtlasFallback_KeepsLegacyPrimarySamplerBound()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("materialProgram.Sampler(\"ShadowMap\", selectedShadowMap is XRTexture2D shadow2D ? shadow2D : DummyShadowMap, 4);");
        source.ShouldNotContain("materialProgram.Sampler(\"ShadowMap\", !useDirectionalShadowAtlas && selectedShadowMap is XRTexture2D shadow2D ? shadow2D : DummyShadowMap, 4);");
    }

    [Test]
    public void DeferredDirectionalPrimaryAtlas_UsesRuntimeLayerCount()
    {
        string source = ReadRepoFile("Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs")
            .Replace("\r\n", "\n");
        string readPrimaryBody = ExtractRegion(
            source,
            "float ReadShadowMap2D",
            "float SampleDirectionalAtlasPage");

        readPrimaryBody.ShouldContain("atlasI0.y < textureSize(DirectionalShadowAtlas, 0).z");
        readPrimaryBody.ShouldNotContain("atlasI0.y < 2");
        readPrimaryBody.ShouldNotContain("return contact;");
    }

    [Test]
    public void ClearingCascadeBounds_LeavesPrimaryDirectionalAtlasSlotIntact()
    {
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");

        string clearCascadeShadowsBody = ExtractRegion(
            directionalSource,
            "internal void ClearCascadeShadows()",
            "internal void UpdateCascadeShadows");

        clearCascadeShadowsBody.ShouldContain("ClearCascadeShadows(ShadowRequestSource.Desktop);");
        clearCascadeShadowsBody.ShouldContain("ClearCascadeShadows(ShadowRequestSource.Hmd);");
        clearCascadeShadowsBody.ShouldNotContain("_primaryAtlasSlot = default;");
        directionalSource.ShouldContain("Array.Clear(state.AtlasSlots);");
        directionalSource.ShouldContain("internal void ClearDirectionalAtlasSlots()");
        lightsSource.ShouldContain("DynamicDirectionalLights[i].BeginDirectionalAtlasSlotPublish();");
    }

    [Test]
    public void DirectionalAtlasTileRendering_PreservesPrePushedRenderArea()
    {
        string renderStateSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs")
            .Replace("\r\n", "\n");
        string shadowPipelineSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/ShadowRenderPipeline.cs")
            .Replace("\r\n", "\n");

        string initialRenderArea = ExtractRegion(
            renderStateSource,
            "private bool PushInitialMainRenderArea",
            "private void PushRequiredRenderArea");

        initialRenderArea.ShouldContain("viewport?.RenderPipeline is ShadowRenderPipeline { PreserveExistingRenderArea: true }");
        initialRenderArea.ShouldContain("CurrentRenderRegion.Width > 0");
        initialRenderArea.ShouldContain("return false;");
        shadowPipelineSource.ShouldContain("Keeps an atlas tile render area intact");
        shadowPipelineSource.ShouldContain("VPRC_PushShadowOutputFBORenderArea");
    }

    [Test]
    public void DirectionalCascadeAtlasStaleTiles_PreserveRenderedUniformState()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("PreviousAtlasSlots");
        source.ShouldContain("BeginDirectionalAtlasSlotPublish");
        source.ShouldContain("ShouldPreserveStaleCascadeAtlasUniformData");
        source.ShouldNotContain("previous.ContentVersion == allocation.ContentVersion");
        source.ShouldContain("ContentVersion = previous.ContentVersion");
        source.ShouldContain("LastRenderedFrame = previous.LastRenderedFrame");
        source.ShouldContain("allocation.ActiveFallback is ShadowFallbackMode.StaleTile or ShadowFallbackMode.None");
        source.ShouldContain("atlasSlot.HasCascadeUniformData");
        source.ShouldContain("CopyPublishedRenderedCascadeUniformData");
        source.ShouldContain("splits[i] = atlasSlot.SplitFarDistance;");
        source.ShouldContain("matrices[i] = atlasSlot.WorldToLightSpaceMatrix;");
        source.ShouldContain("staleAges[i] = ResolveRenderedCascadeStaleAge(frameId, atlasSlot.LastRenderedFrame);");
    }

    [Test]
    public void TogglingDirectionalCascades_InvalidatesPublishedAtlasState()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs")
            .Replace("\r\n", "\n");

        string toggleBody = ExtractRegion(
            source,
            "case nameof(EnableCascadedShadows):",
            "case nameof(ShadowMapStorageFormat):");

        toggleBody.ShouldContain("ClearDirectionalAtlasSlots();");
        toggleBody.ShouldContain("EnsureCascadeShadowResources();");
        toggleBody.ShouldContain("ClearCascadeShadows();");
        toggleBody.ShouldContain("EnsureShadowMapForActiveDynamicLight();");
    }

    [Test]
    public void GroupedDirectionalCascadeAtlasRender_AdvancesPastRenderedGroupMembers()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");

        source.ShouldContain("int lastGroupRequestIndex = FindLastDirectionalCascadeGroupRequestIndex(directionalGroup, i);");
        source.ShouldContain("lastGroupRequestIndex,");
        source.ShouldContain("i = Math.Max(i, lastGroupRequestIndex);");
        source.ShouldContain("private int FindLastDirectionalCascadeGroupRequestIndex(");
    }

    [Test]
    public void VulkanDynamicFramebufferTransitions_UseOrderedAttachmentTargets()
    {
        string framebufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string commandBufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        framebufferSource.ShouldContain("private AttachmentTargetInfo[]? _attachmentTargets;");
        framebufferSource.ShouldContain("internal bool TryGetAttachmentTarget(");
        framebufferSource.ShouldContain("targetInfos[i] = attachments[i].TargetInfo;");
        commandBufferSource.ShouldContain("vkFbo.TryGetAttachmentTarget(");
        commandBufferSource.ShouldNotContain("var (target, _, mipLevel, layerIndex) = targets[i];");
        commandBufferSource.ShouldNotContain("foreach (var (target, _, mipLevel, layerIndex) in targets)");
    }

    [Test]
    public void VulkanLayeredFramebuffer_UsesAttachmentLayerCountForTextureArrays()
    {
        string framebufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Framebuffers/VkFrameBuffer.cs");
        string commandBufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        framebufferSource.ShouldContain("public uint FramebufferLayers { get; private set; } = 1u;");
        framebufferSource.ShouldContain("uint framebufferLayers = ResolveFramebufferLayers(attachments);");
        framebufferSource.ShouldContain("FramebufferLayers = state.FramebufferLayers;");
        framebufferSource.ShouldContain("Layers = framebufferLayers");
        framebufferSource.ShouldContain("layerIndex < 0");
        framebufferSource.ShouldContain("Math.Max(source.DescriptorArrayLayers, 1u)");
        commandBufferSource.ShouldContain("ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask)");
        commandBufferSource.ShouldContain("LayerCount = plan.LayerCount");
        commandBufferSource.ShouldContain("ResolveClearRectLayerCount(op.Target, clearTargetFrameBuffer, activeRenderLayerCount, activeRenderViewMask)");
    }

    [Test]
    public void VulkanDeferredShadowDraws_CaptureLayeredShadowUniformState()
    {
        string meshRendererSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string drawingSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string renderStateSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderState.cs");
        string resolverSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/MeshRenderMaterialResolver.cs");

        meshRendererSource.ShouldContain("LayeredShadowUniformState ShadowUniformState");
        meshRendererSource.ShouldContain("LayeredShadowUniformState.CaptureFromCurrentRenderingState()");
        meshRendererSource.ShouldContain("CaptureProgramBindingSnapshot(effectiveMaterial, shadowUniformState)");
        drawingSource.ShouldContain("Renderer.SetMaterialUniforms(material, programData, draw.ShadowUniformState);");
        drawingSource.ShouldContain("MeshRenderMaterialResolver.ApplyShadowUniforms(programData, material, draw.ShadowUniformState);");
        renderStateSource.ShouldContain("SetMaterialUniforms(material, program, LayeredShadowUniformState.CaptureFromCurrentRenderingState())");
        renderStateSource.ShouldContain("if (shadowState.IsShadowPass)");
        resolverSource.ShouldContain("public struct LayeredShadowUniformState");
        resolverSource.ShouldContain("ApplyShadowUniforms(XRRenderProgram program, XRMaterial material, in LayeredShadowUniformState shadowState)");
    }

    [Test]
    public void GroupedDirectionalCascadeAtlasFailure_RendersSequentialAtlasTiles()
    {
        string atlasManagerSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs")
            .Replace("\r\n", "\n");

        atlasManagerSource.ShouldContain("TryRenderDirectionalCascadeGroupSequentially");
        atlasManagerSource.ShouldContain("TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)");
        atlasManagerSource.ShouldNotContain("CanRenderDirectionalCascadeGroup(request, group)");
        atlasManagerSource.ShouldContain("light.CanRenderGroupedCascadeShadowAtlasTiles(group)");
        atlasManagerSource.ShouldContain("usedSequentialFallback = TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow);");
        atlasManagerSource.ShouldContain("light.RenderCascadeShadowAtlasTile(request.Key.Source, request.FaceOrCascadeIndex, page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow)");
        atlasManagerSource.ShouldContain("_directionalSequentialFallbackFrame = true;");
        atlasManagerSource.ShouldContain("FallbackReason: usedSequentialFallback ? \"GroupedAtlasRenderFailed\" : light.CascadeShadowRenderFallbackReason");
        atlasManagerSource.ShouldContain("sequential fallback also failed, leaving atlas tiles stale.");
        atlasManagerSource.ShouldNotContain("leaving atlas tiles stale instead of falling back to sequential.");
    }

    private static string ReadRepoFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "XREngine.slnx");
            if (File.Exists(candidate))
                return File.ReadAllText(Path.Combine(directory, relativePath));

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository root from test directory.");
    }

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        return source[start..end];
    }
}
