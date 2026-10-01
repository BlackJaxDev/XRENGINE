# Unified WebGPU shader inventory and renderer integration audit

**Date:** 2026-10-01. **Baseline:** `11ef1f64663e631f969b36921eb5a02affabf9e5`.
**Status:** source inventory and implementation guidance, not shader or rendering acceptance.

Related: [unified runtime TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md),
[pipeline invariants](../../../architecture/rendering/default-render-pipeline-notes.md),
[mesh submission contracts](../../../architecture/rendering/mesh-submission-strategies.md),
[compute reuse audit](browser-compute-reuse-audit.md),
[Slang coexistence plan](../../design/scripting/slang-shader-cross-compile-plan.md).

## Scope and decision recommendation

The initial web tier should be the engine's mono forward path: depth, bounded
directional/spot/point lighting and shadowing, opaque/masked/sorted-transparent
engine materials, sky/environment, HDR scene color, explicit exposure and
tonemapping, optional bloom/FXAA, and engine UI/text. The source material remains
`XRMaterial`; the mesh remains `XRMeshRenderer`; `DefaultRenderPipeline` owns pass
selection. No conversion to `BrowserMeshComponent`, `BrowserMaterialData`, or a
second scene database establishes engine integration.

**D7 approved route:** use additive authored Slang
counterparts for the admitted engine raster, post-process, and UI shader families,
cooked offline to WGSL. Retain the existing desktop GLSL routes and sources
unchanged. Reuse the already-authored WGSL deformation, culling, BVH, and Hi-Z
kernels as backend lowerings of the canonical engine contracts. Do not rewrite
working compute ports merely to make all sources use one language.

This recommendation follows the existing Slang 2026.8 pin and coexistence design;
it does **not** select Slang globally, claim that Slang accepts engine GLSL, or
promote generated OpenGL GLSL. The current Vulkan and WebGPU Slang pilots are
evidence of available integration seams, not proof that every listed pass compiles
to WGSL. A WGSL-only authoring choice would avoid a compiler prerequisite for new
ports, but duplicate every new raster material implementation and its ABI mapping.
Existing hand-written WGSL compute sources need no such additional conversion.

Classification used below:

- **S:** arithmetic/stage behavior is a candidate for an authored Slang port and
  offline WGSL cooking. Existing GLSL is not directly compilable through this route.
- **W:** reuse or extend an existing WGSL backend lowering; engine ownership and
  binding integration still need implementation.
- **D:** excluded from the initial web tier. This can mean unsupported shader
  stages/native features, or a deliberately unqualified optional feature. It does
  not mean that all such mathematics are fundamentally impossible on WebGPU.

Each listed family must have a cook-time admission rule. A user material with an
unlisted feature or custom shader must fail with its material, pass, source, and
reason; it must not render with a Lambert/unlit substitute.

## Existing source and toolchain reality

The `Build/CommonAssets/Shaders` tree contains 552 GLSL-family sources: 222
fragment (`.fs`/`.frag`), 148 compute, 109 include (`.glsl`/`.glslinc`), 40 vertex,
20 geometry, nine task/mesh, and four tessellation. It also contains two Slang
pilots. Shader path names in the tables below are relative to that directory
unless explicitly stated otherwise.

`FrontendPilots/SceneCopy.frag.slang` and `FrontendPilots/CopyCount3.comp.slang`
are opt-in pilots, not full web-pass counterparts. The separate runtime has
`XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit.slang` plus WGSL assets.

`Tools/ShaderCooker` already handles explicit WGSL, generated unlit recipes, and
Slang, with immutable hash-named files, compiler/dependency identity, bounded
diagnostics, and manifest-last publication. It is still a fixed-profile cooker:

- `Program.PrepareAsync` requires `xrengine.browser.mesh.v1`, column-major layout,
  exactly `vertexMain`/`fragmentMain`, the fixed mesh/pipeline/binding ABI, and no
  optional device features
- `WgslAbiVerifier` verifies the admitted matrix/tint/2D-texture/sampler profile;
  arbitrary engine uniform/storage arrays and shader families are not admitted
- `BrowserMaterialShaderGenerator.Generate` supports only opaque unlit tint or
  color texture, and explicitly rejects lighting, skinning, storage, bindless,
  masked, and transparent material requests
- `ShaderCompileRequest`, `ShaderCompileTarget.WebGPUWgsl`, `ShaderArtifact`,
  `XRShader.SourceLanguage`, and `XRShader.EntryPoint` are reusable shared seams
- `Assets/shader-artifact.js` also checks the fixed profile. Expanding only the
  offline cooker would produce artifacts the browser correctly rejects

The next cooker slice therefore needs explicit versioned engine semantic/layout
profiles, stage/entry-point metadata (including compute and depth-only), actual
binding/reflection checks, and matching runtime artifact validation. Keep the
legacy browser profile readable for the reference harness. Missing `slangc` must
remain a named offline error; no compiler download or runtime browser compilation
of source languages is implied.

No compiler or browser execution was performed for this audit. Neither `dotnet`
nor `slangc` was on this audit shell's PATH when checked; that is not a claim about
other task environments or subsequently provisioned tools.

## Pass and source work list

### Vertex, depth, and shadow casters

| Source / producer | Class | Required lowering and known-value check |
| --- | --- | --- |
| `Rendering/Shaders/Generator/DefaultVertexShaderGenerator.cs` and `MeshDeformVertexShaderGenerator.cs` in `XREngine.Runtime.Rendering` | S | Add a target-aware vertex/semantic route preserving mesh locations, UV/color sets admitted by the profile, model/view/projection, normals/tangents, billboarding, and canonical draw data. Do not use the reference harness's fixed 20-byte position/UV layout as the engine vertex contract. Check translated/rotated/asymmetric geometry and nonuniform/negative scale. |
| `Common/DepthOutput.fs`; `Common/DepthNormalPrePass.fs` | S | Preserve depth mode and encoded normal semantics. The masked caster/prepass must run the same admitted material alpha evaluation as color. Check near/far depth, a cutout texture, and normal orientation. |
| `PointLightShadowDepth.fs`; `Snippets/ShadowMomentEncoding.glsl` | S | Retain radial point-light depth and the selected depth/moment encoding, including light far distance. Check all six faces and seams. |
| `DirectionalCascadeShadowDepth.gs`; `DirectionalCascadeAtlasShadowDepth.gs`; `PointLightShadowDepth.gs`; `PointLightAtlasShadowDepth.gs` | D | Geometry/layer emission is not a WebGPU shader route. Use the engine shadow scheduler with separate face/cascade render passes and explicit viewports/array-layer views. Preserve masked material variants; do not discard alpha merely to reuse a generic depth shader. |
| `Scene3D/CopyDepthFromTexture.fs`; `Scene3D/CopyDepthFromTextureMS.fs` | S | Port only for admitted depth-copy/resolve paths. Depth resolve is a shader operation when an attachment resolve is unavailable. Check sample count and standard/reversed depth selection. |
| `Scene3D/FullscreenTri.vs` | S | Reusable post-process vertex stage; check output and source-texture corner orientation. |

Material selection must reuse `MeshRenderMaterialResolver.Resolve` and
`ApplyShadowUniforms`, `ShadowCasterVariantFactory`, and the engine pass variants.
The port must not reconstruct shadow override precedence independently in JavaScript.

### Forward surface materials and transparency

| Source / family | Class | Required lowering and known-value check |
| --- | --- | --- |
| `Common/UnlitColoredForward.fs`; `Common/UnlitTexturedForward.fs`; `Common/UnlitTexturedOpaqueForward.fs`; `Common/UnlitAlphaTexturedForward.fs`; `Common/UnlitTexturedArraySliceForward.fs` | S | Tint, texture encoding, opacity/mask, and array-slice variants. Check labeled texture corners, linear/sRGB ramp, cutoff boundary, and alpha blend order. |
| `Common/LitColoredForward.fs`; `Common/LitTexturedForward.fs`; `Common/LitTexturedAlphaForward.fs` | S | Preserve the engine light, attenuation, material/specular, ambient, and shadow semantics. A one-direction Lambert shader is not parity. |
| `Common/LitTexturedNormalForward.fs`; `Common/LitTexturedNormalAlphaForward.fs`; `Common/LitTexturedSpecForward.fs`; `Common/LitTexturedSpecAlphaForward.fs`; `Common/LitTexturedNormalSpecForward.fs`; `Common/LitTexturedNormalSpecAlphaForward.fs` | S | Normal/specular/masked variants with the original texture-slot interpretation. Check mirrored UV/tangent handedness, alpha masking in all passes, and moving directional/point/spot highlights. |
| `Uber/UberShader.vert`; `Uber/UberShader.frag`; `Uber/common.glsl`; `Uber/uniforms.glsl`; `Uber/pbr.glsl`; `Uber/specular.glsl`; `Uber/emission.glsl` | S, admitted subset | Preserve the authored material model and `UberAuthoredState`/variant feature identities. Begin with base color, normal, metallic/roughness/specular, opacity, emission, bounded lights and environment; reject other authored features until their ports are qualified. Preserve the actual existing channel selection, smoothness inversion, roughness mapping, and feature specialization. These include/helper files are semantic references; not all are directly included by today's main shader. |
| `Snippets/ForwardLighting.glsl`; `LightStructs.glsl`; `LightAttenuation.glsl`; `ShadowSampling.glsl`; `ShadowMomentEncoding.glsl`; `SurfaceDetailNormalMapping.glsl`; `NormalEncoding.glsl`; `AmbientOcclusionSampling.glsl` | S | Shared dependency closure of the common forward materials. Replace desktop resource declarations with bounded bindings without changing lighting/encoding math. A disabled AO feature has a constant neutral value and no AO resources. |
| `Snippets/ForwardLightingPBR.glsl`; `PBRFunctions.glsl`; `ColorConversion.glsl`; `NormalMapping.glsl`; `SurfaceEmission.glsl` | S | Reusable engine math/semantic references as needed by the admitted lit model; preserve feature-specific formulas rather than mixing incompatible material parameter meanings. |
| `Common/*Deferred.fs`, `Scene3D/DeferredLightCombine*.fs`, `DeferredLighting*.fs`, `DeferredDecal*.fs`, and deferred fragment generation | D as passes | A forward web tier must map admitted authored surface semantics into forward shading. Simply omitting `OpaqueDeferred` commands loses ordinary engine content. Do not silently rewrite the serialized material's pass or shader asset. Reject unsupported custom deferred shader semantics at cook time. |
| `Common/LitTexturedSilhouettePOMForward.fs`; `Common/WaterDynamicForward.*`; `Common/Mirror*.fs`; `Uber/outline.*`; optional Uber feature modules | D initially | POM/silhouette depth, tessellated water, mirrors, outline, dissolve/glitter/flipbook, extended/layered surface effects and other unqualified authored features require named exclusions. They can be admitted later per feature with render evidence. |
| `*WeightedOit*`, `*Ppll*`, `*DepthPeel*`; `Snippets/ExactTransparencyPpll.glsl`; `ExactTransparencyDepthPeel.glsl`; corresponding clear/resolve/debug shaders | D initially | Initial transparency is the existing sorted CPU-direct lane. Keep its sort order across batching; do not admit order-independent techniques merely because their source has a forward suffix. |

The shared code locations for this work are `Objects/Materials/XRMaterial.*`,
`Resources/Shaders/UberShaderVariantBuilder.*`, `UberMaterialBindingPlanner`,
`MaterialTextureBindingResolver`, `MaterialPassSet`, and the shader generators.
`BrowserMaterialShaderGenerator` is not the canonical generator to grow into a
parallel engine material system.

### Sky and environment

| Source / family | Class | Required lowering and known-value check |
| --- | --- | --- |
| `Scene3D/Skybox.vs`; `SkyboxCubemap.fs`; `SkyboxEquirect.fs`; `SkyboxOctahedral.fs`; `SkyboxGradient.fs` | S | Preserve engine sky orientation, camera-translation removal, intensity, encoding, and far-depth behavior. Check labeled cube faces and an asymmetric gradient. |
| `Scene3D/SkyboxCubemapArray.fs` | S, optional bounded variant | Requires explicit cube-array texture/view support and admission against device limits. Exclude until that profile exists. |
| `Scene3D/BRDF.fs`; `IrradianceConvolution.fs`; `IrradianceConvolutionCubemapOcta.fs`; `IrradianceConvolutionEquirect.fs`; `IrradianceConvolutionEquirectOcta.fs`; `IrradianceConvolutionOcta.fs`; `Prefilter.fs`; `PrefilterCubemapOcta.fs`; `PrefilterEquirect.fs`; `PrefilterEquirectOcta.fs`; `PrefilterOcta.fs`; `CubemapToOctahedron.fs`; `Equirect.fs`; `Cubemap.vs`; `Cubemap.fs`; `OctahedralEnv.fs` | S, offline first | Prefer cooked engine environment/BRDF products for the initial web runtime. These become runtime shader requirements only if live probe/environment generation is admitted. Check diffuse/specular environment energy and roughness mip selection with known materials. |
| `Snippets/OctahedralMapping.glsl`; `OctahedralBorderCopy.glsl`; `DDGIEnvironmentCapture.glsl` | S, bounded utility only | Preserve direction and seam conventions required by the admitted sky/environment path; utility reuse does not enable DDGI. |
| `Scene3D/SkyboxDynamic.fs`; `Scene3D/Atmosphere/*`; `Scene3D/VolumetricFog/*` | D initially | Exclude dynamic atmosphere and volumetric chains explicitly rather than allocating neutral intermediate images. |

### HDR, tonemapping, and bounded post processing

| Source / family | Class | Required lowering and known-value check |
| --- | --- | --- |
| `Scene3D/SceneCopy.fs`; `Scene3D/PassthroughHDR.fs`; `Scene3D/PostProcess.fs`; `Scene3D/TonemapStandalone.fs`; `Scene3D/FinalPostProcess.fs`; `Snippets/ToneMapping.glsl`; `ScreenSpaceUtils.glsl`; `DepthUtils.glsl` | S | Port the bounded admitted composition with engine exposure/color-grade/tonemap values. Remove excluded feature bindings at cook time rather than populating desktop stencil/editor-outline/atmosphere/fog resources. Preserve the final display transfer exactly once. Check HDR ramp, exposure, color-grade defaults, and output corners. |
| `Scene3D/BloomCopy.fs`; `BloomDownsample.fs`; `BloomUpsample.fs` | S, optional | Same raw-HDR mip-zero input, threshold on first downsample only, legal mip count at tiny extents, and tuned combine interpretation as desktop. Check isolated highlights and 1x1/odd sizes. |
| `Scene3D/FXAA.fs`; `Scene3D/BilinearUpscale.fs`; `Scene3D/HudFBO.fs` | S, optional | Bounded FXAA/backing-resolution upscale and UI composition. Check sharp diagonal edges, DPR scaling, and UI opacity. |
| `Scene3D/BrightPass.fs`; `BloomBlur.fs`; `BloomBlurDynamic.fs` | D for selected bloom route | Legacy/alternate algorithms are not dependencies of the chosen progressive bloom chain. Do not accidentally threshold both before and within downsampling. |
| `Scene3D/*AO*`; `Scene3D/MotionVectors*`; `MotionBlur*`; `DepthOfField*`; `TemporalAccumulation*`; `TemporalSuperResolution*`; GI composites; vendor upscaling | D initially | Explicit pass/resource exclusion. Manual exposure avoids importing a synchronous luminance readback. Future GPU exposure needs its own admitted compute/storage ABI. |

`DefaultRenderPipeline.Resources.cs`, `.CommandChain.cs`, `.FBOs.cs`, `.Textures.cs`,
and `.PostProcessing.cs` must consume the same immutable capability/quality result.
Disabling a pass after declaring/allocating its resource chain is not sufficient.
The existing startup warm-up in `DefaultRenderPipeline.cs` also needs profile-aware
selection; it eagerly warms several excluded desktop shaders.

### Engine UI and text

| Source / family | Class | Required lowering and known-value check |
| --- | --- | --- |
| `Common/UIQuadBatched.vs`; `UIQuadBatched.fs`; `Common/UITextBatched.vs`; `UITextBatched.fs` | S | Preserve `UIBatchCollector` instance/storage layout, clip/scissor rectangles, draw order, atlas sampling, and blend state. Generic executor viewport/scissor support is prerequisite. |
| `Common/Text.vs`; `TextRotatable.vs`; `Text.fs`; `TextMsdf.fs`; `TextMtsdf.fs`; `TextMtsdfScreen.fs` | S | Preserve both `UIText` and `UITextComponent` atlas mode and derivative-based edge treatment; glyph atlases should be cooked with the engine font route. Check rotated/scaled text, colored glyphs, clipping, and hit-test alignment. |
| `UI/UIAlignedQuadTexture.fs` | S | Ordinary engine image quad. Check alpha/color encoding and texture origin. |
| `UI/GrabpassGaussian.frag`; `UI/Backgrounds/Surf2.fs` | D initially | Optional custom UI effects require explicit future admission. |
| Stereo text, `*Stereo*`, `*OVR*`, `*NV*`, `Common/VRDualRender.gs`, editor gizmos/debug geometry | D | Initial output is mono; WebXR and editor rendering are separate deliverables. |

### Reuse of deformation, visibility, and hierarchy compute

All WGSL paths here are relative to `XREngine.Runtime.Rendering.WebGPU/Assets`.

| Existing source and canonical engine source | Class | Integration requirement |
| --- | --- | --- |
| `gpu-skinning.wgsl` against `Compute/Animation/SkinningPrepass.comp` and `SkinningPrepassDispatcher` | W | Reuse `SkinPaletteMatrix` (48-byte affine), Core4x8/Core4x16 indices, UNorm8 weights, spill and sparse quantized morph records. The current adapter writes 20-byte position/UV vertices plus separate normal/tangent output and has explicit size limits; adapt to engine buffers and publish through `XRMeshRenderer` skinned-output snapshots rather than creating browser-only mesh ownership. |
| `gpu-hiz-init.wgsl`; `gpu-hiz-reduce.wgsl` against `Compute/Occlusion/GPURenderHiZInit.comp`, `HiZGen.comp` | W, later measurement gate | Current kernels assume standard zero-to-one depth, far/clear 1 and MAX reduction. Reversed-Z needs an explicit variant/contract. Preserve conservative odd-size footprints and separate write/read passes. |
| `gpu-scene-culling.wgsl` against `Compute/Occlusion/GPURenderOcclusionHiZ.comp::HiZOccludedAabb` | W, later measurement gate | Reuse eight-corner conservative bounds; ambiguous projections stay visible. Fixed indexed slots zero culled `instanceCount`; preserve sorted order and keep `firstInstance=0` unless the enabled feature supports it. Do not introduce indirect-count readback. |
| `gpu-scene-bvh.wgsl` against engine `GpuBvhTree` and `Scene3D/RenderPipeline/bvh_*` | W, later measurement gate | Reuse 16-byte header/48-byte compact nodes, Morton ordering and Karras topology; consume canonical GPUScene identity/publication. The reference 40-word input record is an adapter, not a replacement GPUScene schema. |
| `Compute/Animation/BlendshapePrecombine.comp`; `SkinningPrepassInterleaved.comp`; `SkinningPrepassPrecombined.comp`; `SkinningPrepassInterleavedPrecombined.comp`; `SkinnedBounds.comp`; `SkinnedBoundsReduce.comp` | D until separately admitted | The current WGSL port does not automatically implement all engine output layouts, precombined modes, or conservative bounds. Reject unsupported selected deformation modes or use an explicitly selected canonical CPU route. Deformed objects remain visible and do not become occluders until bounds are valid. |
| `Compute/Culling/GPURenderHiZSoACulling.comp` | D as port source | Older center/sphere test is not the canonical conservative occlusion implementation. |
| `Advanced/*`; `Meshlets/*`; `Graphics/BindlessMesh.*`; `Common/MaterialTable.glsl`; `Common/VulkanBindlessMaterialTable.glsl`; GPU-scene indirect-count/scatter paths | D initially | No mesh/task stages, GPU virtual addresses, desktop bindless handles, descriptor indexing, or indirect-count capability is advertised by the web tier. |

`Assets/browser-raster.wgsl`, `browser-compose.wgsl`, `mesh.wgsl`, and the reference
pipeline's cooked unlit shaders remain **D as production engine replacements**.
They can support isolated executor validation, but reusing their flat-Lambert or
fixed-layout scene policy does not implement the engine's material/pipeline model.

## Smallest correct renderer integration slices

1. **Target-first engine renderer and clear/present.** Add an `AbstractRenderer`
   implementation constructed with `RendererHostContext(BrowserCanvasRenderTarget,
   false, generation)`. Register that engine renderer via
   `WebGpuRendererBackendModule`/factory, retaining the existing `WebGpuRendererHost`
   as a composed session/executor where useful. Exercise an engine-owned output
   frame; do not fake an `XRWindow`. `RenderFrame(double)` is already a neutral
   entry point. Every unsupported abstract operation needs a stable named reason.
   The current base `Dispose()` does no teardown, so explicit ownership of session
   disposal and API-object retirement is essential.
2. **Engine object wrappers and a bounded CPU-direct unlit model.** Implement
   `AbstractRenderAPIObject` wrappers for `XRDataBuffer` (`IApiDataBuffer`),
   `XRShader`/`XRRenderProgram`, `XRMaterial`, and `XRMeshRenderer.BaseVersion`
   (`IApiMeshRenderer`, `IRenderPreparationState`). Preserve owner generation,
   resource dirty events, material overrides, mesh index/vertex layout and model
   transform. Asynchronous program/pipeline creation stays pending through
   preparation; never block the browser thread from `Generate()`.
3. **Shared command arena and state lowering.** Keep GL-shaped state in C#; key
   immutable pipelines with shader/artifact generation, binding layouts, all vertex
   layouts, topology, cull/front-face, color formats/blend/write masks, depth and
   stencil state/bias, and sample state. Reuse the JavaScript resource table,
   bounded pipeline cache, usage-scope and pass-plan validators. Add dynamic
   commands and uploads in reusable C# storage with one submission crossing per
   frame. Current prepared JSON sequences are cold replay objects: they have fixed
   bindings/draw counts and one pipeline per render pass, so repeatedly serializing
   them for changing engine draws is not an allocation-free frame architecture.
4. **Textures and render targets.** Extend descriptors/executor together for array
   layers, cube views, comparison samplers, required vertex/texture formats, HDR
   attachments, and bounded storage textures. Then implement wrappers for
   `XRTexture2D`, `XRTexture2DArray`, `XRTextureCube`, texture views,
   `XRFrameBuffer`, and `XRRenderBuffer`. Map depth-only copies/resolves explicitly.
   The existing generic command profile lacks HDR target formats, storage texture
   bindings, array/cube bindings, and comparison samplers; the reference pipeline's
   private HDR resources do not fill these API gaps.
5. **Engine material ABI and bounded forward pipeline.** Land the cooker/artifact
   profile first, then admitted material/lighting/shadow/environment ports. Add an
   immutable web capability/quality result consumed by pass selection, resource
   declarations, shader warm-up, and cook admission. Existing deferred-authored
   surface content must receive a supported forward encoding, not disappear.
6. **Compute and canonical deformation.** Route `DispatchCompute` through the same
   ordered arena; integrate the existing packed WGSL skinning math into engine
   deformation ownership. Only after CPU-direct/lit correctness and measurement
   add fixed-slot GPU culling/BVH/Hi-Z as separate capability families.

## Invariants and concrete hazards

- `RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy` currently returns forced
  non-meshlet strategies immediately; diagnostics and permissive zero-readback
  preferences can also select GPU-indirect without indirect-count support.
  Returning `SupportsIndirectCountDraw() == false` is therefore insufficient.
  Gate the web tier explicitly to `CpuDirect` with a visible reason, including
  forced/diagnostic settings, while preserving desktop resolution behavior.
- `AdvancedRenderPipeline` capability selection must leave unsupported physical
  outputs explicitly unbound under `Available`, and fail under `Required`. Do not
  replace a shared camera's pipeline source to accommodate one browser output.
  Use the existing capability/output-binding policy, not the older selection
  result alone as an instruction to swap camera assets.
- Stable pipeline resources belong to transactional `RenderResourceGeneration`
  declarations. Resize/quality/HDR changes stage replacements, validate attachment
  identity/range/aspect/format/sample compatibility, and commit atomically. Failed
  pending generations leave active resources usable. Do not allocate replacement
  textures inside ordinary draw commands.
- The generic executor currently has no viewport/scissor command fields. Atlas
  shadows, split viewports, UI clipping, and partial clears need explicit lowering.
  Attachment clear is not a scissor-limited clear; use a suitable draw-based clear
  where the engine requires only a subrectangle.
- Canvas output must be reacquired per submission; retain generation/extent and
  output identity, never the acquired view. Existing `GpuPassPlan` uses canvas
  handles 0/-1 and rejects stale generation plans; preserve that behavior.
- Submit-time resource usages and pass boundaries replace desktop barrier calls.
  Sampled mip/attachment overlap and writable-storage aliasing must remain checked.
  A memory-barrier method cannot silently paper over incompatible same-pass usage.
- Device loss must invalidate session/resource/wrapper/preparation generations and
  reject stale async completions. Rebuild from engine CPU/cooked data; do not
  resurrect reference-harness scene DTOs. Retire dependents before handles and
  avoid synchronous queue waits on the browser owner thread.
- Use `GetScreenshotAsync`/pixel/luminance callbacks and existing readback tickets.
  Synchronous `GetDepth`, mapped GPU reads, and `WaitForGpu` require named rejection
  in a non-blocking host. A CPU staging mirror is not a fresh GPU readback.
- Generic command handles retain dependencies; cache eviction is not destruction
  of in-flight objects. Release prepared sequences/groups/pipelines before their
  underlying resources. Bound caches and uploads by actual device limits.
- `Rendering/Properties/AssemblyInfo.cs` grants GL/Vulkan internal access but not
  WebGPU. Add a deliberate friend assembly only if required by the engine adapter's
  internal frame/resource hooks; do not widen those contracts casually.
- The coordinate ABI needs known-value proof: engine row-vector matrix composition
  versus WGSL columns, near/far depth, texture Y, front-face winding, HDR transfer,
  and every admitted reversed-Z variant. The current WGSL reference contract only
  admits standard depth; do not infer support from a generic renderer depth enum.

No TODO row is closed by this inventory. Each source group still needs authored
ports, cooked ABI evidence, known-value renders, and OpenGL/Vulkan preservation
captures before its integration acceptance can be recorded.

## Implementation source checkpoint

The subsequent source slice makes `WebGpuRendererHost` an `AbstractRenderer` while
preserving its reference-harness capability interfaces. It owns a typed engine
viewport and enters the normal current-renderer/output scopes. An engine frame
records a clear plus retained mesh commands into a bounded binary arena; the one
submission import copies a uniform arena and supplies per-draw dynamic offsets.
Transforms and material callback values are snapshotted before later draws can
change them. The executor uses one native encoder/queue submission and reacquires
the canvas texture once, without retaining a managed memory view.

Concrete `WebGpuDataBuffer`, `WebGpuRenderProgram`, `WebGpuMaterial`, and
`WebGpuMeshRenderer` wrappers now use the engine's objects and cooked module
metadata. The first mesh profile admits rigid, indexed, mono triangles, remaps
semantic vertex inputs to existing interleaved/separate engine streams, and keeps
material/override selection in `MeshRenderMaterialResolver`. Shader catalog
identities are session-scoped; missing artifacts fail rather than matching raw
GLSL text or substituting reference materials. Shader modules and pipelines are
prepared asynchronously. Retained command/resource dependencies prevent premature
release and reject late publication after retirement.

These are source implementations awaiting the integrated build/live-render gates.
They do not establish lit-material, texture, framebuffer, skinning, complete
`DefaultRenderPipeline`, screenshot, mobile, or desktop-preservation acceptance.
Unsupported resource types and operations still fail with named reasons. The
initial clear path and depth diagnostic pass are validation surfaces, not a
complete web-tier fallback. The shader group gate remains in force before later
production pass groups are admitted.
