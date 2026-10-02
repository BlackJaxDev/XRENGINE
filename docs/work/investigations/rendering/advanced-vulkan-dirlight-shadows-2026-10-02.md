# Advanced pipeline (Vulkan): directional light casts no shadows on Sponza

Status: **Re-enable recovery passed bounded Vulkan runtime validation; automated
suites still have failures**, October 2, 2026. The isolated editor build passed
with zero warnings and errors. Five long off/on cycles and three camera views
were checked with `XRE_AOT_PARITY=off`; the normal unit-world parity mode remains
blocked by a separate shader-loading exception (see below).

## Problem

Vulkan + `AdvancedRenderPipeline` + Unit Testing World Sponza with `DirLight` and
`DirLightCastsShadows` enabled rendered no directional shadows. Every surface
facing the light was fully lit.

## Root cause

The Advanced native-opaque shader and the directional cascade publisher disagreed
on the units of the cascade "max bias".

- `DirectionalLightComponent.ResolveCascadeBiasSettings` publishes
  `BiasMin = texelWorldSize * ShadowDepthBiasTexels / cascadeDepthRange` (normalized
  depth) and `BiasMax = ShadowSlopeBiasTexels` (a **texel count**, default `2.0`)
  through `RenderedSplitBlendBias.zw`. Raster receivers (`DeferredLightingDir.fs`,
  `ForwardLighting.glsl`) resolve `.w` as a slope scale with screen-space
  derivatives, which is consistent.
- `AdvancedGlobalResourceCapture` copied `.z/.w` straight into
  `AdvancedShadowRecord.DepthBiasAndFilter.xy`. `StandardShadow.glslinc` used them as
  normalized depth: `bias = mix(x, y, 1 - saturate(N.L))`.

With `y = 2.0`, every receiver not facing the light head-on was pushed in front of
every occluder, so the depth compare always passed. This mapping has existed since
the Advanced shadow sampler landed (`f1318da77`, 2026-09-04). It is not a regression
from the latest commits or the uncommitted `VulkanFrameLoop` changes.

## Fixes

1. **Unit conversion (CPU).** `AdvancedGlobalResourceCapture.CreateDirectionalDepthBiasAndFilter`
   publishes the constant depth floor unchanged. It converts the slope scale into
   normalized depth per authored texel, derived from the rendered orthographic
   light matrix (clip units per world unit) and the atlas resolution scale. The
   per-capture `DirectionalShadowGpuRecord[8]` allocation became a stack span.
2. **Analytic slope (shader).** For directional records, `StandardShadow.glslinc`
   applies `x + y * footprint * tan(theta)`, the analytic equivalent of the raster
   receiver-plane bias. Point and spot records keep the min/max `1 - N.L` contract.
3. **Back-facing receivers.** The slope uses the reconstructed triangle plane
   (`XRAdvancedSurface.geometricNormal`) with `|cos(theta)|`. Clamping `N.L` at zero
   treated planes facing away from the light as grazing, which lifted receivers
   through thin occluders. This produced sawtooth light leaks on arch soffits
   whose shading normals face the light.
4. **Kernel footprint.** The footprint is `sqrt(2) * (radius + 0.5)` texels. This
   covers the diagonal taps of the 3x3 PCF kernel and half-texel receiver
   quantization; a one-texel footprint left acne on steep faces.
5. **Re-enable diagnostics.** Cascade atlas render paths now return a decline
   reason, including the pipeline instance's `LastRenderDeclineReason`. The "Grouped
   directional cascade render failed ... sequential fallback also failed" warning
   reports both the grouped and the sequential reason.

`AdvancedShadowRecord.DepthBiasAndFilter` documents the per-type contract.
`docs/architecture/rendering/default-render-pipeline-notes.md` records the rule.

## Evidence

- Before: `AdvancedShading.ShadingDiagnostics` read reason OK, visibility 255 for
  every pixel. The `ShadowFallbackReason` and `ShadowMask` debug views agreed.
- RenderDoc (rdc 1.41): the 4096² D24 atlas holds four valid cascade tiles. The
  light record has flags `3` and shadow handle `(5,1)`. Cascade records are
  Resident with correct UV bias. Thread debug of a floor pixel: receiver `0.95263`,
  atlas texel `0.95227`, computed bias `2.0`, so the receiver became `-1.047`.
- After the unit fix the published record shows slope `0.000408` = 2 x the
  `0.000204` cascade-0 texel depth.
- Registration check: real gallery-floor points (y = 4.155) match stored depth
  within +/-0.3 texel depth through cascade 0. Matrix and UV sampling are correct.
- Coverage check: Sponza2's `roof` group overhangs the courtyard at y ~ 13-14 and
  leaves only a narrow gap. At the default 55 degree sun most of the courtyard
  floor is genuinely shadowed. Advanced looks darker than the Default pipeline
  only because light probes are not captured (no ambient), not because of
  over-shadowing.
- Acne root: on the soffit, receivers were 15-30 texel depths behind the arch top
  (correctly occluded). The old clamp-to-grazing bias (~21 texel depths) lifted
  them partly through. Sun-facing arch faces at ~73 degrees from the light showed
  center-tap errors up to +/-7 texel depths from steep-surface undersampling;
  no constant UV offset reduced them.
- Final captures from the arch, hallway and courtyard views show directional shadows
  with no sawtooth acne on arches at default light settings.

## Ruled out

- Uncommitted `VulkanFrameLoop` mesh-operation changes (atlas content is correct).
- `ProgramUniformValue` compact storage (semantics unchanged).
- Atlas residency, bindless binding, compare samplers (none in use), and
  rasterizer depth bias (disabled on Vulkan).
- A spurious caster "lid" (it is Sponza's real roof overhang).
- UV registration (verified on flat geometry).
- RenderDoc thread debugging is unreliable for bindless texture fetches. It
  reported implausible normal-map texels while the hardware normals view was
  correct, so it was only used for buffer-sourced values.

## CastsShadows re-enable failure (intermittent)

After several successful quick and long off/on cycles, the diagnostic run
reproduced the failure on cycle 3 with shadows disabled for 100 seconds. The
sequential decline reason was "The cascade viewport has no shadow render
pipeline." The rendering log showed a recreated cascade viewport applying an
`AdvancedRenderPipeline` transition while its shadow request was pending.

`XRRenderPipelineInstance.Pipeline` previously inspected only the applied
`_pipeline`. An early getter call before the render thread applied an explicit
`ShadowRenderPipeline` request created and requested a default pipeline,
superseding the shadow request. The working-tree fix returns a pending pipeline
when no applied pipeline exists and guards default assignment with the same
transition lock used by explicit requests. Pipeline application remains owned by
the render thread. Review also identified that a default constructed before a
concurrent explicit request could be rejected and left subscribed to global
settings events. The request now reports acceptance so the rejected candidate
is destroyed outside the transition lock. `XRE_DIRECTIONAL_SHADOW_AUDIT=1` adds
per-frame atlas state.

### Resumed validation

The first resumed build was blocked by seven errors in the concurrent networking
refactor. A later retry completed successfully with zero warnings and errors in
3 minutes 5.60 seconds. No networking files were changed for this investigation.

The initial rebuilt unit-world launch produced a black image. Its Advanced
execution blocker identified an `AotParityViolationException` for reflective
construction of `XRShader` in `RuntimeThirdPartyAssetLoadingServices.Load`.
This prevented the visibility shader family from loading. Restarting the same
binaries with the session-only `XRE_AOT_PARITY=off` override admitted Advanced
execution. The following results validate shadows under that explicit override;
they do not validate NativeAOT parity or the default strict unit-world startup.

- All five `CastsShadows` off/on cycles (60, 80, 100, 120, and 140 seconds off)
  passed. Each mutation was read back; each recovery retained Advanced execution,
  published all four sampleable cascade tiles with `fallback=None`, and reported
  zero stale samples. No grouped-plus-sequential cascade failure or pipeline
  application failure appeared in the live logs.
- Fresh arch, hallway, and overhead courtyard captures were inspected in normal
  output and `ShadowMask` mode. Masks change with camera position and contain
  both lit and shadowed regions. All six captures report zero non-finite samples.
  The shadow-disabled control lights surfaces that become dark again after
  recovery. Shadowed regions remain very dark without probes/ambient, as in the
  prior investigation; these checks do not establish complete visual-quality
  acceptance across light settings and materials.
- Focused `Shadow|AdvancedGlobalResource|AdvancedShaderAccess` tests: **185 passed,
  34 failed, 219 total**. Failures comprise 30 source-text/index assertions and
  four numeric expectations (three atlas allocation/UV/generation expectations
  and the `AdvancedViewRecord` size expectation of 896 versus actual 944 bytes).
  The allocator algorithms are untouched by this task's decline-diagnostic diff.
- Pipeline lifecycle/purpose/host-service tests: **101 passed, 16 failed, 117
  total**. Failures include two source-text assertions, three runtime exceptions,
  and eleven behavioral expectations involving resource layouts, generation
  keys, allocator formats, retention, and factory routing. The full suites are
  not green; no clean baseline run was performed to attribute every failure.
- Tests were not modified. An initial unrestricted parallel test build lost
  MSBuild child nodes; retrying with two build workers completed and produced
  the reported test results. Vulkan validation layers were disabled in the
  fixture, so absence of VUID lines is not a validation-layer certification.
- The isolated session was stopped after capture review; no validation editor
  process was left running.

## Startup atlas rejections

"Atlas writers ... rejected by backend submission (status=Failed)" during load
lines up with present-now frame retries while Sponza meshes are cold. The manager
retries the requests and the atlas renders once meshes are warm. No change needed.

## Follow-ups

- Capture light probes (or provide ambient) for the Advanced Sponza scene so
  shadowed regions are not pure black.
- Fix the strict unit-world `XRShader` factory/parity blocker in the separately
  owned asset-loading work, then repeat validation without the parity override.
- Triage the recorded automated test failures using the
  [complete failure checklist](../../todo/rendering/shadow-and-pipeline-validation-failures-todo.md)
  before treating the broader renderer/resource lifecycle as validated.
- `DirectionalShadowAtlasFallbackTests` and
  `CascadedShadowDefaultsAndForwardShaderTests` contain source-shape assertions
  (`TryRenderDirectionalCascadeGroupSequentially(plan, light, entry, collectVisibleNow)`
  and the single-line sequential tile call) that already fail at `HEAD`. They need
  updating with explicit clearance.

## Evidence locations (disposable)

`Build/_AgentValidation/20261002-090000-dirlight-shadows/` contains `mcp-captures/`
(`fix/`, `sweep/`, `tune/`, `default-arch/`, `diag/`) and `renderdoc/`
(`dirshadow141_frame691.rdc`, `dirshadowfix141_frame1091.rdc`,
`dirshadowdiag141_frame437.rdc`), with analysis scripts in `scratch/`.

The resumed run adds `mcp-captures/codex-validation/`,
`reports/codex-toggle-results.jsonl`, `reports/codex-toggle-summary.json`,
`reports/codex-capture-results.json`, `reports/codex-tests/*.trx`, and
`logs/codex-validation-build.log`. The runtime session is
`00000000-000000-shared/mcp-sessions/20261002-132624-dirshadow-fix`, with successful
override-run logs under `xrengine_2026-10-02_15-35-44_pid36172`.
