# Vulkan Material Readiness And Magenta Bloom

Date: 2026-07-30

Status: Resolved

## Problem

The Vulkan large-scene validation appeared magenta even though the test model
included an authored checker texture. Character locomotion also captured the
cursor during editor-driven debugging.

## Findings

- The glTF material was not the first magenta stage. `AlbedoOpacity`,
  `LightingAccumTexture`, and `HDRSceneTex` contained finite non-magenta scene
  data.
- `BloomBlurTexture` was the first solid magenta target, and the final
  post-process output inherited it.
- The bloom mip-zero copy material declared an empty texture array while its
  `SourceTexture` sampler was supplied only through a draw callback. Vulkan
  therefore published a zero-texture descriptor layout and sampled its magenta
  fallback.
- GPU material readiness was a separate correctness issue: descriptor slot zero
  had previously been treated as ready even though it is reserved for fallback.

## Resolution

- Bloom now resolves the input framebuffer texture when the declared mip-zero
  framebuffer material is created. The material owns a stable, real source
  texture slot before Vulkan descriptor publication.
- A stale source/material mismatch is treated as a declared-resource generation
  error instead of mutating the material texture layout during the draw.
- The intermediate attempt that refreshed the material texture slot during
  `Execute` fixed the image but caused primary-command signatures to change.
  It was replaced by the stable declaration above.
- Material texture resolution now reports typed `Ready`, `Pending`,
  `Unsupported`, and `Failed` states. GPU material rows are submitted only after
  descriptor publication.
- The dedicated material cohort disables temporal AA and sets
  `"Locomotion": false`. All large-scene Vulkan cohorts now use the flying
  camera, so editor debugging no longer captures the cursor.

## Validation

- Focused GPU material/bloom contract tests: 7/7 passed.
- Flying-camera Vulkan capture (historical disposable evidence, no longer retained):
  `Build/_AgentValidation/20260729-vulkan-runtime-organization/mcp-captures/Screenshot_20260730_013138_093_c36e905ac306464f8a0c9355c93aea8e.png`.
- `BloomBlurTexture`: finite, minimum RGB `0`, maximum RGB `0.7373047`,
  average RGB `0.1144064`; it is no longer solid magenta.
- `FinalPostProcessOutputTexture`: finite, minimum RGB `0`, maximum RGB
  `1.2470703`, average RGB `0.28603542`.
- Strict authored-material capture (historical disposable evidence, no longer retained):
  `Build/_AgentValidation/20260729-vulkan-runtime-organization/perf-material-final-short/reports/summary.json`.
  It reports 12/12 ready material rows, zero non-ready texture references, zero
  fallback-submitted material rows, eligible-primary reuse ratio `1.0`, zero
  primary-recording allocation bytes, zero submission rejections, and zero
  validation VUIDs.

Existing Magick.NET `NU1901`/`NU1902` dependency advisories remain visible in
build output and were not introduced by this work.
