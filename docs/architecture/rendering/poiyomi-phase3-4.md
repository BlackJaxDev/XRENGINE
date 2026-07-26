# Poiyomi Phase 3/4 Architecture

This document records the render-state, pass-set, module, and binding contracts
implemented for Poiyomi Toon 9.3.64 imports.

## Pass Set

One `XRMaterial` owns parameters, textures, and authored uber feature state.
`MaterialPassSet` adds immutable pass definitions for EarlyZ, depth-normal,
shadow, velocity, transform ID, picking, reflection, base color, and inverse-hull
outline work. Definitions carry pass-specific shaders and fixed-function state
without duplicating the authored material.

Pass order is deterministic:

| Order | Identity |
| ---: | --- |
| 100 | Early depth |
| 200 | Depth-normal |
| 300 | Shadow |
| 400 | Velocity |
| 410 | Transform ID |
| 420 | Picking |
| 430 | Reflection |
| 500 | Base color |
| 600 | Outline |

Every definition carries the same position/opacity state hash and the complete
alpha, dissolve, UV-discard, vertex-deformation, and culling coverage contract.
Renderable meshes submit an enabled outline definition as a second,
CPU-orchestrated mesh command sharing the source renderer and authored material
state. The canonical vertex and fragment variants provide inverse-hull
extrusion after deformation and reuse the base alpha/dissolve coverage path.
The pass identity, render pass, position/opacity hash, and pass macros all
participate in prewarm keys. Enabled definitions are copied to caller-owned
storage without steady-state allocations.

Poiyomi's `ForwardAdd` pass is folded into the Forward+ base pass. Its authored
additive state is retained for diagnostics. Disabling the source Add pass
suppresses any separate compatibility pass while ordinary Forward+ base-pass
lighting remains active.

## Presets And Fixed State

The importer maps all nine serialized `_Mode` values independently. Render queue
and queue priority are preserved separately from transparency classification.
RGB and alpha blend factors and operations, depth write/test, culling, color
mask, polygon offset, alpha-to-coverage, fog opt-out, and common/front/back
stencil state are converted from their serialized Unity enum values. Outline
blend, depth, stencil, and cull state is independent from the base pass.

## Uber Helper Audit

`UberHelperModuleAudit` is the authoritative, testable inventory.

- Active canonical includes: `common.glsl`, `uniforms.glsl`, `parallax.glsl`,
  `dissolve.glsl`, `glitter.glsl`, and `flipbook.glsl`.
- Active companion path: `XRENGINE_OUTLINE_PASS` variants of the canonical
  mono, OpenVR/OpenXR, and fragment shaders.
- Reusable reference implementations whose live behavior remains inline:
  `backface.glsl`, `details.glsl`, `emission.glsl`, `matcap.glsl`, `pbr.glsl`,
  `specular.glsl`, and `subsurface.glsl`.
- Dormant for its later parity phase: `decals.glsl`.
- Reusable legacy references: `outline.vert` and `outline.frag`.
- Obsolete and superseded by the canonical pass branch: `outlines.glsl`.

A helper file is not evidence that a feature is supported. Support requires a
reachable canonical render-pass path. The variant builder prunes disabled
feature guards and sampler declarations and now computes transitive feature
dependency closure before generating the variant.

## Repeated Slots

`UberMaterialSlotSchemas` owns the reusable contracts for four decals, four
matcaps, four emissions, and two rims. Each schema defines its feature owner,
field suffixes, sampler roles, and maximum specialized slot count. Counts remain
compile-time axes; inactive families do not retain unrelated samplers.

## Resource Limits And Binding Ladder

The portable baseline is 16 fragment samplers, 16 sampled images, and 16 KiB of
uniform storage for both OpenGL 4.6 and Vulkan 1.0; Vulkan also records the
128-byte minimum push-constant budget.

`UberMaterialBindingPlanner` selects the first faithful option:

1. Direct samplers within the active backend limits.
2. Texture arrays only when dimensions, formats, and sampler behavior match.
3. The engine material texture table when its backend capability is active.
4. Bindless/descriptor-indexed resources when supported.
5. A precise unsupported result when none can represent the material.

The planner never silently drops a texture. `UberSamplerFallbacks` supplies
role-aware color, normal, white/black mask, zero-data, neutral-height, and black
emission defaults.

## Validation

`PoiyomiPhase34ArchitectureTests` covers every preset, authored fixed state,
queue and pass enable state, deterministic ordering, companion coverage keys,
outline state, allocation-free enumeration, transitive dependencies, slot
schemas, binding rungs and failures, and semantic fallbacks.
