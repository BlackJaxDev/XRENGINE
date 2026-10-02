# Renderer Validation Failures TODO

Status: Open — 50 failed test cases recorded; no fixes attempted in this document.
Last updated: 2026-10-02
Ownership: Rendering, Vulkan backend, shader contracts, and runtime asset loading, according to the affected test.

## Scope and evidence

This checklist records every failure from the two runs performed during the
[directional-shadow investigation](../../investigations/rendering/advanced-vulkan-dirlight-shadows-2026-10-02.md).
Counts describe these test invocations; no clean baseline run was performed to
prove which failures predate the shadow work. Source-text failures can indicate
outdated assertions or changed contracts and must not automatically be dismissed.

| Suite | Passed | Failed | Total |
|---|---:|---:|---:|
| Shadow / Advanced resource / shader access | 185 | 34 | 219 |
| Pipeline lifecycle / purpose / host services | 101 | 16 | 117 |

The isolated editor build passed with zero warnings and errors. Live shadow
recovery passed five off/on cycles (60, 80, 100, 120, and 140 seconds), and six
rendered/shadow-mask captures were inspected across three camera views. This
bounded runtime success does not close the failed automated cases below.

The shadow suite has 30 source-text/index failures and four numeric expectation
failures. The pipeline suite has two source-text assertion failures, three
exceptions, and eleven other assertion failures. An exception in a test helper
(for example a substring lookup) is not proof of a live renderer exception.
The task's ShadowAtlasManager changes only add decline diagnostics; its allocator
algorithms, UV calculation, and allocation-generation logic were not changed.

## Triage and closure

- [ ] Reproduce each failed case in isolation, then in its fixture, to check global-state/order dependence.
- [ ] Compare the intended contract, current implementation, and a clean baseline before assigning regression ownership.
- [ ] Resolve numeric/layout/generation and exception failures before broadly rewriting source-text assertions.
- [ ] For each source-text assertion, locate the current implementation and preserve the behavioral requirement; do not merely remove assertions to make the suite pass.
- [ ] Follow AGENTS.md's live-validation and test-edit sequencing when implementing follow-ups. This document records work; no test changes were made.
- [ ] Close each checkbox only after recording the resolution and a passing targeted rerun; rerun both full filters before closing the document.

## Separate runtime validation prerequisite

- [ ] Resolve strict NativeAOT parity rejection of XRShader construction in RuntimeThirdPartyAssetLoadingServices.Load, then repeat the Vulkan Advanced shadow checks without an override.

The default unit-world launch raised AotParityViolationException and blocked the
Advanced visibility shader family. The successful shadow validation used the
session-only `XRE_AOT_PARITY=off` override with `XRE_DIRECTIONAL_SHADOW_AUDIT=1`.
Vulkan validation layers were disabled; absence of VUID log lines does not certify
validation-layer correctness. The isolated editor was stopped after validation.

## Failed shadow / Advanced cases (34)

Each item retains the exact test case name, its test source, and the observed
failure. Resolve the four numeric expectations as well as the source contracts.

- [ ] `ConditionalShadowRegistriesRemainStableAndOutputScoped`
  Test source: [VulkanDesktopPlanStabilityTests.cs](../../../../XREngine.UnitTests/Rendering/VulkanDesktopPlanStabilityTests.cs).
  Observed: start should be greater than or equal to 0 but was -1 Additional Info: Missing method start 'private RenderResourceRegistry? BuildMergedFrameOpRegistry'.

- [ ] `DeferredDirectionalLightPass_BindsSafeShadowFallbacks_AfterLightReactivation`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: lightCombineSource should contain (case insensitive comparison) "public XRMeshRenderer? DirectionalLightRenderer { get; private set; }" — expected source fragment was absent.

- [ ] `DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)" — expected source fragment was absent.

- [ ] `DirectionalCascadeAtlasStaleTiles_PreserveRenderedUniformState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: source should contain (case insensitive comparison) "staleAges[i] = ResolveRenderedCascadeStaleAge(frameId, atlasSlot.LastRenderedFrame);" — expected source fragment was absent.

- [ ] `DirectionalCascadeInstancedPath_UsesVertexLayerContractAndMaterialFallbacks`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: generatorSource should contain (case insensitive comparison) "int xreCascadeLayer = gl_InstanceID % xreCascadeLayerCount;" — expected source fragment was absent.

- [ ] `DirectionalCascadeShaders_UsePerCascadeBiasAndReceiverOffset`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "float receiverOffset = atlasSampleAllowed ? light.RenderedCascadeReceiverOffsets[cascadeIndex] : light.CascadeReceiverOffsets[cascadeIndex];" — expected source fragment was absent.

- [ ] `DirectionalCascadeSourceCamera_PrefersPlayerAssociatedCascadedViewport`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "private bool HasActiveCascadedDirectionalShadowViewport(ShadowRequestSource source)" — expected source fragment was absent.

- [ ] `DirectionalDepthCascadeAtlasFallbacks_KeepReceiverArrayBoundOutsideVulkanAtlas`
  Test source: [ShadowMapMomentPhase2Tests.cs](../../../../XREngine.UnitTests/Rendering/ShadowMapMomentPhase2Tests.cs).
  Observed: directionalSource should contain (case insensitive comparison) "program.Uniform(names.CascadeSplits, cascadeSplits);" — expected source fragment was absent.

- [ ] `DirectionalLightComponent_PublishesPerCascadeBiasUniforms`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "CascadeBiasMin[{i}]" — expected source fragment was absent.

- [ ] `DirectionalPrimaryShadowAtlasShaders_DoNotUseLegacyMapWhenAtlasIsEnabled`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "ivec4 atlasI0 = DirectionalShadowAtlasPacked0[atlasRecordIndex];" — expected source fragment was absent.

- [ ] `DirectionalShadowAtlas_ContentHashTracksPublishedCasterMembershipAndState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: commandCollectionSource should contain (case insensitive comparison) "_renderingShadowCasterCommandSetSignature = ComputeShadowCasterCommandSetSignature();" — expected source fragment was absent.

- [ ] `ForwardLighting_BindsForwardPrePassContactShadowTextures`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "const int forwardContactDepthUnit = 26;" — expected source fragment was absent.

- [ ] `ForwardLightingSnippet_DeclaresCascadeShadowBindings`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "layout(binding = 17) uniform sampler2DArray DirectionalShadowMapArrays" — expected source fragment was absent.

- [ ] `ForwardLocalShadowMetadata_UsesStorageBuffers`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "layout(std430, binding = 37) readonly buffer ForwardPointShadowMetadataBuffer" — expected source fragment was absent.

- [ ] `GLMaterial_RebindsLightSamplerUniformsEveryBindingBatch`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "Light bindings include shadow-map samplers." — expected source fragment was absent.

- [ ] `GlobalRecords_HaveStableStd430CompatiblePacking`
  Test source: [AdvancedGlobalResourceContractTests.cs](../../../../XREngine.UnitTests/Rendering/AdvancedGlobalResourceContractTests.cs).
  Observed: Marshal.SizeOf&lt;AdvancedViewRecord&gt;() should be 896 but was 944

- [ ] `GroupedDirectionalCascadeAtlasFailure_RendersSequentialAtlasTiles`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)" — expected source fragment was absent.

- [ ] `GroupedDirectionalCascadeAtlasRender_AdvancesPastRenderedGroupMembers`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: source should contain (case insensitive comparison) "int lastGroupRequestIndex = FindLastDirectionalCascadeGroupRequestIndex(directionalGroup, i);" — expected source fragment was absent.

- [ ] `GroupedShadowAtlasPasses_ClearEachIndexedTileBeforeDrawing`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: shadowPipelineSource should contain (case insensitive comparison) "RuntimeEngine.Rendering.State.ClearByBoundFBO();" — expected source fragment was absent.

- [ ] `LightSources_DeclareTunedShadowDefaults`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: spotSource should contain (case insensitive comparison) "ContactShadowDistance = 0.1f;" — expected source fragment was absent.

- [ ] `LightStructsSnippet_DeclaresDirectionalCascadeFields`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "float CascadeSplits[XRENGINE_MAX_CASCADES];" — expected source fragment was absent.

- [ ] `PointLightLayeredModes_AreExposedAndUseVertexLayerContract`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: generatorSource should contain (case insensitive comparison) "int xrePointShadowSlot = gl_InstanceID % xrePointShadowFaceCount;" — expected source fragment was absent.

- [ ] `PointLightShadowPath_UsesForcedGeneratedVertexContract`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: pointLightSource should contain (case insensitive comparison) "mat = new(refs, geomShader, fragShader);" — expected source fragment was absent.

- [ ] `ShadowAtlasReceivers_ScaleBiasAndFiltersForDemotedTiles`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "atlasResolutionScale = max(DirectionalShadowAtlasParams1[atlasRecordIndex].w, 1.0)" — expected source fragment was absent.

- [ ] `ShadowAtlasTileRendering_IsTimeBudgetedNotOnlyTileCountBudgeted`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "scheduledBudgetCost &gt;= budget" — expected source fragment was absent.

- [ ] `ShadowCasterVariant_NormalizesExactTransparencyShaderBackToStandardSource`
  Test source: [ForwardDepthNormalVariantTests.cs](../../../../XREngine.UnitTests/Rendering/ForwardDepthNormalVariantTests.cs).
  Observed: variantText should contain (case insensitive comparison) "float alphaMask = texture(Texture1, FragUV0).r;" — expected source fragment was absent.

- [ ] `ShadowRenderPipeline_BindsEveryMeshPassToShadowFbo`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: preRenderIndex should be greater than 2392 but was -1

- [ ] `SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: partialDirtyFrame.Generation should be 6uL but was 7uL

- [ ] `SolveAllocations_IsDeterministicAndNonOverlappingAcrossFrames`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: first.Generation should be 1uL but was 2uL

- [ ] `SolveAllocations_PublishesBackendAwareDirectionalAtlasUvBias`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: glAllocation.UvScaleBias.W should be within 9.999999974752427E-07d of 0.001953125f but was 0.0024414062f

- [ ] `VkMeshRenderer_ShadowDrawsSuppressLinePointAndUploadLayeredUniforms`
  Test source: [XRMeshAndMeshRendererVulkanParityContractTests.cs](../../../../XREngine.UnitTests/Rendering/XRMeshAndMeshRendererVulkanParityContractTests.cs).
  Observed: enqueueSource should contain (case insensitive comparison) "MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(effectiveMaterial, instances)" — expected source fragment was absent.

- [ ] `VulkanDeferredShadowDraws_CaptureLayeredShadowUniformState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: meshRendererSource should contain (case insensitive comparison) "CaptureProgramBindingSnapshot(effectiveMaterial, shadowUniformState)" — expected source fragment was absent.

- [ ] `VulkanLayeredFramebuffer_UsesAttachmentLayerCountForTextureArrays`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: commandBufferSource should contain (case insensitive comparison) "ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask)" — expected source fragment was absent.

- [ ] `VulkanMaterialUniformUploadMatchesOpenGlShadowAndEngineUniformSources`
  Test source: [XRMaterialAndShaderVulkanParityContractTests.cs](../../../../XREngine.UnitTests/Rendering/XRMaterialAndShaderVulkanParityContractTests.cs).
  Observed: drawStateSource should contain (case insensitive comparison) "ShadowBindingSourceMaterial" — expected source fragment was absent.

## Failed pipeline lifecycle / purpose / host cases (16)


- [ ] `DefaultPipeline_CameraUnavailableResizeThenFramePrepareKeepsOneFeatureSnapshot(XREngine.Rendering.DefaultRenderPipeline)`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: resizePending.Key.FeatureMask should be 536895746uL but was 16642uL

- [ ] `DefaultPipelines_GiProfilesDeclareOnlySelectedWorkingResources(XREngine.Rendering.DefaultRenderPipeline)`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: System.Collections.Generic.KeyNotFoundException : The given key 'RestirInitialReservoir' was not present in the dictionary.

- [ ] `DefaultPipelines_UseImportedProbeBindingsWithoutDirectRegistryMutation`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: source should contain (case insensitive comparison) "UnbindImportedTexture(" — expected source fragment was absent.

- [ ] `DefaultRenderPipeline_DefaultMonoLayout_DeclaresCoreGraphResources`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: gBuffer.Attachments.Select(x =&gt; x.ResourceName).ToArray() should be ["AlbedoOpacity", "Normal", "RMSE", "TransformId", "DepthStencil"] but was (case sensitive comparison) ["AlbedoOpacity", "Normal", "RMSE", "TransformId", "EmissionColor", "DepthStencil"]

- [ ] `DefaultRenderPipeline_GtaoFrameBuffers_DependOnSampledDepthAndNormalViews`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: frameBuffer.Dependencies should contain "DepthView" but was actually ["ForwardContactDepthView", "AlbedoOpacity", "ForwardPrePassNormal", "RMSE", "TransformId", "ForwardPrePassDepthStencil"] Additional Info: AmbientOcclusionFBO

- [ ] `DefaultRenderPipeline_ResourceFeatureMaskIsStableUntilStructuralFeatureChanges`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: pipeline.BuildResourceFeatureMaskForGenerationKey(instance, null) should not be 16642uL but was

- [ ] `DefaultRenderPipeline_StereoTsrLayout_DescriptorFactoriesAndFbosUseTwoLayerMultiviewShapes`
  Test source: [RenderPipelineResourceLayoutBuilder.cs](../../../../XREngine.Runtime.Rendering/Rendering/Resources/Builder/RenderPipelineResourceLayoutBuilder.cs).
  Observed: System.InvalidOperationException : Render pipeline resource layout is invalid: Resource 'TemporalAccumulationFBO' depends on missing resource 'Velocity'. Resource 'TsrUpscaleFBO' depends on missing resource 'Velocity'. Resource 'MotionBlurFBO' depends on missing resource 'Velocity'.

- [ ] `EffectiveGenerationKey_ChangesForEachStructuralProfileInput`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: changedFields should be ["OutputHDR", "SettingsRevision"] but was (case sensitive comparison) ["OutputColorFormat", "OutputHDR", "SettingsRevision"]

- [ ] `Factory_RoutesDesktopCaptureAndOpenXrToTheirOwnedArchitectures`
  Test source: [RenderPipelinePurposeTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelinePurposeTests.cs).
  Observed: capture should be of type XREngine.Rendering.AdvancedRenderPipeline but was XREngine.Rendering.DefaultRenderPipeline

- [ ] `RapidResizeAndFeatureToggleBurst_CoalescesPendingAndBoundsRetiredGenerations`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: instance.RetiredGenerations.Count should be less than or equal to 3 but was 4

- [ ] `ResourceGenerationKey_ContainsOnlyStructuralRenderProfileInputs`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: propertyNames should be ["AntiAliasingMode", "DisplayHeight", "DisplayWidth", "ExternalTargetKind", "FeatureMask", "InternalHeight", "InternalWidth", "MsaaSampleCount", "OutputHDR", "PipelineName", "ReservedEyeIndex", "ReservedViewCount", "SettingsRevision", "Stereo"] but was (case sensitive comparison) ["AntiAliasingMode", "DisplayHeight", "DisplayWidth", "ExternalTargetKind", "FeatureMask", "InternalHeight", "InternalWidth", "MsaaSampleCount", "OutputColorFormat", "OutputDepthFormat", "OutputHDR", "PipelineName", "PipelineRevision", "ReservedEyeIndex", "ReservedViewCount", "ResourceVariant", "SettingsRevision", "Stereo"]

- [ ] `RetainedAuxiliaryPipelines_DeclareTheirOwnedResources`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: ui.ResourcesByName.Keys.OrderBy(static x =&gt; x) should be ["DepthStencil", "DepthView", "StencilView"] but was (case sensitive comparison) [] (System.Linq.Enumerable+OrderedIterator\`2[System.String,System.String])

- [ ] `SuccessfulBackendCommit_PublishesLogicalAndPhysicalGenerationTogether`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: PreparePendingGeneration(instance) should be True but was False

- [ ] `VulkanAllocator_ReusedImageMetadataChangesOnlyAtCommit`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: activeGroup.LogicalResources.Single().Descriptor.PixelFormat should be EPixelFormat.Bgra but was EPixelFormat.Rgba

- [ ] `VulkanReadbackScope_UsesCapturedRenderedFrameGenerationAndTarget`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: System.ArgumentOutOfRangeException : Index was out of range. Must be non-negative and less than or equal to the size of the collection. (Parameter 'startIndex') ActualValue: &lt;null&gt; ParamName: startIndex

- [ ] `VulkanSwapchainRecreation_DoesNotWaitForWholeDeviceIdle`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: method should contain (case insensitive comparison) "TryPrepareRetirementMarkers(out Fence graphicsMarker)" — expected source fragment was absent.

## Reproduction

Run from the repository root. Use a retained investigation run root for disposable
build output and logs; this example uses the original run. Two MSBuild workers
were used after an unrestricted parallel build lost child nodes.

```powershell
$run = 'Build/_AgentValidation/20261002-090000-dirlight-shadows'
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --artifacts-path "$run/temp-build/codex-tests" -m:2 -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false --filter 'FullyQualifiedName~Shadow|FullyQualifiedName~AdvancedGlobalResource|FullyQualifiedName~AdvancedShaderAccess' --logger 'trx;LogFileName=shadow-validation.trx' --results-directory "$run/reports/codex-tests"
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --artifacts-path "$run/temp-build/codex-tests" -m:2 -p:XREngineUseExistingNativeBridges=true -p:UseSharedCompilation=false /nodeReuse:false --filter 'FullyQualifiedName~RenderPipelineResourceLifecycle|FullyQualifiedName~RenderPipelinePurpose|FullyQualifiedName~RuntimeRenderingHostServices' --logger 'trx;LogFileName=pipeline-lifecycle-validation.trx' --results-directory "$run/reports/codex-tests"
```

For a single failure, narrow the filter to its fully qualified fixture/method;
for parameterized cases, select the method and confirm the failing argument.
Use `--no-build --no-restore` only when the retained test binaries match the source
being validated. Record the checked-out revision and relevant working-tree changes
with each rerun; the original results came from a shared working tree with concurrent work.

## Disposable evidence

Under `Build/_AgentValidation/20261002-090000-dirlight-shadows/`:

- `reports/codex-tests/shadow-validation.trx`
- `reports/codex-tests/pipeline-lifecycle-validation.trx`
- `logs/codex-tests.log` (initial parallel-build failure)
- `logs/codex-tests-retry.log` and `logs/codex-pipeline-tests.log`
- `reports/codex-toggle-summary.json` and `reports/codex-capture-results.json`

Evidence may be cleaned up; the exact failing cases and diagnostics above are the
durable record. Add resolutions here so future work does not depend on those ignored files.
