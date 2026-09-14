# Vulkan 1.4 H6 qualification matrix (2026-09-14)

This is a bounded evidence inventory for H6. It separates authored-source
coverage, command accounting, and viewed output. The `entries` count in the
coverage reports is a shader identity count; it is not a draw or dispatch
count. Vulkan `Recorded` entries are recorded commands and are not proof of
GPU completion.

## Current counted lanes

| Lane | Evidence | Exact counters | What it establishes |
|---|---|---:|---|
| OpenGL final corpus | `Build/_AgentValidation/20260910-060112-vulkan14-h/reports/final-gl-coverage.json` | Original water `C56D...`, revision 3: 689 draws; magenta `17A0...`, revision 4: 2,672; restored original, revision 6: 1,153. Invalid revision 5 is absent. | Final magenta, syntax-error last-good, restored-water, geometry, and two authored-water camera outputs were captured and viewed. Shader metadata status 15 (`Failed`) kept the previous program. |
| OpenGL normal/water | `.../reports/water-normal-code-coverage.json` | 43 identities: Fragment 28, Vertex 6, Compute 7, TessControl 1, TessEvaluation 1. Graphics direct 25,251; compute direct 931; known instanced 5,784; known non-instanced 19,467; tessellation 721 each. | The earlier normal lane is superseded for water qualification by the final corpus: authored water is now visible in the two viewed camera results. |
| Vulkan XR code | `.../reports/xr-code-coverage.json` | 49 identities: Fragment 30, Vertex 6, Compute 12, Mesh 1. The Mesh identity (`VisibilityRaster.mesh`) has zero graphics and compute counters. Nonzero counters are Fragment/Vertex/Compute: graphics direct 191,212; compute direct 1,077; known instanced 2,662; known non-instanced 188,550. | Vulkan command recording covered graphics, compute, vertex/fragment, and instancing metadata. The mesh identity is registered only and is not execution evidence. |
| Vulkan stereo output | `.../reports/final-monado-vulkan-left.json`, `final-monado-vulkan-right.json`, `final-monado-vulkan-coverage.json`, `final-monado-vulkan-runtime-state.json` | True SPS, actual Vulkan, conventional indexing; left 393/copy 393, right 398/copy 398; runtime 178 submitted / 192 completed, zero end-frame failures and fallback attempts; zero Vulkan validation messages/errors. Both 896x1007 eye images were viewed and are distinct. | Vulkan true SPS is qualified for this bounded control. Slang was unavailable; retained GLSL was exercised. |
| OpenGL XR controls | `.../reports/final-monado-opengl-sequential-*.json`, `final-gl-unsupported-sps-capture.json` | Explicit GL `SequentialViews`: left 308/copy 308, right 314/copy 314; 91 submitted / 93 completed, zero end-frame failures; both 896x1007 eyes viewed and distinct; automatic vertical flip true. Explicit GL true SPS reports `CanUseTrueSPS=false`, zero submitted, 116 completed, no fallback. | OpenGL sequential views are qualified. OpenGL true SPS is explicitly unsupported and was not hidden behind fallback. |

The final GL geometry control is recorded in `final-gl-geometry-effective.json`:
effective geometry is `2`, with `PointLightAtlasShadowDepth.gs` at 5,368 direct
graphics commands (source revision 4). This qualifies the requested geometry
lane for the GL corpus; it does not imply Vulkan geometry-stage support.

The authored-water correction is documented by the final validation reports:
mixed `General`/read/write framebuffer scope restoration and scaled grab
resize changed the temporary native-query result from FBO 0 (1,492,992
samples) to FBO 10 (651,638–652,609 samples), with zero GL errors and
`pipelineValid=1`. These are viewed GL results. The water output remains a
GL qualification result and is not evidence for Vulkan tessellation.

The GL preview-disable control remains an actionable diagnostic: the
preview-disabled capture returns `VrCopyEyePreviewTextures` failure after
67 ms, and the property was restored. This is a control-path result, not eye
output qualification. Vulkan validation-zero observations above come from the
Vulkan control reports and must not be reused as a GL error count.

The reports use `backend`, `commandState`, `stage`, `graphicsDirect`,
`computeDirect`, `knownInstanced`, and `knownNonInstanced` fields. The prior
43/49 shorthand means identities in the OpenGL/Vulkan snapshots, respectively;
it must not be described as command totals.

The graphics and instancing totals above use one stage (Fragment); the Vertex
totals match in these reports. One draw increments each participating stage's
identity, so summing Vertex, Fragment, Geometry and tessellation counters would
count the same command multiple times. Compute totals are separate dispatches.

Named nonzero OpenGL examples include `FullscreenTri.vs`, `BloomCopy.fs`,
`BloomDownsample.fs`, `BloomUpsample.fs`, `ColoredDeferred.fs`,
`DeferredLightCombine.fs`, `DeferredLightingDir.fs`, `DeferredLightingPoint.fs`,
`FinalPostProcess.fs`, `FXAA.fs`, `GTAOBlur.fs`, `GTAOGen.fs`, `LightCulling.comp`,
`DetailPreservingMipmaps.comp`, `PointLightShadowDepth.fs`, `PostProcess.fs`,
`WaterDynamicForward.fs`, `WaterDynamicForward.tesc`, and
`WaterDynamicForward.tese` and `PointLightAtlasShadowDepth.gs` (the final GL report records 4,514 direct graphics
commands for each tessellation stage). The nonzero Vulkan set includes the
scene/post-process identities, stereo variants `GTAOGenStereo.fs`,
`GTAOBlurStereo.fs`, `DeferredLightCombineStereo.fs`, `BloomCopyStereo.fs`,
`BloomDownsampleStereo.fs`, `BloomUpsampleStereo.fs`, `PostProcessStereo.fs`,
`FinalPostProcessStereo.fs`, `FXAAStereo.fs`, and `FullscreenTriOVR.vs`, plus
`UITextBatched.vs`, `UITextBatched.fs`, `UnlitTexturedForward.fs`, and
`VulkanAutoExposure2DArray.comp`.

## H6 feature matrix

| H6 representative lane | Source anchors | Counted/viewed evidence | Qualification |
|---|---|---|---|
| GLSL source and backend | `Build/CommonAssets/Shaders/**/*.vs,*.fs,*.comp,*.tesc,*.tese`; `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Shaders/VulkanShaderCompiler.cs` | Final GL corpus captures include original, failed-edit last-good, magenta, restored-water, and geometry paths; Vulkan remains `language=Glsl`, `Recorded`. | GL reload, tessellation, geometry, and viewed output qualify the selected GL lanes. This does not claim every asset was compiled or executed. |
| Macros / material parameters / matrix and clip transforms | The nonzero identity set includes `FullscreenTri.vs`, `DeferredLightingDir.fs`, `ColoredDeferred.fs`, `PostProcess.fs`, `FinalPostProcess.fs`, `UnlitTexturedForward.fs`, and stereo variants. | Those exact identities have nonzero direct/recorded counters. | Representative active scene/post-process source is covered; this does not prove every macro or material variant. |
| UBO / SSBO | Nonzero compute identities are `LightCulling.comp`, `DetailPreservingMipmaps.comp`, and Vulkan `VulkanAutoExposure2DArray.comp`. | GL: 721 and 210 direct dispatches respectively; Vulkan: 1,077 direct dispatches. | Counted compute/storage lane from the actual reports; unrelated registered assets are excluded. |
| Textures / images / samplers | Active fragment identities include `UnlitTexturedForward.fs`, bloom, GTAO, deferred-lighting, post-process, and water files; active compute identities are listed above. | Nonzero GL/Vulkan counters for those exact identities. | Representative sampled-image and compute lanes are covered; image-write completion is not independently measured here. |
| Instancing | `UITextBatched.vs` and `.fs` are nonzero Vulkan identities and report `knownInstanced=2,662`; GL `PointLightShadowDepth.fs` reports 5,768 instanced commands. | GL known instanced 5,784; Vulkan known instanced 2,662. | UI glyph instancing is represented in the scoped counters; GPU scene `instanced=0` does not invalidate this evidence. |
| Supported stages | GL Vertex/Fragment/Compute/Geometry/TessControl/TessEvaluation; Vulkan Vertex/Fragment/Compute (the Mesh identity is zero-count) | GL final geometry: `PointLightAtlasShadowDepth.gs`, 5,368 direct graphics; GL water tessellation: 4,514 each; Vulkan exact nonzero stage totals above | GL tessellation and geometry are qualified for the selected corpus. Vulkan coverage has no nonzero tessellation or geometry-stage entries; mesh/task paths are outside the scoped native-command counter exclusions. |
| Stereo / multiview | Nonzero Vulkan stereo identities: `GTAOGenStereo.fs`, `GTAOBlurStereo.fs`, `DeferredLightCombineStereo.fs`, `BloomCopyStereo.fs`, `BloomDownsampleStereo.fs`, `BloomUpsampleStereo.fs`, `PostProcessStereo.fs`, `FinalPostProcessStereo.fs`, `FXAAStereo.fs`, `FullscreenTriOVR.vs`; runtime SPS controls | Final Monado Vulkan true SPS and OpenGL sequential controls above; both eyes viewed. Registered-only `UITextBatchedStereoMV2.vs`, `MotionVectorsStereo.fs`, and `TemporalAccumulationStereo.fs` are not execution evidence. | Vulkan true SPS and OpenGL sequential views qualify. GL true SPS is explicitly unsupported. |

## Existing test and compiler coverage

The actual test tree is `XREngine.UnitTests/` (there is no separate
`XREngine.Tests` tree in this checkout). Relevant existing tests are:

* `XREngine.UnitTests/Rendering/AdvancedPreparationIntegrationContractTests.cs`
  (`AdvancedPreparationShadersCompileToSpirvForVulkan` and the shared
  OpenGL/Vulkan contract check).
* `XREngine.UnitTests/Scene/SerializedPrefabAvatarPrivateIntegrationTests.cs`
  (Vulkan shader compiler calls for serialized avatar material paths).
* `XREngine.UnitTests/Editor/ShaderEditorServicesTests.cs` (compiler diagnostic
  parsing; it is not a GPU execution test).
* `XREngine.Benchmarks/VulkanPerformance/VulkanPerformanceFixtureTests.cs`
  (benchmark harness, not H6 proof by itself).

These are an inventory, not a claim of passing tests. Final existing narrow
cache, OpenXR preview, queued-readback and resource-lifecycle test attempts were
blocked before discovery by unrelated UnitTests API mismatches after the
runtime/backend projects built. No tests were added or modified; see the
[final validation record](vulkan14-final-validation-2026-09-14.md#validation-and-handoff).

The runtime Slang implementation is
`XREngine.Runtime.Rendering.Vulkan/.../Shaders/SlangVulkanShaderCompiler.cs`.
The H corpus run deliberately had `XRE_SLANGC` unavailable, so the corpus
coverage above is GLSL only. A separate native Slang pilot is tracked in the H
reports and remains distinct from this corpus.

## Qualification decision and limits

H6 has enough representative counted lanes to qualify the selected OpenGL
reload, geometry, tessellation, viewed-water, and sequential XR paths, plus
Vulkan true-SPS XR. The final same-binary Release build completed with zero
warnings/errors in 64.61 seconds. Vulkan retains recorded vertex, fragment,
compute, and stereo evidence. Its mesh identity is registered-only and has zero
counters; native mesh/task paths are explicitly outside scoped counter coverage.
Vulkan tessellation and geometry support remain unqualified. Generated Slang
GL has no qualified route and is not required because retained authored GLSL
counterparts remain supported. No exhaustive shader-port matrix is implied by
H6, and the current evidence does not justify one.
