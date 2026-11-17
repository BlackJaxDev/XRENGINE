# Advanced Render Pipeline

This document describes the frame flow, invariants, capability floor, and output ownership of `AdvancedRenderPipeline`. Selection settings, shared GPU scene tables, and command-authoring rules are in [Default Render Pipeline Notes](default-render-pipeline-notes.md#selecting-advancedrenderpipeline).

`AdvancedRenderPipeline` uses a visibility-buffer opaque renderer. `DefaultRenderPipeline` stays the visual reference and the explicit legacy source. New renderer work goes into `AdvancedRenderPipeline` only. No `DefaultRenderPipeline2` type, alias, or `XRE_USE_PIPELINE_V2` selector exists.

## Code Map
| Area | Location |
|---|---|
| Pipeline partials | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/` |
| Capability, selection, output binding, frame contract | `Advanced/Contracts/`: `AdvancedRenderPipelineCapabilities`, `AdvancedRenderPipelineCapabilityResolver`, `AdvancedRenderPipelineSelectionResolver`, `AdvancedRenderPipelineOutputBinding`, `AdvancedRenderPipelineFrameContract` |
| Stage identity | `EAdvancedRenderStage`, `AdvancedRenderStageDescriptor` |
| Resource ownership and synchronization | `EAdvancedRenderResourceOwnership`, `AdvancedRenderResourceOwnershipContract`, `EAdvancedSynchronizationBoundary`, `AdvancedSynchronizationContract` |
| Shared scene and material tables | `AdvancedSharedGpuSceneDatabase`, `AdvancedFrameSlotUploadArena` |
| Native opaque shading (Vulkan) | `Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp` |

## Frame Flow

`EAdvancedRenderStage` defines the stage order:

```text
FrameBegin
  -> Deformation
  -> VisibilityPreparation
  -> VisibilityRaster
  -> DepthPyramidAndLateVisibility
  -> DirectionalShadowRaster
  -> AmbientOcclusion
  -> WorkClassification
  -> NativeOpaqueShading
  -> LatePasses
  -> TemporalAndPostProcessing
  -> Output
  -> UserInterface
```

- Deformation produces current and previous deformed vertex data one time. Visibility, shadow, velocity, and other geometry consumers reuse it.
- Visibility preparation runs early GPU culling and indirect preparation. Visibility raster writes depth and one visibility payload per pixel. The depth pyramid stage recovers late-visible geometry.
- Work classification builds GPU material and light work proportional to visible coverage.
  `ClassifyTiles.comp` builds a per-tile kernel histogram. With subgroup ballots, each
  subgroup visits only the kernels that its lanes hold (a waterfall loop: the first
  pending lane names the kernel of a pass, the ballot counts its lanes, and those lanes
  leave), bounded to `gl_SubgroupSize` passes. The tile tail runs in parallel: invocation
  k writes the membership of kernel k, and a shared `atomicMax` on
  `(pixel count << 8) | (255 - k)` selects the primary kernel (the largest count; on a tie,
  the lowest kernel index). On New Sponza in VR this cut classification from 2.5 to 0.5 ms
  per view pair, compared with a pass for each of the 128 kernels and a serial tail on one
  invocation.
- Native opaque shading reconstructs surface attributes from visibility identity, evaluates material lighting, and writes HDR scene color.
- Late passes hold transparent, refractive, volumetric, particle, editor-overlay, gizmo, and other special work.

### Mesh surface decal buffer

Imported mesh decals (for example dirt sheets on walls and columns) blend over the surface
below them. They do not enter the visibility buffer.

- **Classification.** The importer marks a material as a surface decal
  (`XRMaterial.AdvancedSurfaceDecal`) when it has an opacity map and "decal" in its name.
  `ModelImportOptions.DecalMaterialMode = Disabled` turns this off for one model. The material
  keeps `ETransparencyMode.Masked` as the fallback for renderers without the lane.
- **Lane support.** The Vulkan backend enables `AdvancedSurfaceDecalLane` at startup. The
  lane is on by default; `XRE_ADVANCED_SURFACE_DECALS=0` (or `false`) selects the masked path.
  Without support (OpenGL, WebGPU, or the variable set to 0), surface decals draw as masked
  materials, and each material reports one diagnostic.
- **Routing.** `AdvancedGpuScenePublisher` sets `GPUIndirectRenderFlags.SurfaceDecal` on the
  draws, and `AdvancedPreparationExtractor` gives them `EAdvancedMaterialCoverageMode.Decal`.
  Decal bins are excluded from the visibility raster (early and late), Hi-Z, and both
  directional shadow recorders. Decals never occlude and never cast shadows. They use the
  traditional indexed raster, not meshlets.
- **Decal buffer phase.** `DepthPyramidAndLateVisibility` enqueues the
  `EAdvancedVisibilityStageBackendPhase.DecalBuffer` phase after late raster (and after the MSAA
  resolve). Its synthetic render-graph pass, `Advanced.DecalBuffer`, clears and writes two
  single-sample RGBA8 targets (`AdvancedDecalBufferResourceNames.Albedo` and `.Surface`) to zero and
  loads the resolved depth read-only. It draws the decal bins of the early and the late argument
  segments.
- **Pipeline.** `VulkanDecalBufferPipelineFactory` creates the decal pipeline:
  - The depth test uses `LessOrEqual` (`GreaterOrEqual` with reversed Z), with no depth write.
  - A slope-scaled depth bias pulls the decal toward the camera.
  - Both targets use premultiplied "over" blending. Alpha accumulates coverage, so a cleared
    texel means "no decal".
- **Shading.** The native shading kernels (standard, Uber, Uber fallback, and both MSAA
  kernels) read the targets through set-1 bindings 69 and 70 when push flag bit 16 is set.
  They resolve `value = surface * (1 - coverage) + decal` for albedo, roughness, and metallic
  before lighting. The standard path applies the decal buffer before the projected decals
  (`XR_ADV_ApplyDecalToSurface`). Emission comes from the surface before decals. MSAA kernels
  apply the pixel value to every sample.
- **Readiness.** The decal programs compile on first use. They are not part of the visibility
  family readiness. While they compile, the pass clears the targets and draws no decals.

### Native opaque shading kernel families

On Vulkan, `ShadeNativeOpaque.comp` compiles as two kernel families. The
preamble define `XR_ADV_NATIVE_SHADING_FAMILY` selects the family:

| Family | Define | Shades | When it compiles |
|---|---|---|---|
| Standard | `1` | Standard, masked, forward, and mirror materials. Native Uber pixels get a fallback while the Uber kernel is not ready. | Always. Advanced admission waits for it. |
| Uber | `2` | Native Uber materials only. | In the background after the standard kernel is ready (prewarm, the default). With `XRE_ADVANCED_UBER_KERNEL_PREWARM=0`, only when the published scene has native Uber shading kernels. Admission never waits for it. |

- `VulkanAdvancedNativeShadingKernelFamilies` splits the published shading
  kernel records into a standard mask and an Uber mask by material layout hash.
  Each pass records indirect dispatches only for the kernels in its mask.
- When the Uber kernel is ready, the standard pass runs first. A
  compute-to-compute barrier follows, then the Uber pass with its own repair
  dispatch.
- Until the Uber kernel is ready, the standard pass also owns the Uber kernels
  and sets the Uber fallback push flag. The fallback shades the material base
  color and base texture (UV0) as a rough dielectric with the world ambient and
  the directional lights. It never writes the magenta error color.
- Multisample visibility resolves all samples of a pixel in one pass. A scene
  with Uber kernels uses the combined multisample kernel (no family define) when
  it is ready, and the standard multisample kernel with the fallback before
  that. Only the kernel for the current visibility mode is requested.
- The standard family also compiles a lean variant (`ShadeNativeOpaqueStandardLean`,
  preamble define `XR_ADV_SHADE_LEAN`). It leaves out the shading debug views, the
  per-light debug values (direct diffuse and specular, shadow reason and visibility)
  and the Uber base-color fallback. A single-sample frame with no shading debug view
  and no Uber fallback records the lean kernel; every other frame records the generic
  standard kernel. The unused paths still cost registers in the generic kernel: on New
  Sponza in VR, the lean kernel shades in about 0.3 to 0.5 ms less. The lean kernel is
  prepared with the family, so admission waits for both and the switch never compiles
  at frame time. The lean kernel still writes the shading diagnostics image, but its
  shadow bits always read "visible" (reason 0, visibility 255). No frame pass reads that
  image (the foveated fill only copies it); to inspect the shadow bits with a capture,
  set a shading debug view, which selects the generic kernel.
- OpenXR eye outputs do not allocate the shading diagnostics image
  (`AdvancedShading.ShadingDiagnostics`, R32_UINT, one layer per view) when no shading
  debug view and no debug overlays are on. No frame pass reads that image, and in
  the 2688x2688 VR benchmark it costs about 55 MB per eye output. The resource profile then sets a
  feature bit that removes the declaration, and the shading stage does not declare
  the image. On Vulkan, binding 18 then holds a dedicated 1x1 R32_UINT storage image
  (`AdvancedShading.ShadingDiagnosticsPlaceholder`, one layer per view), which the same
  feature bit declares with the resource generation. No other binding refers to it, so
  the shading dispatches have no read/write hazard against the visibility metadata image.
  The OpenGL image unit 3 holds the visibility metadata image in its place, which is also
  R32_UINT storage. Push flag bit 31 (`XR_ADV_SHADING_DIAGNOSTICS_DISABLED`) stops every
  store to the placeholder or the metadata image in the shading, background and foveated
  fill kernels. The desktop output and offscreen captures keep the image. Set a shading
  debug view to capture it on an eye output.
- Single-sample OpenXR eye outputs on Vulkan also omit the visibility selection image
  (`Advanced.Visibility.Selection`, R32_UINT, about 55 MB per eye output at 2688x2688)
  while no visibility debug output is on. Only editor picking (which rejects eye
  instances) and the selection debug view read it; the editor outline uses the
  selection bit in the metadata image. The visibility framebuffer then has identity,
  metadata and depth-stencil, the stage requests carry an empty selection name, and the
  raster pipelines are built with two color attachments; the fragment shaders' selection
  output has no attachment and is discarded. Multisample visibility and OpenGL keep the
  image.

### Edge half-rate shading for eye outputs

Lens optics blur the outer part of each eye image, so full-rate shading there
costs time that the user does not see. The `VrAdvancedEdgeHalfRateShading`
engine setting (off by default) makes Advanced eye outputs shade the outer part
at half rate on each axis:

- It applies only to `AdvancedRenderPipeline` instances with the OpenXR eye
  profile, on Vulkan, without MSAA. The desktop output and offscreen captures
  always shade at full rate. OpenGL eyes ignore the setting.
- The full-rate region is centered on the lens center of each eye: the point
  where the view direction meets the image. The kernel computes it from the
  unjittered projection of the view record. With asymmetric eye frusta it is
  not at the texture center (on most headsets it is nearer the nose).
- `VrAdvancedEdgeHalfRateRadius` (default 0.6) sets the radius in clip NDC,
  where 1 is the half extent of the image on each axis. The push flags carry
  it in steps of 0.05.
- Outside the radius, the top-left pixel of each 2x2 block (the
  representative) shades. A pixel of the block that shows the same draw and
  primitive (a follower) skips `ShadeNativeOpaque.comp`. The shading kernel packs
  the remaining pixels into the first invocations of the work group, so whole
  subgroups of followers stay idle.
- `FoveatedQuadFill.comp` then copies HDR color, velocity, reactive mask,
  shading diagnostics and the optional material exports from the representative
  to each follower. A pixel whose identity differs from its representative
  shades itself, so triangle and object edges stay per pixel and colors do not
  bleed across them.
- Depth, visibility, classification, ambient occlusion and shadow maps stay at
  full resolution. TSR reads the filled color and velocity as usual. It needs
  no other input change.
- The follower rule is in `FoveatedQuadShading.glslinc`. It is a pure function
  of the visibility identity, the view record and the push flags, so the shading
  kernel and the fill pass always agree. RVC foveated quad shading uses the
  same rule with its own radii and 4x4 blocks.
- `VrAdvancedEdgeHalfRateDebugTint` tints the half-rate blocks green. Only a
  representative that at least one follower copies gets the tint, so the tint
  marks exactly the pixels that shade at half rate.

The unit-testing world sets the three values with `VR.AdvancedEdgeHalfRateShading`,
`VR.AdvancedEdgeHalfRateRadius` and `VR.AdvancedEdgeHalfRateDebugTint`.

Measured cost at 2016x2240 for each eye (Sponza with an avatar, both eyes, radius
0.6): about 70% of the pixels are in half-rate blocks, but native opaque shading
drops only from about 10.4 ms to 9.8 ms, and the fill pass adds about 0.2 ms. The
setting therefore stays off by default. In these measurements the shading cost
follows the screen area that the work groups cover, not the number of shaded
lanes: a group that shades the representatives of a tile costs about as much as a
group that shades the full tile. A dispatch that packs the representatives of four
tiles into one work group cost about four times as much for each group, so it was
not faster than full-rate shading. Why the cost follows the area is not proven. One
hypothesis is that the fetches of a group are bound by the sample spacing; a test
with texture gradients times two (one mip level coarser) recovered only a small
part of the texture cost, which does not agree with it.

### Headset shadow quality

One engine setting lowers the directional shadow cost of the eyes. It changes only
the headset cascade records (`HmdSource`) of the primary directional light, that is
the first directional light of the world. The desktop records, the cascade rendering
and the shaders stay the same.

- `VrAdvancedShadowFilterTaps` (0 to 32, default 0) replaces the filter sample count
  and the Vogel tap count of the light. The soft shadow mode of the light still
  selects the pattern: `FixedPoisson` uses up to 16 taps, and `Hard` uses 1 tap or a
  4-tap tent, so 2 gives 4 taps there.
- 0 uses the values of the light.

The unit-testing world sets it with `VR.AdvancedShadowFilterTaps`. The contact sample
count needs no headset setting: a directional cascade fetches the stored depth once
for all contact steps, so the sample count changes only arithmetic (see the contact
march in the default render pipeline notes).

Measured gain in native opaque shading at 2016x2240 for each eye (New Sponza with an
avatar, both eyes; the sun uses `ContactHardeningPcss` with 8 filter samples and 16
contact samples over 1 m): 2 taps about -0.6 to -0.7 ms, 2 contact samples about
-0.4 ms, both about -1.0 to -1.3 ms. With 2 taps the sun patches on the floor are
brighter and their edges show steps. Distant shadows change little. 2 contact samples
give little visible change.

### Directional shadow cache per work group

`ShadeNativeOpaque.comp` defines `XR_ADV_SHADOW_GROUP_CACHE`. At the start of each work
group (after the returns that are the same for the whole group), one invocation finds the
first directional light with shadows and resolves its shadow handle, root record and cascade
range. Then one invocation per cascade (up to 8) loads the cascade record, tests the view
mask and residency, and resolves the shadow texture reference. The results go to shared
memory. `XR_ADV_EvaluateShadow` uses this cache for that light and view, with the same
cascade order, blend band, filter and shadow reason codes as the uncached path. Other
lights, a light with more than 8 cascades, and the MSAA kernel keep the uncached path.
At 2688x2688 for each eye this saves about 0.5 to 0.8 ms of native opaque shading.

The Uber kernel (`ShadeNativeUber.glslinc`) evaluates the shadow of the primary directional
light once, before its light loop, because the source-lighting modes need that value. The
light loop visits the same light again with the same direction. It uses the stored value
instead of a second soft-shadow filter. Only the other lights evaluate their shadows in the
loop.

### Hidden-area tile reject

An OpenXR runtime with `XR_KHR_visibility_mask` reports, for each eye, the part of the eye
image that the headset lens never shows. The Advanced stages of the OpenXR eye views (Advanced
and RVC eye pipelines) skip native shading there:

- `AdvancedHiddenAreaTiles` reduces the runtime meshes of each eye to one conservative ellipse
  in eye-image UV. Every visible pixel lies inside it: the ellipse encloses the visible-area
  mesh, or, when the runtime gives only the hidden-area mesh, every cell of a 128x128 grid that
  the hidden triangles do not fully cover. The ellipse is computed again only when the mask
  revision or the field of view changes.
- `VPRC_AdvancedRenderStage` puts the ellipse of each eye view into the work classification and
  native shading requests (`AdvancedHiddenAreaEllipses`). A stereo instance maps view 0 to the
  left eye and view 1 to the right eye; a mono eye instance uses view 0. The desktop output and
  MSAA visibility get no ellipse.
- The Vulkan recording writes the ellipse of the view into push fields that `ClassifyTiles.comp`
  and `ShadeBackground.comp` do not otherwise read (`HiddenAreaTiles.glslinc`). Classification
  returns early for a 16x16 tile that lies fully outside the ellipse, so the tile gets no kernel
  membership and no shading. The background pass writes that tile as opaque black with zero
  velocity and reactive mask. TSR history, ambient occlusion and bloom then read a stable,
  defined value, and the sky pass does not draw there.
- When the runtime gives no mask, the reject is off. The engine writes one line to
  `advanced-hidden-area.log` that says so, and one line for each eye each time its ellipse
  changes: the mask hidden fraction, the ellipse, and the image fraction outside the ellipse.
- `XRE_TEST_HIDDEN_AREA_MASK` replaces the runtime mask with a synthetic circle for runtimes
  without a mask, such as the Monado bench. It is a test hook only, and every session that uses
  it writes a warning to the log. See [OpenXR runtime](../../developer-guides/vr/openxr-runtime.md).

The visibility raster, ambient occlusion, TSR and post-processing still process the hidden
tiles. Only classification and shading skip them.

### Eye render scale

With TSR as the anti-aliasing mode of the eyes, `TsrRenderScale` (0.5 to 1.0, or the
`TsrRenderScaleOverride` of the eye camera) renders the eyes below the swapchain size.
The temporal upscale (`TemporalSuperResolutionStereo.fs`) then writes the output at
the swapchain size:

- Everything before TSR runs at the internal size: the native stages (visibility
  raster, depth pyramid and meshlet occlusion, tile classification, ambient
  occlusion, native opaque shading, the fill pass) and the stereo screen-space
  passes (ambient occlusion, bloom, post process, final post process). TSR is the
  last pass of the chain. It reads the internal size and writes the output size.
- The view set describes each eye by its output rectangle (for OpenXR, the swapchain
  size). `BackendReadyFramePackage.ApplyCanonicalViewPolicy` therefore gives each
  canonical view whose rectangle is the package viewport the internal size of the
  package (`RenderWidth` and `RenderHeight`). `AdvancedViewRecordFactory` writes it to
  `renderSizeAndInverse`, which the native stages use to map their pixels to clip
  space. `outputSizeAndInverse` stays the output size. Without this split, the native
  stages read an internal target as if it had the output size, which zooms the image
  out and reads rows outside the target.
- The scale applies only when the eye has TSR history. The frame profile of the eye
  pipeline instance (`XRRenderPipelineInstance.ApplyCurrentFrameProfile`) resolves the
  temporal history policy for that instance and its stereo flag before it sets the internal
  size. A view without history keeps its full internal size, because a smaller image without
  TSR is only a plain upscale.
- Per-eye OpenXR eyes (`SequentialViews`, `ParallelCommandBufferRecording`) on Vulkan use the
  scale when the eye runs the Advanced stage family with a bound output (Advanced, or the
  Advanced stage family of RVC). `VPRC_TemporalAccumulationPass.ResolveHistoryIsolationPolicy`
  gives such an eye the `PerEye` policy: each eye has its own viewport and pipeline instance,
  so the temporal state of the instance holds only that eye. `VrViewRenderModeResolver` reports
  the same `PerEye` policy for Vulkan Advanced and RVC per-eye modes, so OpenXR keeps the eye
  anti-aliasing mode (it sets the eye cameras to no anti-aliasing only when the resolved policy
  has no history). The per-eye viewport keeps the
  swapchain size as display size; the internal size can be smaller. The Vulkan per-eye checks
  (`ValidateOpenXrEyeViewportExtent`, `ValidateOpenXrExternalFrameOpContexts`) accept an
  internal size between 1 and the swapchain size.
- Other per-eye swapchain eyes (the Default pipeline, and every per-eye eye on OpenGL) have
  no TSR history. They render at the swapchain size without temporal anti-aliasing, and the
  pipeline instance writes a `[TemporalHistory]` warning every 10 seconds that names the
  pipeline and the reason.
- Stereo passes and OpenXR eyes ignore the DLSS/XeSS scale, because the vendor upscalers
  run only on mono views that do not write an external swapchain image.
- Per-eye eyes with a scale below 1 are not yet validated on a headset. OpenGL and WebGPU
  canonical views do not get the internal size.
- With another anti-aliasing mode, or at 1.0, the eyes render at the swapchain size and
  the image does not change.
- The engine default is TSR at 0.75. Set `TsrRenderScale` to 1.0, or set the
  `TsrRenderScaleOverride` of the eye camera, to render the eyes at the swapchain size.

Vulkan GPU timing names each fullscreen pass of the post chain after its fragment shader,
so the TSR, FXAA, bloom, ambient occlusion and post passes have their own rows:

- Eyes: `Stereo > Fullscreen > <shader>` in the stereo pipeline dump, for fragment shaders
  whose file name ends with `Stereo`.
- Mono views such as the desktop: `Fullscreen > <shader>` for draws of an
  `XRQuadFrameBuffer` (mesh renderer name `FullscreenQuad:...`). A shader without a file
  uses the material name.
- The DLSS evaluate: `Vendor > DlssEvaluate`. On mono views it writes
  `VendorOutputTexture` directly, and the window present that follows it is the same
  `VPRC_RenderToWindow` draw as for the TSR output (see
  [Vulkan upscale bridge](../../developer-guides/rendering/vulkan-upscale-bridge.md)).

`VulkanRenderer.CommandBufferRecording.Primary.FullscreenPassTiming.cs` builds these paths.

### Scene registration identity reuse

Native Uber material reuse retains the result of strict shader validation.
`AdvancedUberValidationStamp` records the canonical shader, source text and
identity, provider owner, runtime shader service, asset epoch, and snippet
version. Reuse requires the same material revisions and exact variant handle.
Changed inputs require strict validation again. Prepared and unchanged-row
checks use the same stamp. Successful preflight retains a refreshed stamp even
when unchanged publication skips the table commit. This metadata update does
not change GPU rows or material reference counts.

`AdvancedGpuScenePublisher` retains the registration lookup and an ordered
array of source identities. Each row records the source reference, primitive
index and count, support state, source group, registration slot, and complete
draw handle. Reuse requires the same
command count and registration membership generation. Each present row must
still match an active registration and its draw generation. An absent source
must match an absent cached row.

Registration addition, slot reuse, removal, lookup growth, and publication
failure invalidate this cache. A new ordered array becomes valid only after
commit and successful source identity delivery. Successful publication reuse
follows the same delivery rule. Shrink and disposal clear retained source
references.

The publisher also retains source groups and flat primitive-to-draw handle
slices. Reuse requires exact ordered rows and primitive counts, unchanged
registration membership, and no pending identity recipients. A mismatch
rebuilds the whole grouping from active registrations and the current captured
plans. It does not repeat source capture. Registration or lookup storage growth
invalidates the relevant plan before commit. Scene-table journal growth alone
can preserve these identities and groups. Current unsupported sources receive
invalid handles.

Cold delivery preserves registration order, then current command order.
Removed sources receive cleared handles. Primitive shrink first clears the
removed tail; the next stable group uses the smaller handle count. A failed committed delivery retains
all recipients and their required clear lengths for a later caller retry.
Preflight reserves this storage before commit. A rejection cannot discard it.
Full successful delivery clears the pending list. Membership-change and retry
groups cannot become the reusable plan; the next stable publication builds it.
Callback exceptions still propagate to the caller. In the normal editor loop,
they cause a terminal timer fault, so automatic retry is not guaranteed.

These caches replace identity searches and grouping work. When the unchanged-row
gate below does not pass, the publication captures current source state, command
metadata, effective materials, poses, and GPU bounds sources. Existing geometry,
material, temporal, resource, and transaction checks remain active. Structural
transaction plans are rebuilt. Every identity callback still receives the
current accepted publication. No frame resource or output-page lease is stored
in these caches.

### Unchanged-row gate

Before the whole-scene plan, the publisher compares each command row with the
retained plan of the last accepted publication. For each row, the gate reads
the scene command row and source lookup, the command render snapshot, the
primitive mesh and effective material, the raster state, the cull bound version
of a CPU-owned bound, and the registration. It checks the material header one
time for each run of rows with one material, because the material versions do
not cover every header input. For an unsupported row, the gate repeats the
geometry check and the layout selection, and it requires the same compatibility
reason. A covered row requires the same renderer bone set. One read of the
GPU physics-chain dispatcher spatial epoch covers all covered rows, and the read
fails while the published output page is not usable.

The gate passes only when all of these conditions are true:

- The retained plan describes the current publication.
- The ordered identities and source groups are valid, and no identity recipient is pending.
- The frame identifier follows the last published frame.
- The boundary storage did not grow.
- Each supported row reused its committed material plan. Its material is untextured,
  uses a supported `NativeUberV1` or `StandardLitTextureV1` layout, or has no engine
  semantic (a legacy material), with current 2D or CPU-backed cube bindings.
- Each active registration belongs to one row.

Any other difference runs the full plan with no change in behavior. Thus a
rejected bounds route rejects in the full plan with its usual reason.

### Moved-row patch

A moving object no longer forces the full plan. The swap-time gate walk
(`TryPassUnchangedRowGate`) accepts a supported row whose only differences are
its world matrix, its previous world matrix, and (for a CPU-owned bound) its cull
bound version, when the planned draw row is a dynamic shadow caster
(`GPUIndirectRenderFlags.ShadowCasterDynamic`). Every other row check still
runs, including frame continuity (`LastSeenFrameId + 1`), the source flat
direction, the material, and the registration. The walk stores the new matrices
and bound of each moved row in fixed storage of `MovedRowPatchLimit` (64) rows.

The publication then commits the retained rows (a reuse cannot change content)
and, inside the transaction, `ApplyMovedRowPatches` replaces the current and
previous transform records and the instance record of each moved row, as the
content update of the full plan does. It recomputes the content signature with
`ComputeContentSignature`, writes the new matrices, bound, and signature to the
retained plan entry and the registration, and advances the content generation.
So the next gate compares against the published state. The submission records
hold no matrices and stay unchanged, and every row stays temporally valid
because the gate proved frame continuity and unchanged topology.

The full plan still runs when:

- a static row moves (`StaticRow`). A transform change promotes a static caster
  to dynamic, and the swap that carries the new flag changes the draw row, so
  the row takes the full plan once and qualifies after that;
- more than `MovedRowPatchLimit` rows moved (`LimitExceeded`);
- the publisher has no retained submission rows (`NoRetainedRows`);
- the transform or instance table cannot take the replacements, or a moved row's
  records are not current (`TableCapacity`, `RegistrationNotCurrent`).

The prepare and the prepared-verdict consume never accept a moved row; a moved
row makes the verdict miss, and the swap-time walk handles it.
`get_render_profiler_stats` reports
`frame_lifecycle.collect_stages.swap.advanced_publication.moved_row_patch`
(`moved_row_patches`, `moved_rows_patched`, and `moved_row_fallbacks` by
reason), and `get_render_state` reports the latest patched row count as
`canonicalResidentScene.movedRowPatchCount`.

Native Uber reuse keeps the five material revision checks and the committed
layout, coverage, render-state, and header checks. It encodes the current texture
roles through the same role-only encoder as the full plan and compares each
source with its committed logical binding. This includes the imported metadata
epoch, source-content generation, dimensions, format, and sampler state. The
check repeats when consuming a prepared verdict. Scalar encoding is skipped
only while these checks pass. Standard lit texture reuse captures the same four
legacy texture roles and six engine-surface roles as the full plan. It checks
each committed logical binding and the engine-surface layout and scalar values.
Publisher-owned scratch storage is cleared after each check. The existing five
material revisions, material variant, current handles, header, and render-state
checks still apply.

Legacy material reuse (no engine semantic, for example an imported material with a
height, specular, alpha-mask or emissive map) requires no engine-surface or Uber
companions. It encodes the six legacy texture slots again (from
`SurfaceTextureBindings` when the material has them, otherwise from the legacy
texture list) and compares each with its committed logical binding. It also packs
the constant row again and compares it with the committed row, because one texture
property (the emissive format) feeds that row without a material version. Without
this path, New Sponza (height-mapped materials) failed the unchanged-row gate on
every publication.

The material source witness that skips these checks hashes, for every texture of
both binding paths, its content counters and `AdvancedSourceVersion`, so a sampler,
LOD-clamp or streaming change of a surface-binding texture also breaks the
witness.

GPU-authored cubes without a content witness, arrays, projective mirrors, and other
textured material families keep the full plan. Native readiness and descriptor
ownership checks remain at their existing render boundaries.

CPU-backed cubes retain an exact witness for the mip array, mip and face
identities, CPU data identity, dimensions, and formats. `Mipmap2D` records active
field writers and a content epoch. `Invalidate` advances this epoch after direct
CPU writes. Callers that write through a data pointer must call `Invalidate`.
A changed witness produces a new cube source generation. Stable reads do not
allocate. Storage images, framebuffer attachments, and cubes without complete
CPU face data cannot use this reuse path. This witness does not replace native
texture preparation or descriptor lifetime checks.

The cube face check runs once per publication pass. `AdvancedSourceWitnessEpoch`
marks a pass on its thread, and the cube keeps a memo for the rest of the pass.
A reader checks an unchanged cube without the cube lock: a sequence counter is
odd while the lock holder changes the observed faces, and the reader accepts its
reads only when the counter is even and the same before and after them.

#### Material source witness

The texture-source encode of the committed-binding check is skipped while a
material witness is unchanged. `WitnessedCommittedMaterialBindingsMatch` is used
by the gate row check, by the prepared-verdict revalidation
(`AreMaterialWitnessesCurrent`), and by the material transition reuse check. The
witness (`AdvancedMaterialSourceWitness`) holds:

- the material and layout objects and the five material revisions;
- `XRMaterialBase.TextureSourceVersion`, which advances when a bound texture
  raises `XRTexture.AdvancedSourceChanged`;
- a hash of each bound texture's own counters: for a 2D texture, the source
  content generation, the imported metadata epoch, the mip array identity and
  length, and the base mip identity, content epoch, size, and format; for an
  array, the layer content witness; for a CPU-backed cube, the cube source
  generation;
- the resource publisher `CommitGeneration`, which advances on each texture or
  sampler record write or release;
- the material database generations;
- a hash of the committed bindings and surface records that the check compared.

`XRTexture.AdvancedSourceVersion` advances on every texture property change, on
sampler and format metadata writes that publish no notification, and on a change
of the progressive-upload state. A material with a texture that has no witness
contract (a texture view, a GPU-authored cube or array, or an unknown type)
always takes the full check.

Each writer advances its counter after the data write. Thus a texture write on
another thread that races the witness read leaves a different witness for the
next pass, which then takes the full check. A change can be one pass late, but
it is not lost. Set `XRE_ADVANCED_PUBLISHER_WITNESS_VALIDATE=1` to run the full
check also when the witness is unchanged. A mismatch logs a warning and returns
the full-check result. A hit that a concurrent texture write changed during the
check is counted separately and is not a failure.

When the gate passes and no global resource changed, the publisher reuses the
current publication and delivers the identities again, as the full reuse path
does. The reuse applies the same lease rule as the full reuse path, so the
publisher does not reuse a publication that the ring drains. When a global
resource changed, or when the ring drained the publication, the publisher
commits the retained submission rows with the global transition. This commit
skips the per-command apply, tombstone, and committed-plan capture passes. Every
source still receives the new publication identity. The commit needs the rows
of a full commit, so after a full-path reuse the next commit runs the full plan.
The retained rows include the managed deformation-source rows. The commit reuses
their browser companion data and renderer rejection text without a new
computation, as the full reuse path does.

The existing `get_render_state` response exposes
`canonicalResidentScene.registrationIdentityReuseCount` and
`registrationLookupRebuildCount`, plus `sourceGroupReuseCount`,
`sourceGroupRebuildCount`, and `pendingIdentityRecipientCount`, for the latest
publication attempt. `unchangedRowGateHitCount` gives the rows that the latest
publication reused without the full plan; zero means that the full plan ran.
`retainedRowCommit` is true when the latest publication committed the retained
rows. The `GpuIndirect.AdvancedPublication.UnchangedRowGate` profiler scope
times the gate. These live
counters can be read while the publisher is updating them. Use repeated samples
for diagnostics; do not treat one response as an atomic publication snapshot.

`AdvancedGpuSceneIdentityCacheTests` and
`AdvancedGpuSceneIdentityCacheGrowthTests` check real scene publication without
a graphics device. They cover exact source-to-handle mapping, warm-cache
invalidation, capacity growth, missing and unsupported sources, failed delivery,
and cache reference cleanup. `AdvancedGpuSceneIdentityDeliveryBoundaryTests`
checks failures after commit and during reuse of a pinned publication. A
successful caller retry must deliver the accepted publication and clear every
pending recipient before the image becomes consumable.
`AdvancedGpuSceneUnchangedRowGateTests` checks that an unchanged scene skips the
full plan, that a pinned publication is reused, that a global change commits the
retained rows unchanged, and that material, raster, geometry, static-row
transform, mesh, header, frame-gap, and membership changes run the full plan. It
also checks that a moved dynamic row is patched without the full plan (same draw
handle, new content generation, the plan entry follows the published row) and
that a static row that starts to move runs the full plan.

### Prepared unchanged-row verdict

The gate walk can run on the collect thread before the swap. After
`PostCollectVisible`, `EngineTimer` raises `PrepareSwapPublications`.
`RuntimeWorldRenderer.GlobalPrepareSwapPublications` calls
`GPUScene.PrepareAdvancedResidentPublication`, which runs the same row evaluator
as the swap-time gate (`EvaluateUnchangedRow`) and stores a single-use verdict.
At the swap, `PublishCore` first tries to consume the verdict and runs
`TryPassUnchangedRowGate` only when the verdict did not decide. The commit is
unchanged and stays in the swap: boundary capacity, tombstone reclaim, the
global preflight, the transaction, the stamp, the seal, the captures,
`_currentPublication`, and identity delivery. The global capture is final only
after the light swap, and render frame N still reads publication N.

Every input that the gate reads has a witness that the prepare samples before
the first row read. Each cross-thread writer advances its witness after the
write:

- `GPUScene` row-input version: every draw row, bound, lookup, count, render
  snapshot, and capacity write. The swap records it right after `CopyStreams`,
  under the scene lock, and the prepare requires the same version with empty
  dirty ranges. The prepare reads render-side rows only through
  `TryGetAdvancedRowInputs`, under the scene lock.
- Static epochs: late render-thread world writes, GPU-driven bone state and
  coverage, and mesh geometry revisions.
- Per row: the command `MutationVersion`, and a mesh witness for each run of rows
  with one mesh (vertex count, index count, geometry revision, primitive type,
  triangle count).
- Per material and per raster option set: the values that the evaluator compared.
- The publisher epoch (advanced only when a publication ends), frame continuity,
  the settings bits, and the dispatcher bounds epoch.

A row is evaluated again at the consume, instead of trusted, when its source is
not a `RenderCommandMesh3D`, when its renderer has no current mutation binding,
when its material override differs from its snapshot, when its command is dirty,
when its command version changed, or when it is an unsupported row. Any other
witness change is a miss, and the swap runs today's gate, which falls back to
the full plan. The prepare takes the collect publication lock first and the
scene lock only for one row at a time. It never calls the committed spatial
query, so the recorded lock order is unchanged. An exception in the prepare
discards the verdict and is recorded; it never stops the collect thread.

`XRE_ADVANCED_PREPARED_GATE=0` disables the prepare; the swap then runs exactly
today's gate. `XRE_ADVANCED_PREPARED_GATE_VERIFY=1` also runs the swap-time gate
after each hit, publishes its result, and counts each disagreement with the
witness that should have caught it. `get_render_profiler_stats` reports the
counters in `frame_lifecycle.collect_stages.swap.advanced_publication.prepared_gate`:
prepares, refusals and misses by reason, hits, re-evaluated rows, verify
disagreements, and the last exception.

The same stats object splits `commit_ms` for the full publications: `plan_ms`
(row plan, geometry staging, global preflight, and unchanged-publication check),
`apply_ms` (transaction apply), and `seal_ms` (table snapshots, captures, commit,
and identity delivery). `global_preflight_ms` is the global-resource preflight of
every publication path, and `identity_delivery_ms` is the part of `seal_ms` that
delivers the source identities. On New Sponza with an animated avatar, a full
publication spends about 1.5 ms in the plan, 0.1 ms in the apply, and 0.6 ms in
the seal; the global preflight and the identity delivery are about 0.05 ms each.

With `XRE_ADVANCED_SCENE_PUBLICATION_DIAGNOSTICS=1`, the swap gate also writes a
bounded sample of its row failures to `advanced-scene-publication-diagnostics.log`:
`row-input-miss` names the compared value (`World`, `PreviousWorld`, `Command`,
`BoundsVersion`, `RowLookup`) with the mesh, material, and owner type of the row
(a `World` or `PreviousWorld` difference is `RowMoved` in the swap-time evaluation, where
a command swap delivered new matrices, and `LateWorldWrite` only in the prepare, where the
row inputs are unchanged since the last swap),
and `unsupported-miss` names the failed step of an unsupported row (`Geometry`,
`Layout`, or `LateRejection` when the full plan rejected the row after the layout
selection) with both compatibility reasons.

An unsupported row that the native Uber material encoder rejected
(`UnsupportedResourceBinding`) is unchanged when its captured inputs are
unchanged: it still has no registration, its draw row, renderer, mesh and
material are the planned ones, the mesh geometry values and the material binding
layout, value and resource versions, shader state revision and Uber state
revision equal the plan, and the raster state of its render options is equal.
Each input of that encoder advances one of these values (the engine semantic
advances the shader state revision, and every `UberAuthoredState` change
advances the Uber state revision). Rows that the resource encoder rejected for a
texture state, such as an empty texture, depend on texture contents that no
material version tracks, so they keep the geometry and layout comparison and
still fail with `LateRejection`. `late-world-write` lines come from
`RenderCommandMesh3D.ReportLateRenderWorldWrite`: each sampled render-thread
`WorldMatrix` write that advances the late-write epoch (and so fails the gate
with `LateWorldWrite`) gives the command type, the owner type, the mesh name,
the stable query key, and the first five callers of the setter. The sampler
writes the first 12 writes, then 1 in 128. `swap_gate_failures` counts each
world-swap publication whose swap-time gate failed, by the reason of the first
row or check that failed. Each of these publications plans and commits the whole
scene, so these reasons name what keeps the scene off the unchanged-row path.

### Deformation source identity

`AdvancedPreparationExtractor` keeps an `AdvancedDeformationSourceSet` for each
scene. It compares the unique meshes in the bounded captured submission, not
the scene topology version or material signatures. Each
`AdvancedDeformationSourceWitness` includes geometry revision and dimensions,
primitive type, meshlet count, preparation policy, skin-buffer identity and
revisions, and morph-buffer identity and revisions. Membership order does not
matter. Additions, removals, or changed inputs advance the source generation.
Missing geometry and conflicting captures are rejected. A failed scan cannot
leave partial changes under an accepted generation.

Static GPU generations use scene identity, database epoch, deformation source
generation, and preparation policy. Camera, texture, and material changes can
therefore retain unchanged deformation input data. Output pins, copy-on-write,
and per-mesh witness checks still protect GPU consumers. A membership change can
reuse a donor from the same scene, database epoch, and policy when every retained
mesh witness is current. An unpinned donor changes its source-set key. A pinned
donor keeps its GPU buffers while an unpinned successor adopts its managed rows,
witness maps, and payload index. Reuse stops when the retained mesh or payload
count exceeds the configured deformation-job limit. A changed witness uses fresh
preparation. Renderer-owner state
continues to reset on scene topology changes because draw owners can change
without changing a shared mesh. Source-set storage reserves the bounded union
of old and new membership to avoid allocation during replacement.

`get_render_state.advancedPreparation` reports `deformationSourceGeneration`
and `deformationSourceMeshCount` for the last preparation attempt.
`deformationSourceChanges` identifies added/removed meshes and changed input
fields in the last transition. `deformationSourceChangedMesh` names the first
changed mesh when available. These fields
describe the selected inputs. The publication and deferral fields show whether
preparation completed. An output-arena allocation failure defers the entire
preparation attempt until boundary growth succeeds. A diagnostic slice must not
be published as GPU-deformed output.

### Extraction lookups

`AdvancedPreparationExtractor.Build` reads each skinned draw's mesh preparation
once. The pre-pass `TryPrepareCanonicalDeformationMeshes` keeps a ready mesh
slice for each command (`AdvancedPreparedDeformationMesh`), and consecutive
rows that draw one shared mesh use one preparation. `TryAddDeformation` reuses
the stored slice while `AdvancedGpuDeformationResources.MeshSliceRevision` is
unchanged and the mesh keeps its geometry revision, vertex count, and topology
generation. Otherwise it calls `TryPrepareMesh` again. The revision advances
when a frame opens, when the static generation changes or resets, and when a
mesh slice is added or removed. Slice offsets do not move in other cases:
copy-on-write keeps them, and a commit only appends rows.

A covered draw captures its retained renderer output state once. The bounds
route material check, the pose, and the history check use the same capture.
`PhysicsChainMaterialBoundsContractMemo` keeps up to four material bounds
contracts for one `Build`, keyed by the published material snapshot. A hit still
requires the material to be current. The memo is cleared for each `Build`, so a
shader change that does not advance a material version is seen in the next
frame. `PhysicsChainMaterialBoundsContractMemoTests` cover it.

A covered renderer without active blendshapes takes a fast path in
`AdvancedGpuDeformationResources.TryGetOrAddPoseCore`: it skips
`XRMeshRenderer.TryPrepareDeformationInputs`, because the retained palette
replaces the renderer palette and the retained envelope proves the mesh,
geometry revision, bind root, and bone layout. The full path runs until the
renderer records GPU-owned bones (`HasGpuOwnedDeformationPose`), so a later loss
of coverage still resets the CPU palette seed state. Covered rows also skip the
CPU animation scheduler and use bone tier 0.

### Visibility content generation

After the indirect planner, `Build` compares its visibility columns with the
columns of the last compared build (`AdvancedVisibilityContentWitness`): the
payloads, the geometry producers, the indirect ranges, the range payload order,
and the deformed-arena slices. The frame slots of a slice change in each frame,
so the comparison ignores them; it compares the slice offsets and the other
fields. `StableVisibilityContentGeneration` keeps its value while every column
is equal, and it takes a new value from one process-wide counter when any column
changes. `VisibilityContentGeneration` is different: it advances on each build
and guards the mutable extractor columns. A deferred build does not compare, so
the next successful build compares with the last successful content.
`AdvancedSharedPreparationService.GetCurrentDiagnostics()` reports the
cumulative counts in `VisibilityContent`: comparisons, unchanged comparisons,
and the builds in which each column changed.

### Vulkan visibility stream reuse

Each Vulkan frame plan owns the stable-bin stream of its visibility family
(`VulkanPreparedStableBinStream`). Frame plans are pooled for each frame slot,
so one stream serves every second frame on desktop. The stream keeps its sealed
records, bin headers, manifests, and submission plans across frames when every
seal input is equal.

Indirect ranges and bins are keyed by the raster state class, the coverage, the
cull mode, the topology, the geometry producer, and the deformed-vertex flag.
Indexed and meshlet producers (`IndirectIndexed`, `StaticMeshlet`,
`SkinnedMeshlet`) read their geometry from the shared atlases through each
payload: the index atlas, the static vertex atlas, and the meshlet descriptor
table. Their keys hold no geometry handle
(`AdvancedIndirectRangeKey.RangeGeometry`), so the payloads of different meshes
share one range, one bin, and one indirect draw. The visibility identity (draw,
meshlet, and primitive) comes from the payload that the task and mesh stages
read, not from the bin. CPU-direct producers bind the vertex and index buffers
of one mesh, so their keys keep the geometry handle. Before meshlet ranges were
merged, New Sponza produced about 640 one-mesh bins for each view, and the eye
frame spent about 3.6 ms of CPU recording them.

Records and bin keys contain no frame binding:

- A deformed record does not hold the deformation output buffer. The deformed
  arena rotates three frame slots, so that buffer changes in each frame. The
  record keeps only its vertex base inside the buffer
  (`VulkanVisibilityGeometryRecordClosure.UsesDeformedVertexSource`), and its
  manifest has no native use for the buffer. The bin key uses a fixed vertex
  binding signature for deformed bins.
- Records and keys do not contain the scene native generation, which advances
  with each scene publication preparation. Each frame binds the generation to
  the stream (`BoundSceneNativeGeneration`).
- The static vertex and index slices come from the resident geometry cache.
  They keep their identity while the cache entry stays resident.

When the frame plan resets, `RetainForNextPlan` releases the template leases and
clears the sealed state of the plan, but it keeps a valid seal. The next seal
calls `TryReuseVisibilitySeal`. It keeps the stream only when these inputs are
equal:

- `VulkanVisibilitySealSignature`: the database epoch, the draw and geometry
  table generations, the resident static vertex and index slices, the pass
  index, the view mask, the frame-operation context of the bin keys, the
  submission strategy, the lane capabilities, the output policy (with the output
  reservation), the diagnostic plan, and the deformation history validity.
- An exact row comparison of the visibility columns: payloads, producers,
  indirect ranges, range payload order, and deformed-arena slices without their
  frame slots. A deformed slice must also name the frame slots of this frame's
  deformation output.
- This frame's deformation output must cover the largest vertex extent of the
  kept records.

A kept stream binds the frame: the deformation output buffers, the scene native
generation, and the geometry sources. It clears the raster pipelines, header
native state, and shadow pipelines, which the raster pipeline step prepares
again for the frame's target closure. That step binds the frame's deformation
output into each deformed bin header. It validates range members once for each
sealed content, because a kept stream has equal records and payloads. Recording
checks each bin once: the stream must be bound to the scene publication of the
frame, the bin's sources must be live, and the header must bind the vertex
source of its records. The records of a bin share one index slice and vertex
source through the bin key, so one check covers every member.

Any other difference thaws the stream and seals it again with no change in
behavior. `AdvancedVisibilityStreamReuseTelemetry.Capture()` reports the reuse
count and the reseal count for each first reason
(`EAdvancedVisibilityStreamReuseMiss`).

### Material payload slots

`AdvancedMaterialDatabase` gives each material a fixed constant-word slot and
a fixed texture-binding slot. Adding or replacing a payload writes its active
entries and clears each unused tail. The dirty range covers the whole slot,
including the cleared entries. Adjacent writes merge into one range.

`AdvancedMaterialDatabaseContractTests` checks complete slots for addition,
replacement, and payload shrink. Replacement uses no managed allocation when
capacity is sufficient.

### Metallic-roughness texture channels

The Standard RM slot (texture slot 2) normally holds roughness in red and metallic in
green. A glTF `metallicRoughnessTexture` holds roughness in green and metallic in blue:

- The glTF importer keeps one grayscale texture per role in the legacy sampler slots,
  because the Default shaders read a separate map for each. The `Metallic` and `Roughness`
  surface bindings point to the packed source texture with channels 2 and 1. An ORM
  `occlusionTexture` binds the same texture object.
- When both bindings use one texture with those channels, `MaterialBindingSourceEncoder` sets
  `GPUMaterialEntry.PackedMetallicRoughnessFlag` (bit 30 of the material flags word).
  `XR_ADV_StandardRoughnessMetallic` then reads green and blue. The native opaque kernels,
  the decal buffer, the decal modifier and the WebGPU shading contracts use it.
- Other sources (Assimp and native FBX maps, legacy texture lists) keep red and green.
  A material with separate metallic and roughness textures still binds only one of them
  to the RM slot.

### Material ambient occlusion

The Standard layouts (`OpaqueDeferred`, `ForwardOpaque`, `MaskedForward`) have six
texture slots: albedo, normal, metallic-roughness, emissive, opacity, and occlusion
(slot 5). The occlusion texture comes from a `MaterialSurfaceTextureBinding` with the
`EMaterialTextureSemantic.Occlusion` semantic:

- `MaterialBindingSourceEncoder` publishes it only when the binding exists and
  `XRMaterial.OcclusionStrength` is above 0. It then sets
  `GPUMaterialEntry.OcclusionTextureFlag` (bit 29 of the material flags word) and
  writes `OcclusionStrength` and `OcclusionControls` (channel in bits 0-1, texture-coordinate
  set 1 in bit 2).
- The occlusion words follow the original 36-word row: strength at word 36, controls at
  word 37, and the texture handle at word 38 (`MaterialTextureBinding.PackHandleAfterFields`).
  Every earlier word offset, including `Flags` at word 5, is the same as in a row without
  occlusion, so the Uber, MSAA and repair kernels, WebGPU, and `GPUMaterialTable` keep their
  offsets. The row is 40 words.
- The native opaque kernel samples the texture only when the flag is set, so a
  material without the texture takes no extra sample. The value follows glTF:
  `occlusion = 1 + strength * (texel - 1)`.
- The kernel multiplies it into the screen-space ambient occlusion (product). The
  combined value darkens only indirect light: the world ambient term, probe diffuse,
  and probe specular through `XR_ADV_SpecularVisibility`. Direct light does not use it.
- The glTF importer binds `occlusionTexture` (red channel) with its `strength`. The
  Assimp importer binds `TextureType.AmbientOcclusion` slots. The native FBX importer
  maps texture names with "occlusion" or an "_ao" token to that type, instead of the
  base color default.
- Only the native opaque kernels read the texture. The Native Uber path, the Default
  pipeline, and the WebGPU shading contracts ignore it.

### General debug shapes

`ModelComponent` copies its `RenderBounds` setting into each new runtime mesh.
A model rebuild preserves the requested bounds display.

The Vulkan opaque visibility raster programs (indexed, mesh-shader, mono and
multiview) define `XR_ADV_VISIBILITY_IDENTITY_ONLY`. With this define,
`VisibilityRaster.vert` and `VisibilityRaster.mesh` write only the position and
the flat identity values that `VisibilityRaster.frag` reads (locations 0-5 and
7-10). The masked, fragment-depth and decal programs do not set the define. They
keep the interpolated coverage attributes (locations 6 and 11-16). OpenGL does
not set the define.

Vulkan visibility preparation records its compute stages once for all views of
a family. A desktop view, each per-eye pipeline (Sequential and Parallel eye
recording) and each RVC single-view stage family have one view. SinglePassStereo
and RVC stereo families have two or more.
- `EarlyVisibility.comp`, `BuildVisibilityIndirect.comp` and both
  `FinalizeIndexedInstanceGroups.comp` variants run as one dispatch each with one
  Y workgroup row for each view. The push constants hold the view 0 segment. The
  shaders add `gl_WorkGroupID.y` to the view index, `gl_WorkGroupID.y *
  payloadCapacity` to the payload base, and `gl_WorkGroupID.y *` (range offset
  table length) to the range base (`VisibilityPreparationViews.glslinc`). These
  are the strides of `VulkanAdvancedVisibilityResourceState.TryGetViewSegment`;
  `SupportsMultiViewPreparation` checks them before recording. OpenGL still
  dispatches each view with one row, so the offset is zero there.
- The per-view outputs, counter records and argument segments do not change.
  The views write disjoint segments, so one storage barrier between stages
  serves all views.
- The depth pyramid build and `LateVisibility.comp` bind the images of one view
  through that view's descriptor tables, so they still run once per view. The
  late test results of all views then get one storage barrier, and the late
  group finalize runs as one multi-view dispatch with one more barrier.
- The late test uses `vkCmdDispatchIndirect`. `BuildVisibilityIndirect.comp`
  writes one dispatch record per view after the indexed arguments of all views
  in the early argument stream (`TryGetLateDispatchArgumentOffset`; 16 bytes per
  view). The X count is `max(1, ceil(max(early, deferred) / 256))`, with both
  counts bounded by the payload capacity. At least one workgroup runs, because
  the late test also clears the view's current meshlet history set. The early
  raster read barrier makes the records visible to the indirect dispatch.
- Dispatch and barrier counts for the visibility preparation of one family with
  V views and indexed instance groups (single-sample, with the depth pyramid):

  | Stage | Dispatches before | Dispatches now | Barriers before | Barriers now |
  | --- | --- | --- | --- | --- |
  | Early, indirect, group finalize | 3V | 3 | 3 | 3 |
  | Depth pyramid build | V | V | 3V image | 3V image |
  | Late test | V (over the capacity) | V (indirect, live count) | V | 1 |
  | Late group finalize | V | 1 | V | 1 |

  Without indexed instance groups, the group finalize rows have no dispatch and
  no barrier.

The Vulkan meshlet visibility raster has a task stage, `VisibilityRaster.task`,
in front of `VisibilityRaster.mesh`.
- `BuildVisibilityIndirect.comp` and `LateVisibility.comp` write
  `ceil(meshletCount / XR_ADV_TASK_MESHLETS_PER_GROUP)` task workgroups per
  visible meshlet draw. The constant is 32 and is defined in
  `VisibilityInterface.glslinc`.
- Each task invocation tests one meshlet. The frustum test uses the cooked
  bounding sphere against the planes of the unjittered view-projection matrix
  of the push view.
- The cone test uses the cooked normal cone. It runs only for static,
  back-face culled draws with a positive-determinant, uniformly scaled world
  matrix and a non-degenerate cone. Skinned meshlets are never culled, because
  their cooked bounds describe the bind pose.
- The survivors are compacted in lane order into the task payload: the payload
  row plus up to 32 global meshlet indices. The task stage launches one mesh
  workgroup per survivor, and the mesh stage reads its meshlet index from that
  payload.
- The task stage cannot read `gl_ViewIndex`, so it culls with the push view
  index of the eye stream. Under multiview the mesh stage keeps its per-view
  discard.
- **Invariant:** task-stage output must rasterize only into views that the
  task stage tested. Today only the push view rasterizes: the mesh stage
  discards every other hardware view. This holds whether the driver runs the
  task stage once per draw or once per view. If a stereo mode lets one draw
  rasterize into more than one view, the task stage must cull a meshlet only
  when every receiving view rejects it: the frustum test against each view,
  and the cone test against each eye position. The task stage must never test
  against one eye for output that two eyes use.
- The early raster adds its launched, frustum-culled and cone-culled meshlet
  counts to counter words 13-15 (`meshletEarlyVisible`,
  `meshletEarlyFrustumCulled`, `meshletEarlyConeCulled`). It also adds the
  meshlets that the previous-frame history culled (`meshletEarlyHistoryCulled`,
  word 38) and the triangles of the launched meshlets
  (`meshletEarlyTriangles`, word 39). Words 16-37 hold the lookup segments,
  so the per-view record is 40 words (160 bytes). The delayed Advanced
  counter receipt reports these counts under the instrumented strategies
  (`GpuMeshletInstrumented`, `GpuIndirectInstrumented`). Late-raster task work
  is not counted, because the counter copy runs before the late raster.
- The delayed diagnostic readback ring keeps its last quarter of slots for the
  per-view counter receipts. The per-bin copies request thousands of slots per
  second; without the reserved slots they took every slot and the counter
  receipts stopped after startup. `vulkan.diagnostic_readback` in
  `get_render_profiler_stats` reports the slots by state and the dropped and
  completed reservations.
- `EmitAdvancedVisibilityRasterReadBarrier` includes the task stage, so the task
  stage sees the preparation writes in frames without indexed instance groups.

Static meshlets also use two-phase occlusion culling. Two task variants share
`VisibilityRaster.task`. They share the frustum and cone tests in
`VisibilityMeshletCulling.glslinc`.
- **Early variant** (no define). It draws a meshlet that passes the frustum and
  cone tests only when the meshlet was visible last frame. It reads the meshlet
  history and writes nothing to it. It does not declare the depth pyramid,
  because the pyramid has no read-only layout during the early raster.
- **Late variant** (`XR_ADV_VISIBILITY_LATE_TASK`, added to the task stage only).
  It adds a Hi-Z test of the meshlet bounding sphere against this frame's depth
  pyramid. The late compute pass leaves the pyramid in `GENERAL`, and the late
  raster samples it in that layout, so the late raster adds no layout
  transition. The late compute operation seals three tables for each view:
  role 0 builds the pyramid, role 1 runs the late compute test with the pyramid
  in `SHADER_READ_ONLY_OPTIMAL`, and role 2 is for the late raster with the
  pyramid in `GENERAL`. Preparation copies the sealed closure to the late raster
  operation of the same family. Each table holds the same storage bindings as
  the frame-slot table plus the view's pyramid layer. The task stage does not
  declare the storage pyramid binding, which only compute stages can see. The stable-bin stream
  prepares a separate `LateRasterPipeline` for each meshlet bin. A meshlet bin
  without that pipeline or table fails the late raster. There is no fallback to
  the early variant.
- **Recheck.** `LateVisibility.comp` sends every static meshlet draw of the
  early raster to the late raster again, with the recheck flag in the late mesh
  payload. For a recheck draw, the late variant computes "drawn early" from the
  same inputs as the early variant: the same frustum and cone result, the same
  previous-frame bit and the same history-valid flag of the view. It launches
  only the meshlets that pass now and were not drawn early. Deferred draws
  (culled early at draw level) have no recheck flag, so all of their passing
  meshlets draw.
- **Draw-level test of early meshlet draws.** Before the recheck, LateVisibility
  tests the AABB of each early static meshlet draw against this frame's pyramid.
  The early raster drew the draw, so its own depth cannot occlude it. An occluded
  draw gets `FLAG_OCCLUDED` and an empty late launch, and the next frame defers
  it. For this reason EarlyVisibility does not send early static meshlet draws to
  the deferred list for the periodic age retest that other producers use. That
  retest took large occluders out of the early raster in their retest frame. The
  pyramid then had holes, so the meshlets behind those occluders passed the late
  test, were drawn, and were drawn early again in the next frame without a
  visible result. Depending on how the retest frames of the large draws lined up
  at startup, the atrium late raster settled at about 2 ms or about 8 ms.
- **History.** Binding 71 of set 1 holds the meshlet history: an 8-word header
  with one parity bit for each view, then two bit sets for each view (previous
  and current), one bit for each global meshlet index. The capacity is 8 views
  and 2,097,152 meshlets. A meshlet above the capacity, and every skinned
  meshlet, has no history and is never culled by history.
  - `EarlyVisibility.comp` flips the parity of its view at the start of the
    frame. The previous set is then read-only for the whole frame.
  - `LateVisibility.comp` clears the current set of its view one time, before
    the late raster.
  - The late variant sets the bit of each meshlet that passes all of its tests,
    with `atomicOr` only. A clear and set-only writes make the result
    independent of the order of draws and invocations. A meshlet that two draws
    share is "visible" when at least one instance passes.
  - **Stereo:** the bits are indexed by the push view index of the eye stream,
    so one eye never reads or writes the bits of the other eye.
  - **Frame slots:** one history buffer exists for each output bank. All
    in-flight frame slots share it, so the GPU reads and writes it in queue
    submission order.
  - **Barriers:** the parity flip and the clear are compute writes. The raster
    read barrier (compute to task, shader read and write) makes them visible to
    the task stage. The in-frame storage barriers, whose source scope includes
    all graphics stages, order the late task writes of one frame before the
    parity flip, the clear and the early reads of the next frame.
- **Conservative cases.** `RenderFrameViewHistoryLedger` clears the
  history-valid flag of a view for a first observation, a camera change, a
  camera cut (a teleport is a pose discontinuity), an output change and a frame
  gap (the view skipped a frame). The early variant then draws every meshlet
  that passes the frustum and cone tests, and ignores the stale bits. Stale or
  undefined bits also never remove a meshlet from the image: a clear bit only
  moves the meshlet to the late raster, which tests it against this frame's real
  depth, and a set bit only draws the meshlet early. So the buffer is
  device-local, is not initialized, and keeps its bits for a new output owner.
- MSAA frames build no depth pyramid. Their late payloads carry the no-Hi-Z flag,
  so the late variant keeps only the frustum and cone tests.
- The counters do not change: counter words 13-15 count only the early raster.

Initial native visibility raster clears both depth and stencil. Late raster
loads both attachments to preserve values written in the current frame. MSAA
resolve clears the canonical stencil target because the depth resolve does not
write stencil. Gizmo bit `0x80` must not survive into the next frame: postprocess
uses it to bypass tone mapping for current debug shapes.

`AppendAdvancedDebugShapeCommands` draws queued shapes after late scene work
and physics debug work. It binds the forward color and depth target at internal
resolution. Depth-tested shapes run first, followed by overlay shapes. Neither
pass writes depth. The commands then restore depth testing and depth writes.

This step supplies the draw path for `ModelComponent.RenderBounds`. Bounds
collection alone does not draw a box. `VPRC_RenderDebugShapes` declares each pass
to the render graph. The `DebugOverlays` capture policy excludes this work from
shadow and light-probe outputs.

### Shading debug view for every output

`ShadingDebugView` (`EAdvancedShadingDebugView`) is a property of each Advanced stage family: the
desktop pipeline, each VR eye pipeline, the single-pass stereo pipeline and the stage family inside
an RVC pipeline. To compare outputs in one run, set `XRE_ADVANCED_SHADING_DEBUG_VIEW` at launch to a
view name or number (for example `ShadowFallbackReason` or `9`, `AmbientOcclusion` or `10`). Every
Advanced stage family then returns that view and ignores its own value. Unset, each pipeline keeps
its own setting.

### Debug views for meshlets and occlusion

Two debug views help to check meshlet culling. Both toggles are in the camera
`Debug Visualization` post-process stage (`gpuBvhDebug`), and the MCP tools
`set_meshlet_debug_display` and `set_camera_post_process_parameter` can set them.
When they are off, no pass, buffer, program or allocation exists for them.

- **Meshlet colors** (`Meshlet Debug Display`, or `ShadingDebugView = MeshletId`).
  `VPRC_AdvancedRenderStage` requests the `MeshletId` shading debug view while the
  pipeline selects no other view. Native shading then writes a color from a hash
  of the global meshlet index in the visibility identity instead of the material
  result, so a meshlet keeps its color from frame to frame. Pixels of indexed
  producers (CPU-direct, GPU indirect, and indexed ranges of meshlet frames such
  as parallax ranges) have no meshlet and are neutral grey. The submission strategy does not change. It works
  for each view, so stereo eyes show it too.
- **Occlusion-culled bounds** (`Occlusion-Culled Bounds`). After native opaque
  shading, the optional kernel `OcclusionCulledBounds.comp` draws box edges into
  the shaded HDR color of each view, and marks those pixels reactive so temporal
  resolve does not blend them. It only reads culling results; the culling shaders
  do not change.
  - Orange: draws that passed the frustum test and failed the late Hi-Z draw
    test (`FLAG_OCCLUDED` in the persistent state). The box is the world AABB of
    the candidate that the test used.
  - Cyan: meshlets of static meshlet draws that reached the late raster, passed
    the frustum and cone tests, were not drawn early, and have no current-frame
    history bit. The late task stage sets that bit for each meshlet that passes
    all of its tests, so a missing bit means that the late Hi-Z test removed the
    meshlet. The box encloses the cooked bounding sphere that the test used.
  - Frustum-culled and cone-culled units are not drawn. With virtual geometry on,
    clusters outside the cluster LOD cut are not drawn either: the overlay compiles
    with the same cut defines as the meshlet task stage and skips the clusters
    that the cut rejects before the occlusion test.
  - The program is created on first use and is not part of family admission.
    While it compiles, shading records no overlay.
  - The Advanced CPU-direct path has no occlusion test, so the overlay has no
    data there. The editor debug panel states the source of the boxes, or their
    absence, for the current frame's effective strategy.

### Selected GPU chain debug

`AppendAdvancedPhysicsDebugCommands` runs after late scene work and before
temporal and post-processing work. It is independent of the transparent mesh
count. The hook binds `ForwardPassFBO` and loads its existing HDR color and
native visibility depth without clearing either attachment. It uses the
internal viewport, depth testing, depth writes, and `LessEqual`.

`VPRC_RenderDebugPhysics` declares the dynamic `Advanced.PhysicsWorldDebug`
pass. The Advanced hook sets `RenderWorldPhysics` to `false`, so selecting a
GPU chain does not also draw the world's general physics debug frame. Other
callers keep the option's default value of `true`.

No selected GPU chain means no framebuffer setup, debug generation, or draw.
The hook also respects `DebugInstanceRenderingAvailable` and the capture
policy's `DebugOverlays` permission. Shadow and light-probe passes skip it.
Minimal depth and visibility output profiles omit the late stage entirely.

The dispatcher keeps a ring of four bounded batches for each world and view. A
view is the pipeline instance that records the draw, so the desktop view and
the XR eye views draw the same world in the same frame. A view draws the slot
that it generated this frame, or it regenerates the first slot whose native
buffers are free. A frame skips the debug output of a view only when all four
slots still have GPU work in flight. One lock serializes batch generation,
because it records compute work through the shared backend. Batches of a view
that has not drawn for 600 frames are retired. The hook adds no CPU readback.

`VPRC_RenderDebugPhysics` takes the world from the window viewport. An XR eye
view has no window viewport, so the command uses the world that is rendering
now, as `VPRC_RenderDebugShapes` does.

The chain's `DebugDrawChains` value is the selection. A change made while the
chain was not registered with the GPU dispatcher, for example while a `UseGPU`
change was pending, is applied on the chain's next GPU dispatch. That check
compares two flags and takes a lock only when they differ. Buffer ownership, barriers, and indirect draw rules are defined in
the [physics-chain compute contract](../physics/physics-chain-compute-backends.md#global-gpu-chain-debug).

### Transparent motion-vector matrices

Motion blur can require transparent velocity when temporal AA is disabled.
`AdvancedColoredAlphaTemporalVariantFactory` uses the active temporal authoring
view when it is available. Otherwise, it uses the current output's unjittered
frame matrices. A retained `FrameViewSet` must contain one matching camera and
eye. A missing or duplicate match disables history for that eye and sets its
previous matrix to its current matrix. It must not reuse camera-global history
from another output. Vulkan draw snapshots retain these matrices until replay.

### Canonical geometry pre-pack

The publication copies each new mesh into the canonical geometry arenas as
packed `AdvancedDeformedVertex` records, indices and meshlet words. Packing
used to run on the swap thread, so a large scene activation blocked one swap
for more than a second. `AdvancedCanonicalGeometryPrepackCache` moves that work
to the general job workers:

- When a pipeline has requested the canonical resident scene once, GPUScene
  queues a pre-pack for each mesh it registers. Registration runs in the
  collect phase, so most entries are ready when the publication runs.
- A worker takes the mesh's geometry read scope with a non-blocking enter and
  tries again on a later job tick when the mesh is busy. It validates the
  triangles, packs the vertices, indices and meshlets, and keeps the result
  only when the geometry revision, buffers, triangle list and meshlet payload
  did not change during the pack. Workers never take the GPUScene lock and
  never touch the publication transaction.
- The publication copies a ready entry for the exact planned revision into its
  staging arrays and skips the per-triangle validation of that mesh. A mesh
  without a ready entry is validated and packed on the swap thread, as before.
  The result is the same bytes either way.
- A committed publication releases the entries it copied. GPUScene releases an
  entry when its mesh leaves the atlas. Entries own plain arrays, so their
  memory returns to the runtime.

`XRE_ADVANCED_GEOMETRY_PREPACK=0` disables the pre-pack. The publication then
packs every mesh on the swap thread.

### Synchronization boundaries

OpenGL and Vulkan lower the same four logical boundaries (`EAdvancedSynchronizationBoundary`) through their own encodings:

1. `ComputePreparationToVisibilityRaster`
2. `VisibilityRasterToComputeShading`
3. `ComputeShadingToLateGraphics`
4. `LateGraphicsToPresentation`

Inside the Vulkan native shading stage, a barrier separates two dispatches only when the second
reads data from the first, or when both write the same pixels:

- `BuildFroxels` and `Background` have no barrier between them. Background reads only the
  visibility identity. One global barrier after Background makes the froxel, light index and decal
  buffers and the background image writes visible to shading.
- The main shading kernels, the overflow repair dispatch and the Uber kernels have no barriers
  between them. Their pixel sets are disjoint: the main kernels return when the classification
  overflow flag is set, and the repair kernel returns when it is clear. Without overflow, each
  pixel belongs to one kernel, and the Uber kernels shade only Uber kernel tiles.
- The barrier before the Uber repair dispatch stays. In an overflow frame, the standard repair
  shades every pixel and the Uber repair then overwrites the Uber pixels.
- Classification runs once per view and every view uses the same tile lists, counters and
  indirect arguments. The first view clears the counters. Each later view starts with a global
  compute-to-compute barrier, because it appends to the earlier views' lists and writes the
  indirect arguments again, and the barrier after the earlier `BuildArguments` covers only
  indirect reads of those arguments.

Synchronization validation reports the disjoint-pixel dispatches above as hazards; see the
known false positives in [Vulkan Diagnostics](../../developer-guides/rendering/vulkan-diagnostics.md#synchronization-validation-coverage).

Because these dispatches overlap, the GPU profiler scope `Advanced > Shading > GpuOverflowRepair`
starts while the main kernels still run. Its time includes the main kernels. Use the
`Advanced > Shading` scope for the stage total.

### Resource ownership

Each declared resource has one ownership class (`EAdvancedRenderResourceOwnership`): `PipelinePersistent`, `FrameSlotTransient`, `TemporalHistory`, `Imported`, or `External`. Current and previous frame-slot reuse waits on a fence or timeline completion.

The classification kernel-tile membership buffer (`AdvancedClassification.KernelTiles`, one per
frame slot) holds one range of tile records per dense shading kernel index. It has
`AdvancedRenderPipeline.KernelTileRangeCount` ranges (48), not one per possible kernel (128): the
material publisher creates at most 42 distinct kernels, and the kernel table reuses the lowest free
index. A kernel index past the last range sets the membership overflow flag, and that frame is
shaded by the overflow repair pass. For a 2688x2688 eye pair this cuts each slot from about 101 MB
to 38 MB of device-local memory.

Debug-only resources are declared only when their feature bit is in the resource
feature mask. `AdvancedTransparency.DebugOutput` (RGBA16F, about 110 MB per eye output
at 2688x2688) needs a late-pass debug view or debug overlays. Each optional resource
uses its own bit. Bits 48-50 hold `AdvancedVisibilityResourceFeature`, and `Core` is
always set, so a debug resource gated on one of those bits is always allocated.

## Invariants

- Compatible opaque and masked surfaces write one visibility payload and shade through the visibility path. They do not choose between deferred and ordinary opaque-forward color paths.
- The production Advanced path does not allocate or populate a classic full GBuffer. Diagnostic reconstruction targets exist only behind an explicit capture or debug mode.
- Late and special passes are explicit. They do not make the opaque renderer a deferred and forward composite.
- Geometry identity is backend-neutral across CPU-direct, zero-readback indirect, meshlet, skinned, and future virtual-geometry producers.
- Material work groups by shading-kernel compatibility and visible coverage, not by material object or descriptor-set identity.
- Scene, geometry, material, light, shadow, animation, and texture data are GPU-addressable through stable table records. Warmed production frames have no per-object or per-material binding loops.
- Production GPU-driven modes do no same-frame count, visibility, material-range, or overflow readback.
- Frame execution never creates or resizes declared pipeline resources.
- GPU-written frame data does not invalidate reusable command topology. Recorded command packets change only for topology, capacity, binding, shader, or resource generations.
- A required accelerated mode that is not available fails visibly with a machine-readable reason. It does not route through CPU emulation.
- Per-frame hot paths allocate no managed heap memory in steady state.
- OpenGL 4.6 and Vulkan use the same logical contracts. Backend encodings can differ.
- An unsupported opaque material in a required Advanced mode renders an observable error material or fails selection. It does not enter the classic opaque renderer.
- A canonical texture whose image is still loading does not fail the frame. Its row keeps the non-resident encoded reference, so each material binding samples its own fallback (for example black, white, or a flat normal) until the image is ready. Every frame builds the scene publication again, so the real image (the streaming preview first, then the full image) replaces the fallback as soon as it is ready. A texture view with the wrong shape still fails the publication.
- An imported texture starts with a small loading placeholder: the red and blue filler checker for color slots and a flat normal for normal slots. The model importer marks this placeholder with `XRTexture2D.MarkAwaitingFirstResidency()`. While a texture is in this state, the Vulkan publication treats it as still loading, so the binding fallback shows instead of the checker. The first imported resident publication clears the state. This includes the filler data of a missing source file, so a missing texture still shows the checker. The state is runtime-only: it is not serialized and is not part of the asset or a cache key.
- Readiness is backend state that the canonical table generations do not cover. The Vulkan runtime keeps a texture readiness generation that advances when the encoded reference of any texture row changes. The resident texture table stores the generation it wrote. A different generation blocks the unchanged reuse and the journal patch, and forces one full rewrite of the texture table in that frame slot.
- Imported transparent materials draw in the late lane. `XRMaterial.ResolveAdvancedLatePassMetadata()` returns the explicit `AdvancedLatePassMetadata` when a material authors it. Otherwise it derives shared, immutable metadata from the transparency mode: `AlphaBlend` in the `TransparentForward` pass becomes sorted alpha, and `WeightedBlendedOit` in the weighted blended OIT pass becomes OIT. Derived metadata never requests the scene color snapshot, and it is not serialized, so no asset changes. A material that samples scene color must author its metadata.
- The model importers resolve a material opacity: `Opacity` when present, otherwise one minus `TransparencyFactor` times `TransparentColor` (white when absent). A transparent color with a zero factor is opaque. An untextured material below the opaque threshold becomes lit forward glass (`LitColoredForward.fs`, opacity in `MatColor.a`). A textured blended material keeps `LitTexturedForward.fs` and gets `MaterialTransparency = 1 - opacity`; an unset uniform reads 0, so other users of that shader stay opaque. Imported blended materials use sorted alpha; weighted blended OIT needs the explicit Blended import option. Imported transparent surfaces cast no shadows and have no refraction.
- A viewport whose scene is not due skips its swap (for example the desktop view with the VR mirror off, or a desktop held for XR frames). Its last rendering package and its picking leases would then pin one old scene publication. The table journals cannot reclaim past that pin, so the database rejects every later publication and each collect swap stalls. On each skipped swap, `XRViewport` therefore drops the rendering package pin (`RenderCommandCollection.ReleaseLaggingRenderingCanonicalPin`) and the picking leases (`XRRenderPipelineInstance.ReleaseLaggingAdvancedPickingRetention`) when they lag more than 16 publications behind the newest one. The next swap takes a new pin before the viewport renders its scene again.

## Capability Floor

`AdvancedRenderPipelineCapabilityResolver` rejects an output with an `EAdvancedRenderPipelineRejectionReason` when a required capability is missing:
| Requirement | Rejection reason |
|---|---|
| A live renderer on a supported backend | `RendererUnavailable`, `UnsupportedBackend` |
| Integer visibility render targets | `MissingIntegerRenderTargets` |
| Compute shaders | `MissingComputeShaders` |
| Storage buffers for geometry and scene tables | `MissingStorageBuffers` |
| Indirect dispatch and draw | `MissingIndirectSubmission` |
| A production texture-indirection mode | `MissingTextureIndirection` |
| Explicit image and buffer synchronization | `MissingSynchronization` |
| Current and previous frame-slot storage | `MissingFrameSlotStorage` |
| Stereo-array resources, when stereo is requested | `MissingStereoArrayResources` |
| OpenGL multiview raster, for OpenGL stereo | `MissingOpenGlMultiviewRaster` |
| The `VisibilityBuffer` shader family | `MissingShaderFamily` |

Optional acceleration flags (`SupportsBufferDeviceAddress`, `SupportsDescriptorIndexing`, `SupportsDescriptorHeap`, `SupportsSubgroupOperations`, `SupportsMeshShaders`, `SupportsAsyncCompute`, `SupportsTimelineSemaphores`) can improve an implementation. They never change the logical visibility, scene, or material contracts.

## Output Ownership

Every pipeline request carries an explicit output purpose. Outputs can share scene, mesh, and material data and compatible GI, temporal, froxel, and post-process feature contracts. They never share output-local pipeline instances, command recordings, frame-slot ownership, or temporal histories.
| Output | Owner |
|---|---|
| Desktop scene | The standard selection policy. `AdvancedRenderPipeline` is the configured source for new desktop cameras under the `Available` or `Required` mode (`XRE_ADVANCED_RENDER_PIPELINE_MODE`, `AdvancedRenderPipelineMode`). The camera pipeline asset is authoritative; capability results and visibility-family reservations live on each `XRRenderPipelineInstance`. |
| OpenXR eyes | An independently owned pipeline of the family that `VrRenderPipeline` selects: `DefaultRenderPipeline`, `AdvancedRenderPipeline` with the OpenXR eye profile, or `RvcRenderPipeline`. The desktop Advanced policy does not affect eyes. An unsupported view mode and family pair submits no projection layer. |
| Offscreen capture | The standard capability policy. Desktop debug and RVC settings do not redirect it. |

Under `Available`, an output that cannot reserve the required family stays explicitly unbound and reports the rejection. Under `Required`, selection throws `AdvancedRenderPipelineNotSupportedException`.

## Native Uber Tile Coverage

Native Uber vertex-mode UDIM coverage reads the three original corners through
the shared `PrimitiveAccess.glslinc` geometry contract. The visibility and native
directional-shadow fragments use the same rejection predicate. No geometry
shader or non-finite clip position is required. Pixel-mode discard continues to
use interpolated UVs.

`AdvancedPackedVertexCodec` classifies UV0 through UV3 before half precision
packing. `AdvancedDeformedVertex.Reserved2` stores four five-bit tile IDs and a
validity bit at bit 31. IDs 0–15 identify the four-by-four tile grid; 16 means
outside the grid, and 17 means a non-finite source UV. The record remains 64
bytes. Its layout identity changes when this coverage data becomes required.
Deformation preserves the field. Native material admission permits
UV0 through UV3. Visibility and directional shadows use the same tile selector.
Missing or invalid classification rejects the triangle and increments
the existing decode diagnostic counter.

## Source Flat Lighting Direction

Source flat-mask lighting needs a source object direction. Skinned geometry uses
world-space vertices and an identity geometry matrix, so that matrix cannot
supply the direction. `RenderableMesh` captures a separate skin-root or component
basis at command swap. `SourceMultilayerFlatDirection` transforms the imported
axis and normalizes it without overflow from large scale values.

Native draws store the octahedral direction and its validity in words 18 and 19
of `AdvancedDrawRecord`. The record remains 80 bytes. Publication hashes, reuse
checks, and journal capacity checks include these values. A direction change
updates draw content. Root transform changes dirty active flat-mask commands
before bounds-cache early returns.

Authored draws carry the same direction in `u_SourceFlatDirection`. Vulkan object
uniform generations and cached draw templates retain it. An active flat mask
with an invalid direction fails explicitly. Materials with source lighting or
the stylized feature disabled do not require this direction.

## Known Limits

Authored late replay and authored Vulkan shadow passes do not have this triangle
binding contract. They reject active vertex-mode UDIM discard. Point and spot
shadow atlases use the authored path. A separate local-shadow content-cache
limit remains: cached local tiles do not include all material input revisions
in their content hash. These paths require further work before full tile-discard
shadow parity is available.

- The Advanced pipeline still builds the classic GBuffer and `DeferredLightCombine` resources for its opaque path. Their removal is open work.
- `AdvancedRenderPipeline` is the source default for new desktop cameras, but it is not yet certified production-ready for every backend, material, and output profile.

## Related Documentation

- [Default Render Pipeline Notes](default-render-pipeline-notes.md)
- [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md)
- [Mesh Submission Strategies](mesh-submission-strategies.md)
- [GPU Record Layouts](gpu-record-layouts.md)
