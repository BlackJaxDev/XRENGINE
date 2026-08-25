# Vulkan Forward+ Graph Phase 6 — 2026-08-25

## Problem

The default Forward+ graph replayed opaque/masked forward geometry into more
than one depth-normal target and surrounded that work with full-resolution
normal/depth save, restore, and contact-copy blits. AO, bloom, temporal, probe,
and several post-process resources were also retained when their producers were
disabled.

## Issues Found

- The original graph performed a dedicated forward replay plus a shared-GBuffer
  replay and paired full-resolution backup/restore and contact copies.
- A first simplification attempt wrote forward depth/normals directly into the
  deferred G-buffer. Architecture review rejected it because deferred lighting
  could then combine forward depth/normals with deferred-only albedo, RMSE, and
  transform-ID attachments.
- The backend-neutral FBO color binding syntax did not retain a nonzero color
  attachment index. A normal copy from deferred color attachment 1 would
  therefore have been modeled as color attachment 0 on Vulkan.
- The first isolated editor run found an AO resource-ordering failure because
  the concrete AO provider used the new scene-surface names while the declarative
  AO FBO dependencies still named the deferred surface.
- A forward-geometry live probe found that the auxiliary mesh replay described
  a synthetic graph pass but executed CPU/GPU mesh submission under the original
  forward pass identity. The resulting scene-surface texture remained byte-for-
  byte identical to the deferred normal attachment.
- Architecture review found that AO incorrectly depended on the legacy contact-
  prepass toggle, contact-only consumers did not explicitly declare their
  dynamic texture reads, and velocity consumers could receive stale data when a
  frame had no eligible motion-vector mesh draws.

## Implemented Solution

- Added one complete-scene normal/depth target. It is seeded once from
  `DeferredGBufferFBO` color attachment 1 plus depth, then receives one
  opaque/masked forward overlay without clearing.
- AO and contact-shadow sampling use the complete-scene target. Deferred light
  combine continues to use the untouched deferred G-buffer.
- The forward prepass executes only when enabled forward meshes exist and AO or
  an enabled forward material requesting contact shadows consumes it. AO forces
  the overlay independently of the legacy contact-prepass toggle. Contact inputs
  are exposed only after the prepass completes in the current frame.
- AO production now follows deferred seeding and the forward overlay, so it
  samples the current frame's complete-scene surface rather than an unfinished
  or previous-frame texture.
- Removed the duplicate forward replay, G-buffer backup/restore, dedicated
  contact normal/depth copy, and their obsolete textures/FBOs.
- Added indexed framebuffer color resource names and explicit forward-prepass
  attachment metadata. Vulkan can now plan deferred-normal transfer source,
  scene-surface transfer destination, attachment writes, and later sampled
  reads without conflating color attachments.
- CPU-direct and GPU-driven auxiliary mesh submission now carry the synthetic
  prepass graph identity through to Vulkan operations. The actual forward mesh
  passes explicitly declare normal/depth sampled reads for dynamically selected
  contact-shadow variants.
- Velocity resources are published whenever temporal, vendor-upscale, or motion-
  blur consumers need them. The target is cleared to neutral every such frame;
  only the mesh replay is skipped when no command can emit motion.
- Feature generations now gate AO, bloom, temporal/velocity, atmosphere, fog,
  and debug resources. Neutral fallback textures preserve stable final-composite
  bindings. Probe synchronization is conditional, while shadow work remains
  request/light driven.
- Existing logical lifetime and aliasing intent remains backend neutral.
  Vulkan physical image aliasing stays disabled until asynchronous interval
  proof is available; this phase does not trade correctness for speculative
  memory reuse.

## Validation

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  — passed with 0 warnings and 0 errors.
- `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
  — passed with 0 warnings and 0 errors.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
  — passed with 0 warnings and 0 errors.
- `rdc doctor` — RenderDoc 1.44 replay support and the Vulkan layer are ready.
- Isolated MCP session `vulkan-phase6b-20260825` used the required Vulkan
  backend, committed a 72-resource default-pipeline generation, and produced
  two nonblack 1920x1080 screenshots from different camera poses. The images
  changed with camera movement, ruling out a stale readback.
- Final isolated MCP session `vulkan-phase6d-20260825` injected an unlit forward
  cube into Sponza and exercised the actual opaque-forward overlay. The final
  frame was nonblack and visually correct. `Normal` and
  `ForwardPrePassNormal` produced different SHA-256 hashes, and the captured
  complete-scene normal image visibly contains the cube; the combined depth
  view also contains finite scene depth.
- Steady-state `log_vulkan.log` and `log_rendering.log` contained no VUID,
  validation error, exception, or resource-generation failure. Startup-only
  unpublished/generation-mismatch deferrals resolved before the captures.
- Evidence is under
  `Build/_AgentValidation/20260825-011223-vulkan-phase6/` and the isolated
  session logs under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260825-014307-vulkan-phase6d-20260825/`.

The repository testing policy forbids changing or extending feature tests until
the live feature path is validated and the user explicitly clears test work.
No unit tests were modified or run in this implementation pass; companion
acceptance-test updates remain a separately authorized step.

## User Result

Awaiting user confirmation.
