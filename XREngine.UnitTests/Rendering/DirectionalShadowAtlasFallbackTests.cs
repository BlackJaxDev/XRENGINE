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

        clearCascadeShadowsBody.ShouldContain("Array.Clear(_cascadeAtlasSlots);");
        clearCascadeShadowsBody.ShouldNotContain("_primaryAtlasSlot = default;");
        directionalSource.ShouldContain("internal void ClearDirectionalAtlasSlots()");
        lightsSource.ShouldContain("DynamicDirectionalLights[i].ClearDirectionalAtlasSlots();");
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

        source.ShouldContain("int lastGroupRequestIndex = FindLastDirectionalCascadeGroupRequestIndex(group, i);");
        source.ShouldContain("int nextRequestIndex = lastGroupRequestIndex + 1;");
        source.ShouldContain("i = lastGroupRequestIndex;");
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
        framebufferSource.ShouldContain("FramebufferLayers = ResolveFramebufferLayers(attachments);");
        framebufferSource.ShouldContain("Layers = FramebufferLayers");
        framebufferSource.ShouldContain("layerIndex < 0");
        framebufferSource.ShouldContain("Math.Max(source.DescriptorArrayLayers, 1u)");
        commandBufferSource.ShouldContain("LayerCount = Math.Max(vkFrameBuffer.FramebufferLayers, 1u)");
        commandBufferSource.ShouldContain("clearLayerCount = op.Target is null");
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
        atlasManagerSource.ShouldContain("usedSequentialFallback = TryRenderDirectionalCascadeGroupSequentially(light, group, collectVisibleNow);");
        atlasManagerSource.ShouldContain("light.RenderCascadeShadowAtlasTile(request.FaceOrCascadeIndex, page.FrameBuffer, allocation.InnerPixelRect, collectVisibleNow)");
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
