# Renderer Validation Failures — Completed

Status: Closed — all 50 recorded failures resolved; strict-parity live validation passed.
Last updated: 2026-10-02
Ownership: Rendering, Vulkan backend, shader contracts, and runtime asset loading, according to the affected test.

## Closeout results

Work resumed from `79ab16f8f`. Both original filters, plus strict-parity shader
source loading and cache reuse/invalidation coverage, pass together: **342 passed,
0 failed, 0 skipped or inconclusive**. All 50 original methods also pass in
separate test processes. Test edits began only after live validation and explicit
user clearance. No production changes were made merely to satisfy stale text.

The isolated Vulkan Advanced editor built without warnings/errors and passed
64-, 109-, and 153-second shadow-disable/re-enable cycles under
`XRE_AOT_PARITY=error`. Normal and shadow-mask captures were viewed from three
positions. Node selection also passed after fixing gizmo construction. The named
session is stopped and original ignored world settings are restored. Validation
layers were disabled; this does not certify every Vulkan validation rule or a
published NativeAOT executable.

Production fixes honor registered asset factories (including XRShader), defer
gizmo model setup until scene-node attachment, preserve camera-owned AO resources
during camera-unavailable resize, and commit Vulkan publication before independent
old-resource retirement with retained-owner quarantine on failure.

Two additional generated-layout assertions now verify the generated declarations
and their compiled SPIR-V offsets/strides instead of removed preprocessor macros.
Six previously inconclusive source checks now follow moved backend files, and
missing required source is a failure. The original source assertion names below
are retained as history; the GI test now ends in
`DeclareOnlySupportedProviderResources`, and the resize test ends in
`RetainsUnsignaledGenerations`.

Committed history confirms the view-record additions in `6e50d124`/`51ffc79f`
and the atlas texel-center/publication-generation changes in `fe17fafde` predate
this closeout. A separate clean baseline run was not performed; no blanket
regression attribution is made from the original shared-working-tree run.

## Original evidence

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

- [x] Verify all 50 original methods in isolation and both original filters together; no order-dependent failure remains in these runs.
- [x] Compare intended contracts and committed source history; explicitly retain the baseline-attribution limitation above.
- [x] Resolve numeric/layout/generation and exception failures, distinguishing production defects from obsolete expectations.
- [x] Follow each source assertion to its current owner and preserve the behavior it protects.
- [x] Complete live validation before the explicitly authorized test edits.
- [x] Record every resolution and passing rerun; both original filters pass at closeout.

## Separate runtime validation prerequisite

- [x] Resolve strict parity rejection of XRShader construction in RuntimeThirdPartyAssetLoadingServices.Load and repeat Vulkan Advanced checks with parity enforcement enabled.

The original default unit-world launch raised AotParityViolationException and blocked the
Advanced visibility shader family. The successful shadow validation used the
session-only `XRE_AOT_PARITY=off` override with `XRE_DIRECTIONAL_SHADOW_AUDIT=1`.
Vulkan validation layers were disabled; absence of VUID log lines does not certify
validation-layer correctness. The isolated editor was stopped after validation.

## Resolved original shadow / Advanced cases (34)

Each item retains the original test case name, source, observed failure, and
verified resolution.

- [x] `ConditionalShadowRegistriesRemainStableAndOutputScoped`
  Test source: [VulkanDesktopPlanStabilityTests.cs](../../../../XREngine.UnitTests/Rendering/VulkanDesktopPlanStabilityTests.cs).
  Observed: start should be greater than or equal to 0 but was -1 Additional Info: Missing method start 'private RenderResourceRegistry? BuildMergedFrameOpRegistry'.
  Resolution: Follow the registry-planning authority; assert output-scoped accumulation, immutable replacement snapshots, and owner-descriptor precedence. Passing combined and isolated reruns.

- [x] `DeferredDirectionalLightPass_BindsSafeShadowFallbacks_AfterLightReactivation`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: lightCombineSource should contain (case insensitive comparison) "public XRMeshRenderer? DirectionalLightRenderer { get; private set; }" — expected source fragment was absent.
  Resolution: Check the current deferred-light renderer and typed sampler fallback bindings after reactivation. Passing combined and isolated reruns.

- [x] `DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)" — expected source fragment was absent.
  Resolution: Check current backend files, layered attachment behavior, and explicit sequential fallback decline outputs. Passing combined and isolated reruns.

- [x] `DirectionalCascadeAtlasStaleTiles_PreserveRenderedUniformState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: source should contain (case insensitive comparison) "staleAges[i] = ResolveRenderedCascadeStaleAge(frameId, atlasSlot.LastRenderedFrame);" — expected source fragment was absent.
  Resolution: Assert rendered record preservation and stale age based on desired-content mismatch. Passing combined and isolated reruns.

- [x] `DirectionalCascadeInstancedPath_UsesVertexLayerContractAndMaterialFallbacks`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: generatorSource should contain (case insensitive comparison) "int xreCascadeLayer = gl_InstanceID % xreCascadeLayerCount;" — expected source fragment was absent.
  Resolution: Check the current layered instance expression and material fallback in the relocated OpenGL implementation. Passing combined and isolated reruns.

- [x] `DirectionalCascadeShaders_UsePerCascadeBiasAndReceiverOffset`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "float receiverOffset = atlasSampleAllowed ? light.RenderedCascadeReceiverOffsets[cascadeIndex] : light.CascadeReceiverOffsets[cascadeIndex];" — expected source fragment was absent.
  Resolution: Validate current and rendered bias/receiver-offset fields in immutable directional GPU records. Passing combined and isolated reruns.

- [x] `DirectionalCascadeSourceCamera_PrefersPlayerAssociatedCascadedViewport`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "private bool HasActiveCascadedDirectionalShadowViewport(ShadowRequestSource source)" — expected source fragment was absent.
  Resolution: Follow the current source-state owner and player-associated viewport selection. Passing combined and isolated reruns.

- [x] `DirectionalDepthCascadeAtlasFallbacks_KeepReceiverArrayBoundOutsideVulkanAtlas`
  Test source: [ShadowMapMomentPhase2Tests.cs](../../../../XREngine.UnitTests/Rendering/ShadowMapMomentPhase2Tests.cs).
  Observed: directionalSource should contain (case insensitive comparison) "program.Uniform(names.CascadeSplits, cascadeSplits);" — expected source fragment was absent.
  Resolution: Check storage-record cascade publication and receiver-array binding outside the Vulkan atlas path. Passing combined and isolated reruns.

- [x] `DirectionalLightComponent_PublishesPerCascadeBiasUniforms`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "CascadeBiasMin[{i}]" — expected source fragment was absent.
  Resolution: Check GPU-record publication instead of removed per-field uniform arrays. Passing combined and isolated reruns.

- [x] `DirectionalPrimaryShadowAtlasShaders_DoNotUseLegacyMapWhenAtlasIsEnabled`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "ivec4 atlasI0 = DirectionalShadowAtlasPacked0[atlasRecordIndex];" — expected source fragment was absent.
  Resolution: Check atlas state through directional records while retaining the prohibition on legacy-map sampling. Passing combined and isolated reruns.

- [x] `DirectionalShadowAtlas_ContentHashTracksPublishedCasterMembershipAndState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: commandCollectionSource should contain (case insensitive comparison) "_renderingShadowCasterCommandSetSignature = ComputeShadowCasterCommandSetSignature();" — expected source fragment was absent.
  Resolution: Follow BackendReadyFramePackage signature computation and RenderCommandCollection publication. Passing combined and isolated reruns.

- [x] `ForwardLighting_BindsForwardPrePassContactShadowTextures`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "const int forwardContactDepthUnit = 26;" — expected source fragment was absent.
  Resolution: Assert runtime prepass availability and current contact bindings 28–31. Passing combined and isolated reruns.

- [x] `ForwardLightingSnippet_DeclaresCascadeShadowBindings`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "layout(binding = 17) uniform sampler2DArray DirectionalShadowMapArrays" — expected source fragment was absent.
  Resolution: Assert descriptor-set macros, cascade-array bindings, and record-based matrices. Passing combined and isolated reruns.

- [x] `ForwardLocalShadowMetadata_UsesStorageBuffers`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "layout(std430, binding = 37) readonly buffer ForwardPointShadowMetadataBuffer" — expected source fragment was absent.
  Resolution: Keep storage-buffer enforcement with descriptor-set macros and metadata bindings 37–38. Passing combined and isolated reruns.

- [x] `GLMaterial_RebindsLightSamplerUniformsEveryBindingBatch`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "Light bindings include shadow-map samplers." — expected source fragment was absent.
  Resolution: Follow the current light-binding batch and renderer-owned sampler publication. Passing combined and isolated reruns.

- [x] `GlobalRecords_HaveStableStd430CompatiblePacking`
  Test source: [AdvancedGlobalResourceContractTests.cs](../../../../XREngine.UnitTests/Rendering/AdvancedGlobalResourceContractTests.cs).
  Observed: Marshal.SizeOf&lt;AdvancedViewRecord&gt;() should be 896 but was 944
  Resolution: Expect the current 944-byte view and 272-byte Advanced shadow records; retain all other layout checks. Passing combined and isolated reruns.

- [x] `GroupedDirectionalCascadeAtlasFailure_RendersSequentialAtlasTiles`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)" — expected source fragment was absent.
  Resolution: Assert explicit grouped/sequential decline results and sequential atlas-tile rendering. Passing combined and isolated reruns.

- [x] `GroupedDirectionalCascadeAtlasRender_AdvancesPastRenderedGroupMembers`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: source should contain (case insensitive comparison) "int lastGroupRequestIndex = FindLastDirectionalCascadeGroupRequestIndex(directionalGroup, i);" — expected source fragment was absent.
  Resolution: Follow plan-member completion instead of obsolete loop-index bookkeeping. Passing combined and isolated reruns.

- [x] `GroupedShadowAtlasPasses_ClearEachIndexedTileBeforeDrawing`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: shadowPipelineSource should contain (case insensitive comparison) "RuntimeEngine.Rendering.State.ClearByBoundFBO();" — expected source fragment was absent.
  Resolution: Assert current indexed tile-clear operations before grouped rendering. Passing combined and isolated reruns.

- [x] `LightSources_DeclareTunedShadowDefaults`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: spotSource should contain (case insensitive comparison) "ContactShadowDistance = 0.1f;" — expected source fragment was absent.
  Resolution: Match authored spot contact distance 3, thickness 2, and normal offset 0. Passing combined and isolated reruns.

- [x] `LightStructsSnippet_DeclaresDirectionalCascadeFields`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: source should contain (case insensitive comparison) "float CascadeSplits[XRENGINE_MAX_CASCADES];" — expected source fragment was absent.
  Resolution: Check directional storage-record fields and their GLSL layout instead of removed flat arrays. Passing combined and isolated reruns.

- [x] `PointLightLayeredModes_AreExposedAndUseVertexLayerContract`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: generatorSource should contain (case insensitive comparison) "int xrePointShadowSlot = gl_InstanceID % xrePointShadowFaceCount;" — expected source fragment was absent.
  Resolution: Assert compact relevant-face slots and masked instanced rendering; preserve exposed mode coverage. Passing combined and isolated reruns.

- [x] `PointLightShadowPath_UsesForcedGeneratedVertexContract`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: pointLightSource should contain (case insensitive comparison) "mat = new(refs, geomShader, fragShader);" — expected source fragment was absent.
  Resolution: Follow current point material construction and forced generated-vertex contract. Passing combined and isolated reruns.

- [x] `ShadowAtlasReceivers_ScaleBiasAndFiltersForDemotedTiles`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: forwardSource should contain (case insensitive comparison) "atlasResolutionScale = max(DirectionalShadowAtlasParams1[atlasRecordIndex].w, 1.0)" — expected source fragment was absent.
  Resolution: Check current record-based atlas resolution, bias, and filter scaling. Passing combined and isolated reruns.

- [x] `ShadowAtlasTileRendering_IsTimeBudgetedNotOnlyTileCountBudgeted`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: atlasManagerSource should contain (case insensitive comparison) "scheduledBudgetCost &gt;= budget" — expected source fragment was absent.
  Resolution: Check the current render-budget guard and tile-count budget together. Passing combined and isolated reruns.

- [x] `ShadowCasterVariant_NormalizesExactTransparencyShaderBackToStandardSource`
  Test source: [ForwardDepthNormalVariantTests.cs](../../../../XREngine.UnitTests/Rendering/ForwardDepthNormalVariantTests.cs).
  Observed: variantText should contain (case insensitive comparison) "float alphaMask = texture(Texture1, FragUV0).r;" — expected source fragment was absent.
  Resolution: Assert combined base-texture alpha and mask cutoff, with transparency mode defines removed. Passing combined and isolated reruns.

- [x] `ShadowRenderPipeline_BindsEveryMeshPassToShadowFbo`
  Test source: [CascadedShadowDefaultsAndForwardShaderTests.cs](../../../../XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs).
  Observed: preRenderIndex should be greater than 2392 but was -1
  Resolution: Assert SetOptions ordering, shadow target binding, and normal-Z depth clear. Passing combined and isolated reruns.

- [x] `SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: partialDirtyFrame.Generation should be 6uL but was 7uL
  Resolution: Compare LayoutGeneration and actual allocations; allow publication to advance for changed content. Passing combined and isolated reruns.

- [x] `SolveAllocations_IsDeterministicAndNonOverlappingAcrossFrames`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: first.Generation should be 1uL but was 2uL
  Resolution: Keep non-overlap and stable layout checks while checking publication advancement separately. Passing combined and isolated reruns.

- [x] `SolveAllocations_PublishesBackendAwareDirectionalAtlasUvBias`
  Test source: [ShadowAtlasManagerPhaseTests.cs](../../../../XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs).
  Observed: glAllocation.UvScaleBias.W should be within 9.999999974752427E-07d of 0.001953125f but was 0.0024414062f
  Resolution: Expect half-texel-center scale/bias for both backend Y conventions. Passing combined and isolated reruns.

- [x] `VkMeshRenderer_ShadowDrawsSuppressLinePointAndUploadLayeredUniforms`
  Test source: [XRMeshAndMeshRendererVulkanParityContractTests.cs](../../../../XREngine.UnitTests/Rendering/XRMeshAndMeshRendererVulkanParityContractTests.cs).
  Observed: enqueueSource should contain (case insensitive comparison) "MeshRenderMaterialResolver.ResolveLayeredShadowInstanceCount(effectiveMaterial, instances)" — expected source fragment was absent.
  Resolution: Check snapshot-based instance expansion and four-argument shadow upload with caster relevance. Passing combined and isolated reruns.

- [x] `VulkanDeferredShadowDraws_CaptureLayeredShadowUniformState`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: meshRendererSource should contain (case insensitive comparison) "CaptureProgramBindingSnapshot(effectiveMaterial, shadowUniformState)" — expected source fragment was absent.
  Resolution: Check captured shadow state and caster relevance through enqueue, snapshot, and command-owned upload. Passing combined and isolated reruns.

- [x] `VulkanLayeredFramebuffer_UsesAttachmentLayerCountForTextureArrays`
  Test source: [DirectionalShadowAtlasFallbackTests.cs](../../../../XREngine.UnitTests/Rendering/DirectionalShadowAtlasFallbackTests.cs).
  Observed: commandBufferSource should contain (case insensitive comparison) "ResolveDynamicRenderingLayerCount(vkFrameBuffer.FramebufferLayers, fboViewMask)" — expected source fragment was absent.
  Resolution: Follow shared dynamic-rendering layer utilities and the current clear-target argument. Passing combined and isolated reruns.

- [x] `VulkanMaterialUniformUploadMatchesOpenGlShadowAndEngineUniformSources`
  Test source: [XRMaterialAndShaderVulkanParityContractTests.cs](../../../../XREngine.UnitTests/Rendering/XRMaterialAndShaderVulkanParityContractTests.cs).
  Observed: drawStateSource should contain (case insensitive comparison) "ShadowBindingSourceMaterial" — expected source fragment was absent.
  Resolution: Check command-owned material parameters, texture sampler resolution, shadow source, and required engine bindings. Passing combined and isolated reruns.

## Resolved original pipeline lifecycle / purpose / host cases (16)


- [x] `DefaultPipeline_CameraUnavailableResizeThenFramePrepareKeepsOneFeatureSnapshot(XREngine.Rendering.DefaultRenderPipeline)`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: resizePending.Key.FeatureMask should be 536895746uL but was 16642uL
  Resolution: Fixed production AO feature-mask preservation when all camera references are unavailable; the existing test remains unchanged. Passing combined and isolated reruns.

- [x] `DefaultPipelines_GiProfilesDeclareOnlySelectedWorkingResources(XREngine.Rendering.DefaultRenderPipeline)`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: System.Collections.Generic.KeyNotFoundException : The given key 'RestirInitialReservoir' was not present in the dictionary.
  Resolution: Use the GI provider plan and ResourceVariant; check supported DDGI ownership and no resources for unavailable providers. Passing combined and isolated reruns.

- [x] `DefaultPipelines_UseImportedProbeBindingsWithoutDirectRegistryMutation`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: source should contain (case insensitive comparison) "UnbindImportedTexture(" — expected source fragment was absent.
  Resolution: Follow Advanced.ProbeResources and assert imported buffer bind/unbind ownership. Passing combined and isolated reruns.

- [x] `DefaultRenderPipeline_DefaultMonoLayout_DeclaresCoreGraphResources`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: gBuffer.Attachments.Select(x =&gt; x.ResourceName).ToArray() should be ["AlbedoOpacity", "Normal", "RMSE", "TransformId", "DepthStencil"] but was (case sensitive comparison) ["AlbedoOpacity", "Normal", "RMSE", "TransformId", "EmissionColor", "DepthStencil"]
  Resolution: Include the intentional EmissionColor G-buffer attachment. Passing combined and isolated reruns.

- [x] `DefaultRenderPipeline_GtaoFrameBuffers_DependOnSampledDepthAndNormalViews`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: frameBuffer.Dependencies should contain "DepthView" but was actually ["ForwardContactDepthView", "AlbedoOpacity", "ForwardPrePassNormal", "RMSE", "TransformId", "ForwardPrePassDepthStencil"] Additional Info: AmbientOcclusionFBO
  Resolution: Check ForwardContactDepthView and forward-prepass normal/depth resources and blur dependencies. Passing combined and isolated reruns.

- [x] `DefaultRenderPipeline_ResourceFeatureMaskIsStableUntilStructuralFeatureChanges`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: pipeline.BuildResourceFeatureMaskForGenerationKey(instance, null) should not be 16642uL but was
  Resolution: Select an effective MSAA profile before toggling deferred MSAA. Passing combined and isolated reruns.

- [x] `DefaultRenderPipeline_StereoTsrLayout_DescriptorFactoriesAndFbosUseTwoLayerMultiviewShapes`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: System.InvalidOperationException : Render pipeline resource layout is invalid: Resource 'TemporalAccumulationFBO' depends on missing resource 'Velocity'. Resource 'TsrUpscaleFBO' depends on missing resource 'Velocity'. Resource 'MotionBlurFBO' depends on missing resource 'Velocity'.
  Resolution: Include the velocity resource feature required by the synthetic temporal profile. Passing combined and isolated reruns.

- [x] `EffectiveGenerationKey_ChangesForEachStructuralProfileInput`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: changedFields should be ["OutputHDR", "SettingsRevision"] but was (case sensitive comparison) ["OutputColorFormat", "OutputHDR", "SettingsRevision"]
  Resolution: Expect HDR to change OutputColorFormat as well as OutputHDR and SettingsRevision. Passing combined and isolated reruns.

- [x] `Factory_RoutesDesktopCaptureAndOpenXrToTheirOwnedArchitectures`
  Test source: [RenderPipelinePurposeTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelinePurposeTests.cs).
  Observed: capture should be of type XREngine.Rendering.AdvancedRenderPipeline but was XREngine.Rendering.DefaultRenderPipeline
  Resolution: Check default capture routing and explicit Advanced admission failure without an active renderer; retain desktop/OpenXR routing. Passing combined and isolated reruns.

- [x] `RapidResizeAndFeatureToggleBurst_CoalescesPendingAndBoundsRetiredGenerations`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: instance.RetiredGenerations.Count should be less than or equal to 3 but was 4
  Resolution: Use explicit completion receipts: retain unsignaled generations, then drain after signaling; the count limit is soft. Passing combined and isolated reruns.

- [x] `ResourceGenerationKey_ContainsOnlyStructuralRenderProfileInputs`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: propertyNames should be ["AntiAliasingMode", "DisplayHeight", "DisplayWidth", "ExternalTargetKind", "FeatureMask", "InternalHeight", "InternalWidth", "MsaaSampleCount", "OutputHDR", "PipelineName", "ReservedEyeIndex", "ReservedViewCount", "SettingsRevision", "Stereo"] but was (case sensitive comparison) ["AntiAliasingMode", "DisplayHeight", "DisplayWidth", "ExternalTargetKind", "FeatureMask", "InternalHeight", "InternalWidth", "MsaaSampleCount", "OutputColorFormat", "OutputDepthFormat", "OutputHDR", "PipelineName", "PipelineRevision", "ReservedEyeIndex", "ReservedViewCount", "ResourceVariant", "SettingsRevision", "Stereo"]
  Resolution: Include output color/depth formats, pipeline revision, and resource variant in structural identity. Passing combined and isolated reruns.

- [x] `RetainedAuxiliaryPipelines_DeclareTheirOwnedResources`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: ui.ResourcesByName.Keys.OrderBy(static x =&gt; x) should be ["DepthStencil", "DepthView", "StencilView"] but was (case sensitive comparison) [] (System.Linq.Enumerable+OrderedIterator\`2[System.String,System.String])
  Resolution: Expect UI to use its caller-owned output without allocating private managed textures. Passing combined and isolated reruns.

- [x] `SuccessfulBackendCommit_PublishesLogicalAndPhysicalGenerationTogether`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: PreparePendingGeneration(instance) should be True but was False
  Resolution: Observe previous publication during the backend callback, then both new publications after commit; old resources remain retired. Passing combined and isolated reruns.

- [x] `VulkanAllocator_ReusedImageMetadataChangesOnlyAtCommit`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: activeGroup.LogicalResources.Single().Descriptor.PixelFormat should be EPixelFormat.Bgra but was EPixelFormat.Rgba
  Resolution: Assert separate allocator logical descriptors while the reused physical group's construction metadata remains immutable. Passing combined and isolated reruns.

- [x] `VulkanReadbackScope_UsesCapturedRenderedFrameGenerationAndTarget`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: System.ArgumentOutOfRangeException : Index was out of range. Must be non-negative and less than or equal to the size of the collection. (Parameter 'startIndex') ActualValue: &lt;null&gt; ParamName: startIndex
  Resolution: Follow VulkanFrameLoop.Readback and frame-planner context signatures. Passing combined and isolated reruns.

- [x] `VulkanSwapchainRecreation_DoesNotWaitForWholeDeviceIdle`
  Test source: [RenderPipelineResourceLifecycleTests.cs](../../../../XREngine.UnitTests/Rendering/RenderPipelineResourceLifecycleTests.cs).
  Observed: method should contain (case insensitive comparison) "TryPrepareRetirementMarkers(out Fence graphicsMarker)" — expected source fragment was absent.
  Resolution: Accept the separately declared graphics marker while retaining the no-device-idle contract. Passing combined and isolated reruns.

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

Closeout evidence under `Build/_AgentValidation/20261002-183000-shadow-closeout/`:

- `reports/tests/validation-complete.trx`: 342 passing cases, including both original filters and two import checks.
- `reports/isolated-summary.json` and `reports/isolated/case-*.trx`: 50 original methods passing in separate test processes, with renamed methods mapped explicitly.
- `logs/tests-complete.log`, `logs/session-gizmo-rebuild.log`, `reports/live-profile-final.json`, and `mcp-captures/`: builds, Advanced admission, and viewed runtime evidence.

Original evidence follows for historical context.

Under `Build/_AgentValidation/20261002-090000-dirlight-shadows/`:

- `reports/codex-tests/shadow-validation.trx`
- `reports/codex-tests/pipeline-lifecycle-validation.trx`
- `logs/codex-tests.log` (initial parallel-build failure)
- `logs/codex-tests-retry.log` and `logs/codex-pipeline-tests.log`
- `reports/codex-toggle-summary.json` and `reports/codex-capture-results.json`

Evidence may be cleaned up; the exact failing cases and diagnostics above are the
durable record. Add resolutions here so future work does not depend on those ignored files.
