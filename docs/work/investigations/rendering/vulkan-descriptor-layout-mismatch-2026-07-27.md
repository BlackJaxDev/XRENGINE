# Vulkan descriptor layout/lifetime mismatch and stale-view flicker

Date: 2026-07-27
Status: resolved and live-validated on 2026-07-28

## Problem

The Vulkan debug descriptor validator failed while recording the final
post-process draw:

```text
PostProcessOutputTexture descriptor=ShaderReadOnlyOptimal
tracked=ColorAttachmentOptimal type=CombinedImageSampler
```

After the initial assertion was removed, camera movement could also make what
looked like a smaller scene view flicker in the upper-left portion of the editor
viewport.

## Why the smaller view flickered

It was not a second camera or an inset UI viewport. Live editor diagnostics
reported zero hidden/offscreen UI viewports and one active 1920x1080 scene
viewport.

A descriptor lifetime or layout validation failure rejected the current Vulkan
queue submission. The renderer then deliberately presented the last completed
content instead of presenting an invalid frame. One buffered presentation image
could therefore contain an older camera pose. Alternating between current and
preserved content produced the apparent smaller-view flicker.

A pre-fix fixed-camera sequence showed exact A/B/A frame alternation and changes
across the whole scene viewport, rather than a separately rendered rectangular
inset. The apparent size difference came from the older frame's different camera
framing.

## Root causes

The failure was a set of related native-resource generation bugs:

1. Captured draws retained logical textures, but descriptor transitions could
   re-resolve those textures after a render-resource plan replacement. The
   descriptor still referenced the old native image while the barrier targeted
   the new image.
2. Captured descriptor allocation keys omitted the resource fingerprint when
   all active bindings supported `UPDATE_AFTER_BIND`. Rewriting the shared set
   changed the descriptor contents observed by previously recorded bindings;
   update-after-bind does not snapshot old contents.
3. Secondary command buffers recorded descriptor image requirements without a
   primary-command-buffer execution transition. This was exposed by the dynamic
   UI/font-atlas path.
4. A reusable shared-material fast path trusted its previously stored resource
   fingerprint. Texture streaming could retire and replace an imported image
   view without forcing that path through full descriptor publication.
5. Completed command buffers could publish recorded layout state for a retired
   image generation after Vulkan recycled the same numeric `VkImage` handle.
6. Submission validation rejected a resource that began retirement after
   recording even when the exact recorded generation was already pinned by the
   command buffer.

## Fix

- Captured descriptor allocations always keep their exact resource-fingerprint
  variant, including update-after-bind layouts, and captured variants are never
  refreshed in place.
- Draw preparation and command recording transition the exact native image
  references from the published descriptor-set snapshot.
- Per-operation descriptor frame slots are preserved through primary,
  secondary, and indirect recording paths.
- Secondary command buffers retain descriptor-specific image requirements.
  Primaries validate generations and emit the required transitions immediately
  before executing those secondaries.
- Untracked first-use descriptor images receive an explicit
  `Undefined -> required layout` transition.
- Secondary inheritance metadata, simultaneous-use flags, reset safety, and UI
  command-buffer copy-on-write prevent recorded state from being mutated while
  referenced.
- A generation already recorded and pinned may submit once after retirement
  begins; newly discovered retiring resources and destroyed resources still
  reject submission.
- The reusable material path recomputes the current live resource fingerprint
  before returning a published descriptor set.
- Layout publication ignores touched image state whose recorded resource
  generation no longer matches the currently published generation for that
  numeric handle.

The validation boundary remains strict: invalid or destroyed descriptor
resources are still rejected rather than silently rebound or hidden behind a
fallback.

## Validation

- `rdc doctor`: passed with RenderDoc 1.44 and the Vulkan layer available. A
  capture was unnecessary after deterministic source/log evidence and live
  reproduction isolated the failure.
- `XREngine.Runtime.Rendering.Vulkan.csproj`: built with zero errors. The
  existing Magick.NET `NU1901`/`NU1902` audit warnings remain.
- `VulkanStablePacketAndDescriptorTests`: 49 passed, 0 failed. Result:
  `Build/_AgentValidation/20260727-vulkan-descriptor-layout/reports/targeted-tests/vulkan-descriptor-final.trx`.
- Isolated live editor session `descriptor-stream-layout-fixed2-0728`:
  - clean startup after texture streaming settled;
  - eleven immediate/interpolated camera positions around Sponza;
  - zero rejected submissions, stale-content presentations, descriptor layout
    mismatches, entry-state/generation mismatches, retirement errors, destroyed
    resource errors, secondary conflicts, or Vulkan VUIDs;
  - session was stopped through the named session manager.
- Fixed-camera live sequence: 24 completed frames, 0 failed, 0 dropped, all
  eight readback queue slots exercised, one unique image hash, and zero changed
  pixels between every adjacent frame. Manifest:
  `Build/_AgentValidation/20260727-vulkan-descriptor-layout/mcp-captures/final-sequence/ViewportSequence_20260728_082920_639_f72c14aa78b6445f82195a54d0473259/manifest.json`.
- Final Vulkan log:
  `Build/_AgentValidation/mcp-sessions/descriptor-stream-layout-fixed2-0728/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-07-28_01-21-34_pid18884/log_vulkan.log`.

## Evidence and attempt history

- Original failure log:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-27_17-38-01_pid4648/log_vulkan.log`.
- Pre-fix viewport metrics:
  `Build/_AgentValidation/20260727-vulkan-descriptor-layout/reports/final-motion/static-viewport-metrics.json`.
- Earlier isolated sessions successively exposed the post-process native-image
  mismatch, secondary font-atlas requirements, pending-retirement acceptance,
  imported streaming-view reuse, and stale layout publication. Each invariant
  was fixed without suppressing validation; the final clean session exercised
  all of those paths together.
