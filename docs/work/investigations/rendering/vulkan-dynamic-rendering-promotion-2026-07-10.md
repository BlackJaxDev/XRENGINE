# Vulkan Dynamic Rendering Promotion — 2026-07-10

## Outcome

Dynamic rendering is the validated default render-target path. Explicit legacy render
passes remain a supported diagnostic fallback. A same-layout dynamic-rendering exit
barrier and a conservative sampled-final-layout fallback fixed the post-process
visibility hole that made dynamic output substantially darker than legacy output.

## Validation

- Hardware: NVIDIA GeForce RTX 4070 Laptop GPU, Vulkan 1.4.312, driver 581.57.
- `XRE_VK_RENDER_TARGET_MODE=DynamicRendering` with synchronization validation:
  Unit Testing World loaded and a same-pose viewport capture was written to
  `Build/_AgentValidation/20260710-1015-vulkan-dynamic-promotion/mcp-captures/Screenshot_20260710_113204.png`.
  `log_vulkan.log` from `xrengine_2026-07-10_11-31-29_pid39104` contains zero
  `VUID-`, `SYNC-HAZARD`, and `UNASSIGNED` messages.
- `XRE_VK_RENDER_TARGET_MODE=LegacyRenderPass` produced the matching scene at the
  same camera pose; capture:
  `Screenshot_20260710_112906.png`.
- Fresh focused test run:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "VulkanDynamicRenderingMigrationTests|FullyQualifiedName~VulkanDynamicRenderingMultiviewContracts_PropagateViewMaskAcrossBeginInheritanceAndPipeline" -v:minimal`
  passed 30/30.
- Editor build completed with zero errors. Existing Magick.NET NuGet advisory
  warnings remain outside this migration.

## Optional Backend Decisions

- Dynamic-rendering local read: no current pass has a measured bandwidth or pass-cost
  win that justifies enabling the dormant plan; it remains off.
- Descriptor heap: staged device-local updates, transfer synchronization, counters,
  and failure diagnostics are complete. Neither locally enumerated GPU exposes
  `VK_EXT_descriptor_heap`, so descriptor indexing remains the v1 production
  backend; native heap shaders and acceleration-structure heap descriptors are
  explicitly deferred.
- Shader objects: capability availability alone is not a second program-binding
  backend. The RTX device reports shader-object support while the local Intel Arc
  comparison device does not. Pipeline/GPL remains the required backend pending
  measured native shader-object parity.
- XR foveation: fragment-shading-rate properties are reported, but no OpenXR/OpenVR
  runtime/device matrix demonstrated an attachment benefit. Foveation stays off by
  default and explicit unsupported requests remain failure-visible.
- Transient memory: neither local adapter exposes lazily allocated memory. The
  normal device-local attachment path is retained. GPU-driven fallbacks identify
  unsupported explicit requests; ray tracing remains gated on a real descriptor
  binding backend.

## Known Limits

The broad `--filter Vulkan` suite still has independently stale source-contract tests
after the descriptor source split. That is tracked as suite maintenance, not a runtime
validation failure. The focused dynamic migration contracts are current and clean.

The full unit-test project was also attempted after the clean solution build. It exposed
unrelated failures in `AssetCacheTests.LoadAnimationClip_GeneratesAndUsesThirdPartyCacheAsset`
and `EngineDefaultsProjectPersistenceTests.SaveProjectEngineDefaults_ReloadProject_PersistsActiveEngineDefaults`,
then required stopping its newly launched test host. Its captured output is
`Build/_AgentValidation/20260710-1015-vulkan-dynamic-promotion/logs/full-unit-tests.txt`.

## 2026-07-10 OpenXR desktop/left-eye regression diagnosis

### Reproduction and observations

- Unit Testing World: Vulkan dynamic rendering, `MonadoOpenXR`, configured as
  `SinglePassStereo`, with `FullIndependentRender` desktop mirroring.
- The requested single-pass mode resolves to the `OpenXrSinglePassCompatibility`
  path: it renders two sequential external-eye swapchains rather than a true
  multiview render.
- Captures with normal culling showed the left eye missing the banner and nearby
  meshes while the right eye contained them:
  `OpenXRPreview_LeftEye_20260710_121757.png` and
  `OpenXRPreview_RightEye_20260710_121758.png`.
- The occlusion profiler reported `CpuQueryAsync`, 131 tested candidates, 113
  culled/skipped candidates, and a current view scope of
  `EditorDesktopWhileVr`. Disabling occlusion for an A/B capture restored the
  missing left- and right-eye geometry. This was diagnostic-only; it does not
  constitute a fix.

### Cause of missing geometry

The OpenXR compatibility path shares one `RenderCommandCollection` between both
eyes and explicitly assigns it the *left* viewport's pipeline ownership. The CPU
occlusion coordinator then consumes view-scoped temporal decisions through that
shared collection. Its observed `EditorDesktopWhileVr` key shows that the
desktop/eye separation is not being maintained for this path. Consequently,
stale or wrong-view `CpuQueryAsync` decisions reject meshes for the independent
desktop and left-eye renders. This is an occlusion-state ownership/cache bug, not
ordinary frustum culling.

### Cause of black desktop flicker

The same compatibility configuration has no frame-time headroom: it renders an
independent desktop scene plus sequential left and right external-eye scenes on
each frame. The captured profiler showed a 90 Hz budget of 11.11 ms but frame
latencies of p50 544 ms, p95 1.81 s, and worst 2.35 s. The Vulkan logs additionally
show the main viewport's physical-resource plan alternating, retired-image
backlogs persisting in both frame slots, and forced waits for replacement-plan
retirement. Those stalls prevent timely desktop presents, producing the observed
black/stale desktop frames.

The diagnostic launch also revealed that `XRE_VULKAN_DIAGNOSTIC_PRESET=Synchronization`
is not a valid environment value in the current parser (it resolves to `Off`), so
that particular run did not provide validation-layer coverage. No conclusion here
relies on an absence of VUIDs.

### Required remediation

1. Do not share a left-owned command collection or CPU occlusion pass state between
   compatibility eye renders and desktop rendering; give each output an explicit
   eye/desktop key, or conservatively disable `CpuQueryAsync` for the compatibility
   path until it is correct.
2. Stop treating the sequential compatibility path as viable single-pass stereo:
   either implement the true multiview/stereo staging path or choose a mirror mode
   that does not independently re-render the desktop while VR is active.
3. Stabilize the resource-plan identity for OpenXR external targets and retire
   replaced images only after their final submit completes, eliminating the
   frame-slot retirement backlog and forced waits.

### Resolution applied

The 2026-07-10 strict OpenXR slice completed the ownership and policy changes:

- Sequential and parallel eye rendering now use independent left/right pipeline
  instances, command collections, visibility collection, swaps, and pipeline-ID
  keyed CPU-query occlusion state. True single-pass keeps one stereo pipeline and
  a conservative multiview query scope.
- `SinglePassStereo` now resolves only to `TrueSinglePassStereo`. The compatibility
  implementation enum/path and diagnostic opt-in were removed. Unsupported
  capability or render/submit failure ends the OpenXR frame without projection
  layers; sequential fallback is forbidden.
- The OpenXR external resource-planner target identity is stable per view family.
  Acquired image index/handle, image view, command-chain image key, and frame slot
  remain in command/submission identity, so this does not alias commands across
  runtime-owned images.
- OpenXR instance/system probing preserves native `Result` values. Expected
  HMD-unavailable system discovery retains the valid instance and retries only
  `xrGetSystem` with bounded exponential backoff. Permanent configuration errors
  enter `Unavailable`, and optional-extension capability output is emitted only
  when the capability set changes.

Validation:

- Editor build: 0 errors (existing Magick.NET advisory warnings remain).
- Focused strict-stereo, occlusion, planner-identity, and probe tests: 57/57.
- Monado runtime run `xrengine_2026-07-10_14-06-09_pid44064` selected strict
  `SinglePassStereo`, attempted true stereo, and did not enter a sequential path.
  Submission was rejected before `vkQueueSubmit` because command buffer
  `0x23F7523DD60` referenced retired buffer `0x23FAE0EC050`, generation `12060`.
  The engine then logged that sequential/per-eye fallback was forbidden and
  submitted the frame without projection layers. Fixing that retired-resource
  producer remains in the core-hardening/device-loss scope.

## 2026-07-10 Phase 5.2.4b remediation evidence

Baseline session `xrengine_2026-07-10_14-20-29_pid22804` used Vulkan dynamic
rendering, Monado OpenXR, strict `SinglePassStereo`, independent desktop rendering,
bloom, TSR, and CPU-query occlusion. It recorded 6 retired-uniform-buffer submit
rejections, 38 `MainViewport` signature changes, 47 physical-plan changes, 18
forced waits, and 182 retirement-backlog reports. The supplied stereo-preview
screenshot showed an approximately 111-pixel incorrect top strip; the user also
reported broken bloom, blurry TSR/motion, missing desktop meshes, and extended
desktop black flicker. The numerical strip height matches `1007 - 896`, making a
width-as-height or stale destination-region contract the initial extent hypothesis.

Implementation checkpoints completed during remediation:

- OpenXR prewarm now reserves final per-renderer slot capacity in a first pass,
  including indirect draws, before any descriptor/uniform publication.
- TSR output/history descriptors now declare two stereo layers; generic `-1` layer
  blits copy and transition the common complete layer span.
- Deferred velocity binding resolves the immutable snapshot for its owning pipeline;
  both eye cameras receive the same texel jitter, and TSR applies the
  previous-minus-current jitter delta exactly once.
- Stereo bloom default composition samples the already accumulated mip 1 once,
  matching mono rather than double-counting coarse mips.
- CPU-query state and budgets are independent by complete pipeline view key; the
  former global one-output-per-frame query slot was removed.
- An unwritten, never-presented desktop swapchain image is no longer published by
  the dirty-frame recovery path.

Fresh sync-validation runs:

- `xrengine_2026-07-10_15-49-42_pid29908` proved bloom mip targets and every
  full-resolution stereo post pass used the expected geometry: post-process,
  final post-process, TSR, and OpenXR staging were `896x1007`, with two-layer
  attachment transitions and `viewMask=0x3`. This rules out the original
  width-as-height hypothesis at these destination passes.
- Runs `15-53-29_pid3792`, `15-56-51_pid25016`, and `16-00-31_pid45160` exposed
  two remaining blockers before a 300-frame acceptance window can begin:
  inline Vulkan occlusion pools reach `vkCmdBeginQuery` without an effective reset,
  and the OpenXR mirror primary submission reports `WRITE_AFTER_PRESENT` against a
  desktop swapchain image. These are validation failures, so no visual result from
  those runs is accepted. Query reset preparation has been moved to the first query
  boundary after planner/context activation; the next run must prove the VUID is
  gone before the cross-output swapchain barrier is isolated.

Later Phase 5.2.4b probes used the installed `K:\VulkanSDK\1.4.350.0`
SDK and its Vulkan 1.4.350 Khronos validation layer. They established the
following additional root causes and fixes:

- A Vulkan multiview occlusion query consumes one consecutive query index per
  active view. The pool and reset/result ranges now cover two queries for
  `viewMask=0x3`; both availability values must complete and the result is the
  conservative OR of both eyes. The modern validation layer then completed the
  short strict-SPS probes without the former unreset-query VUID.
- Detached strict-SPS recording now excludes desktop swapchain barriers and
  blits explicitly. OpenXR external image layout publication consults the live
  tracked layout for the exact array layer, eliminating the stale
  `ColorAttachment` versus `ShaderReadOnly` descriptor report.
- Generic odd-resolution scale planning now uses the same floor convention as
  texture factories, so the `896x1007` eye family no longer oscillates between
  incompatible planned and physical extents.
- CPU-query mesh decisions execute during deferred Vulkan lowering. The command
  collection therefore restores a missing ambient camera from its owning
  pipeline, and CpuQueryAsync disables reusable OpenXR primary/secondary command
  chains so a startup empty visibility set cannot be frozen into later frames.
- The strict-SPS collection reaches 21-23 opaque Sponza candidates per frame.
  In the current validation pose all are correctly fail-visible as
  `NearPlaneUnsafe` because the HMD is inside/intersecting their coarse bounds;
  the deterministic acceptance scene still needs separate known-visible and
  known-occluded sentinels outside the near-plane exclusion before nonzero SPS
  query/cull acceptance can be claimed.

The short `probe-active-ledger-no-preview` cohort retained 10 projection-layer
frames with zero Vulkan validation errors, zero submission rejection, zero
retirement, and zero global waits. GPU time was 7.56 ms p50 / 10.42 ms p95.
It is not acceptance evidence: model uploads were still active, CPU frame p95
was 124.42 ms, and the required SPS sentinel queries were not submitted.
