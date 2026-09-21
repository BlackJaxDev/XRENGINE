# Sponza full-magenta output in Advanced rendering

Date: 2026-09-21
Status: fixed and live-validated on Vulkan Advanced

## Symptom

The camera began very close to a large Sponza brick surface, making the viewport
appear entirely magenta. The magenta was the Advanced reconstruction fail-closed
color, not a missing-shader fallback.

## Findings

Two independent reconstruction contract failures were found:

1. `AdvancedMaterialDatabase` published only the active prefix of its constant and
   texture-binding arrays. Material records use fixed-stride slots addressed by
   stable handle index, so a sparse live handle could legally reference beyond
   that shortened GPU range. The database now publishes the full fixed-capacity
   arenas.
2. The imported `bricks` material requested UV1 for an emissive texture while the
   OBJ mesh exposed only UV0. Reconstruction rejected the surface with
   `XR_ADV_RECONSTRUCTION_TEXCOORD1_MISSING`. UV1 now falls back deterministically
   to UV0, including derivatives, when a material requests a secondary set that
   the imported mesh does not contain.

Material inspection showed valid neutral constants. All three brick textures were
resident and published, including `spnza_bricks_a_diff.png`. Diagnostic shader
artifacts executed successfully, proving the Advanced shader family was loading.

## Validation

- Clean editor build: 0 warnings, 0 errors.
- Fresh named Vulkan session: Advanced execution admitted; 199/199 resources
  materialized; no pending shader pipelines and no render-thread shader compile.
- Viewed post-fix captures from the initial close camera and focused Sponza views.
  Magenta is absent and brick/roof texture detail is visible.
- A later Advanced DDGI capture also shows the repaired brick wall under lighting:
  `Build/_AgentValidation/20260921-120211-sponza-advanced-magenta/mcp-captures/Screenshot_20260921_132829_743_826ae7e9834b40b59a13d145ed1d407e.png`.
- RenderDoc tooling passed `rdc doctor`, but capture target control timed out before
  producing an `.rdc`; shader diagnostics and direct resource captures supplied
  the decisive evidence instead.

Startup frame-package generation warnings occurred while resources materialized.
No shader compilation failure, Vulkan VUID, or steady-state fatal/error explained
the original magenta output.
