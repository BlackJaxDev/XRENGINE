# Advanced Attribute Reconstruction Progress

Date: 2026-07-29
Related TODO:
[05 - Attribute Reconstruction](../../todo/rendering/architectural-refactor/05-attribute-reconstruction-todo.md)

## Outcome

The phase-05-owned attribute-reconstruction ABI, CPU reference contracts,
shader implementation, immutable diagnostic resources, synchronization
contract, and focused tests are implemented.

Reconstruction is an on-demand shader operation shared by native material
kernels. It does not allocate or populate a production classic GBuffer.
Optional fullscreen outputs exist only for diagnostics and the explicitly
non-production reference shader.

## Surface And Geometry Contract

`AdvancedSurface` version 1 contains:

- current and previous world positions;
- depth and reconstructed world position;
- geometric and shading normals;
- MikkTSpace tangent, bitangent, and handedness;
- two UV sets, two vertex-color sets, and one canonical flat custom attribute;
- analytical UV gradients and selected conservative mip;
- material row, view, primitive, and surface identity;
- motion, validity, reactive, derivative, and fallback flags.

The shared reconstruction include resolves stable draw, instance, geometry,
material, shading-kernel, transform, optional deformation, and view records.
It decodes indexed classic triangles and meshlet-local triangles, then reads
either static packed vertices or shared pre-skinned current/previous packed
vertices. Every lookup and range is checked before use. Invalid, stale,
missing, and nonresident inputs return a defined invalid surface and increment
slot-local diagnostic counters rather than reading arbitrary storage.

Required-attribute masks avoid decoding and interpolating data a material
kernel does not consume. The mask now covers position, normals, tangent frame,
UV sets, color, canonical custom data, flat attributes, deformed position, and
analytical derivatives.

## Interpolation, Frames, And Derivatives

The production path reconstructs perspective-correct barycentric weights and
their pixel-space derivatives analytically from projected triangle vertices.
It preserves provoking-vertex flat data and rejects non-finite, degenerate, or
out-of-range triangles. World position normally comes from depth plus the
inverse jittered view-projection matrix; vertex-derived deformed position is
computed only when requested.

Normals use the inverse-transpose world transform. Tangents are
Gram-Schmidt-orthogonalized, and transform determinant sign is combined with
the authored MikkTSpace sign so non-uniform, negative, and mirrored transforms
produce a consistent frame on OpenGL and Vulkan.

Compute material sampling uses explicit `textureGrad`. When derivatives are
undefined, the defined fallback is the coarsest available mip through
`textureLod`. Neighbor finite differences are diagnostic-only and may compare
only pixels with identical visibility identity. Optional `R16F`
derivative-error and selected-mip views expose the result.

## Temporal Contract

Velocity is current-minus-previous unjittered NDC for the active eye. Vendor
bridges convert it to normalized UV by multiplying by `0.5`. Current and
previous instance transforms and static or deformed vertex positions use the
same decoded primitive.

The draw flags reserve bits 16 through 19 for the phase-04 velocity validity
reason. Any nonzero reason invalidates velocity and marks the pixel reactive;
masked edges remain reactive independently. The contract covers newly visible,
teleported, topology-changed, vertex-count-changed, history-reset,
arena-overflow, and frame-gap surfaces without conflating the two eyes.

## Resources And Synchronization

Phase 05 owns immutable feature bits 51 through 55. The core profile allocates
one 64-byte reconstruction-counter row per view plus a summary row for every
frame slot. Optional profile features add:

- an `RGBA16F` attribute debug output;
- `R16F` derivative-error and selected-mip outputs;
- GPU validation;
- an `RGBA16F` non-production reference output.

The render graph declares visibility identity, metadata, selection, depth,
geometry payloads, producer tables, scene tables, and counter dependencies.
`AdvancedReconstructionSynchronizationContract` freezes the final-visibility
to reconstruction boundary and the reconstruction-to-delayed-readback
boundary for Vulkan stage/access states and OpenGL barrier masks.

## Validation

- Isolated `XREngine.UnitTests` build: passed with 0 warnings and 0 errors.
- Reconstruction plus adjacent visibility/resource/shader contract tests:
  62 passed, 0 failed. Coverage includes ABI and resource layout, stable-table
  decode, invalid/stale/missing/nonresident and resident-fallback inputs,
  perspective interpolation within `2.0e-5`, degenerate rejection, selective
  and flat attributes, mirrored and non-uniform tangent frames, derivative LOD
  cases, temporal history breaks, per-eye motion, packed validity flags, and
  overflow rejection.
- Refreshed `glslangValidator` validation: all four compute shaders passed the
  OpenGL frontend and Vulkan 1.2 SPIR-V compilation. The Vulkan validation uses
  relaxed default-uniform handling because the engine rewrites those uniforms
  into backend-compatible blocks later in compilation. `spirv-val` passed all
  four modules. SPIR-V disassembly confirms pass-local visibility/debug
  resources use descriptor set 1 at bindings 0 through 5, while shared
  visibility and reconstruction tables in that set use bindings 28 through 41.

## Live Integration Boundary

The advanced backend remains capability-gated. Phase 04 does not yet execute
its visibility producers against live published scene/material GPU tables, and
phase 06 has not installed material classification and native material
kernels. Activating reconstruction before those prerequisites would bind
missing tables, so this phase does not add a silent CPU fallback or prematurely
enable the backend.

Consequently, live static/skinned/blendshape image parity, dense-velocity
captures, measured reconstruction GPU cost, and OpenGL/Vulkan RenderDoc
inspection remain open in the phase-05 TODO. They must be completed after the
advanced path is executable end to end; captures from the legacy renderer
would not validate this implementation.
