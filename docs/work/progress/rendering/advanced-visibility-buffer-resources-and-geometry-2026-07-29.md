# Advanced Visibility Buffer Resources And Geometry Progress

Date: 2026-07-29
Related TODO:
[04 - Visibility Buffer Resources And Geometry](../../todo/rendering/architectural-refactor/04-visibility-buffer-resources-and-geometry-todo.md)

## Outcome

The phase-04-owned visibility ABI, immutable resource layout, five geometry
producer contracts, raster shaders, early/HZB/late sequence, synchronization
contract, diagnostics, and focused tests are implemented.

The selected version-1 surface payload is:

- `RG32_UINT` identity: stable draw-table ID and producer-specific primitive
  identity;
- `R32_UINT` metadata: producer, early/late origin, masked/front-face flags,
  velocity validity, view, payload version, and selection validity;
- optional `R32_UINT` editor selection sidecar;
- `D32F_S8` authoritative depth/stencil;
- reconstructed perspective-correct barycentrics, so no production
  barycentric attachment is allocated.

Invalid identity, metadata, primitive, and selection words use `0xFFFFFFFF`.
Overflow and undefined producer/origin values are rejected rather than
truncated. Normal depth clears to `1.0`; reversed depth clears to `0.0`.

## Resource And Sequence Contract

The pipeline now declares named mono/stereo visibility targets, current and
previous depth-pyramid mip chains, persistent candidates/payloads/producer
tables, persistent per-view visibility state, source arguments and range maps,
and frame-slot replicated early/late indexed arguments, mesh-task arguments,
mesh-command payload maps, visible/deferred lists, range counts, and 64-byte
counter rows.

Debug output and GPU validation are immutable resource-profile features. The
render graph describes attachment, sampled, storage, and indirect usages, while
`AdvancedVisibilitySynchronizationContract` defines paired Vulkan stage/access
states and OpenGL barrier masks for:

1. preparation to early raster;
2. early raster to current depth-pyramid build;
3. current depth pyramid to late preparation;
4. late preparation to late raster;
5. late raster to visibility consumers.

`AdvancedVisibilitySequenceContract` freezes reset, clear, early preparation,
early argument build, early raster, one current-HZB build, late preparation,
late argument build, late raster, validation, and publication order. Late
raster loads the early attachments and depth rather than clearing them.

## Geometry And Shader Contract

Producer selection remains in the mesh-submission strategy resolver and covers:

- CPU-direct static indexed;
- CPU-direct pre-skinned;
- zero-readback indirect indexed;
- static meshlet;
- skinned meshlet.

Producer parity exercises the actual raw encodings: indexed producers emit a
canonical triangle index, while meshlet producers emit a 24-bit meshlet plus
8-bit local primitive and resolve it back to the same canonical triangle.

The indexed vertex shader and mesh shader load canonical draw, instance,
geometry, transform, material, and editor records. The mesh shader consumes
real meshlet descriptors, vertex remaps, packed triangle bytes, current and
previous position streams, and UVs. The ordinary opaque fragment shader writes
identity only. The masked fragment shader is the sole coverage-sampling path
and uses the canonical material texture binding, cutoff constants, UV
scale/bias, stable texture-handle lookup, backend-encoded reference, and common
texture sampling helper.

Depth-changing displacement modes are explicitly rejected until a dedicated
variant exists for every compatible producer. This prevents a displaced
material from silently writing non-displaced depth.

## Diagnostics

The immutable debug profile supports raw words, draw, primitive, material,
shading kernel, selection, early/late origin, invalid-payload, and depth views.
Frame-slot counter rows cover early/deferred/late/recovered work, valid and
invalid pixels, payload overflow, masked coverage, decode bounds failures, HZB
builds, and unsupported displacement. The diagnostic shaders resolve stable
handles through canonical lookup segments and validate payload versions.

## Validation

- `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj`
  with isolated artifacts: passed with 0 errors. Existing Magick.NET package
  audit warnings remain.
- Isolated `AdvancedVisibilityBufferContractTests`: 11 passed, 0 failed. The
  suite covers ABI sizes and sentinels, overflow rejection, metadata and
  primitive packing, all five producer selections, producer-neutral identity
  parity, table-only decode, exact sequence order, variant policy, motion
  invalidation, immutable resource layout, and shader-source restrictions.
- `glslangValidator` front-end validation: passed for all nine visibility
  stages and all four modified preparation/HZB compute stages. The OpenGL
  stages used the engine's OpenGL SPIR-V dialect; mesh validation used relaxed
  Vulkan default-uniform handling to mirror the engine's later uniform-block
  rewrite.

The repository-wide unit-test project could not reach test execution because
concurrent unrelated Vulkan work currently references the missing
`VulkanResourceBindingView` type from `VkFrameBuffer.cs` and
`VulkanResourceBindingKey.cs` (`CS0103` and `CS0246`). The focused tests were
therefore compiled and run through a disposable friend test project that
references only `XREngine.Runtime.Rendering`.

## Live Integration Boundary

The advanced backend remains capability-gated. `VPRC_AdvancedRenderStage`
publishes the phase-04 render-graph usages and acquires shared preparation, but
the phase-02 canonical scene/material database is not yet published and bound
as live GPU tables, and phase-05+ consumers are not installed. Executing the
new raster shaders before those prerequisites exist would access missing
tables, so no silent fallback or premature backend enablement was added.

Consequently, live early and late draw execution, image parity, two-position
scene captures, and OpenGL/Vulkan attachment/barrier captures remain open in
the phase-04 TODO. They must be completed when the advanced pipeline can be
activated end to end; contract-only captures from the legacy renderer would
not validate these resources.
