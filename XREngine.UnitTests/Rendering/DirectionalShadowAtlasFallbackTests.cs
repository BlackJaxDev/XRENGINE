using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shadows;

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
    public void DeferredDirectionalCascadeAtlas_AcceptsExplicitFallbackSlots()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs");

        source.ShouldContain("AreRequiredDirectionalAtlasSlotsUsable");
        source.ShouldContain("AnyRequiredDirectionalAtlasSlotSamplesPage");
        source.ShouldContain("HasExplicitNonLegacyDirectionalAtlasFallback");
        source.ShouldContain("packed.Z > 0 && packed.Z != (int)ShadowFallbackMode.Legacy");
        source.ShouldContain("DirectionalShadowAtlasEnabled\", hasUsableAtlasState");
        source.ShouldNotContain("packed.X == 0 || packed.Y < 0 || packed.Y >= maxPageCount");
        source.ShouldNotContain("HasAnyDirectionalAtlasTileSampleable");
    }

    [Test]
    public void ForwardDirectionalCascadeAtlas_AcceptsExplicitFallbackSlots()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");

        source.ShouldContain("AreRequiredDirectionalAtlasSlotsUsable");
        source.ShouldContain("AnyRequiredDirectionalAtlasSlotSamplesPage");
        source.ShouldContain("HasExplicitNonLegacyDirectionalAtlasFallback");
        source.ShouldContain("packed.Z > 0 && packed.Z != (int)ShadowFallbackMode.Legacy");
        source.ShouldContain("!needsDirectionalAtlasTexture ||");
        source.ShouldNotContain("packed.X == 0 || packed.Y < 0 || packed.Y >= maxPageCount");
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
    public void DirectionalPrimaryAtlas_RemainsSharedBecauseItsCameraIsLightOwned()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");
        string submitBody = ExtractRegion(
            source,
            "private void SubmitDirectionalShadowAtlasRequests()",
            "private void SubmitDirectionalCascadeShadowAtlasRequests");
        string primarySubmit = ExtractRegion(
            submitBody,
            "if (submitPrimary && light.ShadowCamera is XRCamera primaryCamera)",
            "            }\n        }");

        primarySubmit.ShouldContain("EShadowProjectionType.DirectionalPrimary");
        primarySubmit.ShouldNotContain("source:");
    }

    [Test]
    public void DirectionalPrimaryAtlas_UnwrittenPublicationPreservesMatchingResidentGeneration()
    {
        ShadowAtlasAllocation allocation = CreatePrimaryAllocation();
        DirectionalLightComponent.DirectionalCascadeAtlasSlot previous = CreatePrimarySlot(allocation) with
        {
            Fallback = ShadowFallbackMode.None,
        };
        allocation = allocation with { ActiveFallback = ShadowFallbackMode.StaleTile };

        bool preserved = DirectionalLightComponent.TryRefreshPreservedPrimaryAtlasSlot(
            previous,
            allocation,
            out DirectionalLightComponent.DirectionalCascadeAtlasSlot refreshed);

        preserved.ShouldBeTrue();
        refreshed.ShouldBe(previous with { Fallback = ShadowFallbackMode.StaleTile });
    }

    [Test]
    public void DirectionalPrimaryAtlas_UnwrittenPublicationRejectsReallocatedOrNewContent()
    {
        ShadowAtlasAllocation allocation = CreatePrimaryAllocation();
        DirectionalLightComponent.DirectionalCascadeAtlasSlot previous = CreatePrimarySlot(allocation);

        DirectionalLightComponent.TryRefreshPreservedPrimaryAtlasSlot(
            previous,
            allocation with { AtlasId = allocation.AtlasId + 1 },
            out _).ShouldBeFalse();
        DirectionalLightComponent.TryRefreshPreservedPrimaryAtlasSlot(
            previous,
            allocation with { ContentVersion = allocation.ContentVersion + 1 },
            out _).ShouldBeFalse();
        DirectionalLightComponent.TryRefreshPreservedPrimaryAtlasSlot(
            previous,
            allocation with { IsResident = false },
            out _).ShouldBeFalse();
    }

    [Test]
    public void DirectionalPrimaryAtlas_PublicationDistinguishesOmissionFromExplicitClear()
    {
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");

        directionalSource.ShouldContain("_previousPrimaryAtlasSlot = _primaryAtlasSlot;");
        directionalSource.ShouldContain("_pendingPrimaryAtlasSlotWritten = false;");
        directionalSource.ShouldContain("if (_pendingPrimaryAtlasSlotWritten)");
        directionalSource.ShouldContain("TryRefreshPreservedPrimaryAtlasSlot");
        directionalSource.ShouldContain("shadowAtlas.TryGetPlanningAllocation(_previousPrimaryAtlasSlot.Key");
        lightsSource.ShouldContain("CompleteDirectionalAtlasSlotPublish(publishDirectionalSlots, ShadowAtlas)");

        string clearBody = ExtractRegion(
            directionalSource,
            "internal void ClearDirectionalAtlasSlots()",
            "internal void ApplyDirectionalShadowAtlasMode");
        clearBody.ShouldContain("_primaryAtlasSlot = default;");
        clearBody.ShouldContain("_previousPrimaryAtlasSlot = default;");
    }

    [Test]
    public void DirectionalShadowAtlas_ContentHashTracksPublishedCasterMembershipAndState()
    {
        string commandCollectionSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs")
            .Replace("\r\n", "\n");
        string directionalSource = ReadRepoFile("XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs")
            .Replace("\r\n", "\n");
        string lightsSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs")
            .Replace("\r\n", "\n");

        commandCollectionSource.ShouldContain("_renderingShadowCasterCommandSetSignature = ComputeShadowCasterCommandSetSignature();");
        commandCollectionSource.ShouldContain("internal ulong ShadowCasterCommandSetSignature");
        commandCollectionSource.ShouldContain("AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueDeferred);");
        commandCollectionSource.ShouldContain("AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueForward);");
        commandCollectionSource.ShouldContain("AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.MaskedForward);");
        commandCollectionSource.ShouldContain("ComputeShadowCasterPassContentSignature(commands)");
        commandCollectionSource.ShouldContain("ComputeShadowCasterCommandStateSignature(command)");
        commandCollectionSource.ShouldContain("AddShadowState(ref hash, meshCommand.WorldMatrix);");
        commandCollectionSource.ShouldContain("if (command.CullingVolume is AABB bounds)");
        directionalSource.ShouldContain("GetShadowCasterCommandSetSignature(");
        directionalSource.ShouldContain("PrimaryShadowViewport.RenderPipelineInstance.MeshRenderCommands.ShadowCasterCommandSetSignature");
        lightsSource.ShouldContain("directionalLight.GetShadowCasterCommandSetSignature(");
    }

    [Test]
    public void DirectionalShadowAtlas_CasterMotionChangesContentSignature()
    {
        RenderCommandMesh3D command = new(EDefaultRenderPass.OpaqueDeferred)
        {
            WorldMatrix = Matrix4x4.Identity,
        };
        ulong initial = RenderCommandCollection.ComputeShadowCasterCommandStateSignature(command);

        command.WorldMatrix = Matrix4x4.CreateTranslation(3.0f, -2.0f, 7.0f);
        ulong moved = RenderCommandCollection.ComputeShadowCasterCommandStateSignature(command);

        moved.ShouldNotBe(initial);
    }

    [Test]
    public void ShadowViewport_IsExcludedFromPresentationOutputLedger()
    {
        string source = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/XRViewport.cs")
            .Replace("\r\n", "\n");
        string recordBody = ExtractRegion(
            source,
            "private void RecordFrameOutput(",
            "private static ulong MixFrameOutputIdentity");

        recordBody.ShouldContain("if (_renderPipeline.IsShadowPipeline)");
        recordBody.ShouldContain("return;");
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
        source.ShouldContain("ShouldPreserveCascadeAtlasUniformData");
        source.ShouldContain("previous.PageIndex != allocation.PageIndex");
        source.ShouldContain("RefreshStaleAtlasSlotAllocation");
        source.ShouldContain("ContentVersion = previous.ContentVersion");
        source.ShouldContain("LastRenderedFrame = previous.LastRenderedFrame");
        source.ShouldContain("or ShadowFallbackMode.ContactOnly");
        source.ShouldContain("ResolvePreservedCascadeFallback");
        source.ShouldContain("ResolveStaleCascadeFallback");
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
        string commandBufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

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
        string commandBufferSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

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
        string shadowStateSource = ReadRepoFile("XREngine.Runtime.Rendering/Rendering/LayeredShadowUniformState.cs");

        meshRendererSource.ShouldContain("LayeredShadowUniformState ShadowUniformState");
        meshRendererSource.ShouldContain("LayeredShadowUniformState.CaptureFromCurrentRenderingState()");
        meshRendererSource.ShouldContain("CaptureProgramBindingSnapshot(effectiveMaterial, shadowUniformState)");
        drawingSource.ShouldContain("Renderer.SetMaterialUniforms(material, programData, draw.ShadowUniformState);");
        drawingSource.ShouldContain("MeshRenderMaterialResolver.ApplyShadowUniforms(programData, material, draw.ShadowUniformState);");
        renderStateSource.ShouldContain("SetMaterialUniforms(material, program, LayeredShadowUniformState.CaptureFromCurrentRenderingState())");
        renderStateSource.ShouldContain("if (shadowState.IsShadowPass)");
        shadowStateSource.ShouldContain("public struct LayeredShadowUniformState");
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

    private static ShadowAtlasAllocation CreatePrimaryAllocation()
    {
        ShadowRequestKey key = new(
            Guid.NewGuid(),
            ShadowRequestDomain.Live,
            ShadowRequestSource.Default,
            EShadowProjectionType.DirectionalPrimary,
            0,
            EShadowMapEncoding.Depth);
        BoundingRectangle pixelRect = new(32, 64, 512, 512);
        BoundingRectangle innerPixelRect = new(34, 66, 508, 508);
        return new ShadowAtlasAllocation(
            key,
            EShadowAtlasKind.Directional,
            AtlasId: 7,
            PageIndex: 1,
            pixelRect,
            innerPixelRect,
            new Vector4(0.25f, 0.25f, 0.5f, 0.5f),
            Resolution: 508u,
            LodLevel: 0,
            ContentVersion: 42u,
            LastRenderedFrame: 100u,
            IsResident: true,
            IsStaticCacheBacked: false,
            ActiveFallback: ShadowFallbackMode.None,
            SkipReason.None);
    }

    private static DirectionalLightComponent.DirectionalCascadeAtlasSlot CreatePrimarySlot(
        in ShadowAtlasAllocation allocation)
        => new(
            HasAllocation: true,
            IsResident: allocation.IsResident,
            Key: allocation.Key,
            AtlasId: allocation.AtlasId,
            PageIndex: allocation.PageIndex,
            RecordIndex: 3,
            UvScaleBias: allocation.UvScaleBias,
            NearPlane: 0.1f,
            FarPlane: 100.0f,
            TexelSize: 1.0f / allocation.Resolution,
            ResolutionScale: 1.0f,
            Resolution: allocation.Resolution,
            Fallback: allocation.ActiveFallback,
            PixelRect: allocation.PixelRect,
            InnerPixelRect: allocation.InnerPixelRect,
            LastRenderedFrame: allocation.LastRenderedFrame,
            ContentVersion: allocation.ContentVersion,
            HasCascadeUniformData: false,
            SplitFarDistance: 100.0f,
            BlendWidth: 0.0f,
            BiasMin: 0.0f,
            BiasMax: 1.0f,
            ReceiverOffset: 0.0f,
            WorldToLightSpaceMatrix: Matrix4x4.Identity);

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        return source[start..end];
    }
}
