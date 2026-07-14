# Render Pipeline Resource Lifecycle Recovery - 2026-07-13

## Problem

Resume the unfinished Phase 6-through-closeout resource-lifecycle work from
`render-pipeline-resource-lifecycle-todo.md`, restore a clean live editor run,
and assess integration risk against the incoming Vulkan core-hardening and
device-loss commits on `origin/rendering-vulkan-core-hardening`.

## Starting State

- Local `rendering-vulkan-core-hardening` is at `6c642c27` and the refreshed
  remote-tracking ref is nine commits ahead at `08dee8e0`.
- The resource-lifecycle implementation is staged across 110 files. Five files
  also contain an unstaged Phase 6 transaction/diagnostics layer.
- Incoming remote work overlaps 31 locally changed files. Three of the five
  unstaged Phase 6 files also overlap remote work.
- A direct `git fetch` on this machine fails because the SSH key is unavailable,
  but `refs/remotes/origin/rendering-vulkan-core-hardening` was refreshed to
  `08dee8e0` on 2026-07-13 before this investigation.

## Issues Found

- The TODO was checked through Phase 5. All Phase 6, Phase 7, Phase 8, and
  Phase 9 checklist entries remain unchecked even though part of Phase 6 is
  implemented locally.
- The unstaged Phase 6 layer adds failure-safe backend generation preparation,
  logical/physical commit coordination, rollback tests, and complete
  descriptor/layout parity validation. Its live editor behavior had not been
  validated.
- Default-pipeline materialization initially failed because GTAO framebuffer
  factories sampled `DepthView` and `Normal` without declaring those layout
  dependencies. Topological materialization could therefore run the FBO
  factory before its sampled textures existed.
- Vulkan backend-generation preparation tried to generate native framebuffers
  for attachmentless `XRQuadFrameBuffer` material helpers. Those records are
  valid logical resources but not physical Vulkan framebuffer targets.
- After those fixes, the first rendered frame hit the debug assertion that a
  cached frame-op planner state must not own a retired allocator. A physical
  plan replacement retired the active allocator, but generation/preparation
  cache keys still referenced its runtime-state snapshot.
- Windows `HttpListener` deliberately catches an
  `ObjectDisposedException` for `System.Net.HttpRequestQueueV2Handle` during
  orderly listener shutdown. With first-chance tracing enabled for all
  exceptions, the expected .NET teardown mechanism was reported as an engine
  exception.

## Attempted Solutions And Evidence

### Focused lifecycle tests

- Validation root:
  `Build/_AgentValidation/20260713-render-lifecycle-recovery/`
- Initial result before live fixes: 86 passed, 0 failed, 0 skipped.
- Final focused result after fixes: 101 passed, 0 failed, 0 skipped:
  - 88 `RenderPipelineResourceLifecycleTests`
  - 12 `VulkanCoreHardeningPhase21Tests`
  - 1 `DebugDiagnosticsTests`
- Final evidence: `logs/render-lifecycle-final-focused-tests.log` and
  `reports/render-lifecycle-final-focused.trx` under the validation root.

### Runtime fixes

- Added explicit GTAO depth/normal dependencies so the resource layout orders
  sampled views before AO framebuffer factories.
- Added `RenderFrameBufferResource.HasAttachments` and excluded attachmentless
  quad/material helpers from native Vulkan framebuffer generation.
- Evicted every frame-op planner cache entry that references an allocator at
  the physical-plan retirement boundary. This keeps cached generation and
  preparation states from restoring a retired allocator.
- Excluded the exact .NET-owned
  `System.Net.HttpRequestQueueV2Handle` teardown exception from engine
  first-chance diagnostics. Other `ObjectDisposedException` instances remain
  traceable.

### Editor build and desktop Vulkan run

- Final editor build: 0 errors. The build still reports the repository's
  existing Magick.NET vulnerability warnings and the existing surfel-GI
  unassigned-field warnings.
- The final desktop Vulkan session reached 3,247 rendered frames at 1920x1080
  with 25 active viewport commands before it was closed. The default pipeline
  committed 52 textures, 60 framebuffers, and 3 buffers; the UI pipeline also
  committed successfully.
- The allocator-retirement diagnostic recorded eviction of two stale cached
  frame-op states before owner 10 was retired, which is the path that had
  previously asserted and terminated the process.
- No first-chance exception, unhandled exception, assertion, validation error,
  device loss, generation-preparation failure, or crash was present in the
  preserved final logs under `logs/desktop-vulkan-final/`.
- MCP state evidence is in
  `mcp-output/desktop-vulkan-final-summary.json`.
- Two earlier screenshots after the allocator fix were captured from distinct
  camera poses and inspected:
  `mcp-captures/Screenshot_20260713_205620.png` and
  `mcp-captures/Screenshot_20260713_205621.png`. They show different live scene
  views, ruling out stale screenshot data, but the current Vulkan image is
  visibly very dark with clipped highlights and is not a final visual-quality
  pass.

### OpenXR-unavailable shutdown

- The configured Unit Testing World requests OpenXR and requires the requested
  backend. This machine has no available HMD/form factor, so a default launch
  cannot enter an OpenXR session and exits through the expected
  `ErrorFormFactorUnavailable` path.
- The final no-HMD launch exited with code 0. MCP started and stopped cleanly,
  and the final session contained no exception/assert/device-loss diagnostics.
- Evidence: `logs/editor-openxr-final-filtered-shutdown.*` and engine session
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/`
  `xrengine_2026-07-13_21-08-01_pid36712`.
- Active OpenXR rendering remains unvalidated on this machine; hardware/runtime
  coverage is still a Phase 8 item.

### Residual non-fatal Vulkan warnings

The editor is stable, but Phase 8 acceptance is not met. Preserved logs still
show actionable hardening/visual issues:

- attachmentless quad-material FBO names are still interpreted as sampled
  framebuffer slots by planner diagnostics for scene-copy, atmosphere, fog,
  and overdraw paths;
- initial GTAO/AO descriptors temporarily bind placeholders while descriptor
  state is dirty;
- ImGui overlay transitions repeatedly report an explicit/tracked layout
  mismatch;
- Vulkan GPU BVH picking/raycast remains explicitly unsupported and reports its
  coarse/rejected behavior;
- planner-backed HDR mip generation and auto-exposure use their documented
  interim path.

These warnings overlap the incoming Vulkan hardening work and should be
re-evaluated after integration rather than independently rewritten on the old
tip.

## Remaining TODO Work

### Phase 6

The local work now implements the core pending-backend preparation and atomic
logical/physical commit path, with rollback and descriptor-parity tests, but
the phase should remain open until the broader contract is proven:

- finish the Phase 4/6 direct active-registry mutation audit;
- prove generation-key completeness and absence of unrelated churn;
- remove routine `DeviceWaitIdle` only after fence-driven retirement covers
  every old image/buffer/view/framebuffer path on OpenGL and Vulkan;
- stress rapid resize, feature toggles, capture, failure, supersession, and
  device-loss recovery while bounding pending/retired generations;
- verify readback/planner generation selection across window, capture, probe,
  OpenVR, and OpenXR targets.

### Phases 7-9

- Phase 7 still needs the complete feature/profile coverage matrix, imported
  target tests, rapid-resize/retirement tests, command-tree mutation
  enforcement, and steady-state allocation audit.
- Phase 8 still needs OpenGL, resize/toggle stress, scene capture, light probe,
  stereo/OpenVR/OpenXR hardware coverage, a long bounded-resource soak, and a
  clean visual/validation-warning pass. RenderDoc should be used if the
  remaining descriptor/layout/brightness issues survive the remote merge.
- Phase 9 documentation, final inventory/counts, PR notes, TODO completion
  move, and branch integration are all still outstanding.

## Remote Integration Assessment

The assessment used `git stash create` to make an unreferenced synthetic commit
of tracked WIP, followed by `git merge-tree --write-tree`. The actual index,
worktree, branch, and stash list were not merged or reset.

- Local tracked WIP changes 112 paths.
- The nine incoming commits at `08dee8e0` change 215 paths with 25,459
  insertions and 3,474 deletions. Much of that total is physics/master
  integration; the final commit is the large Vulkan hardening update.
- 31 paths overlap.
- 12 paths have textual conflicts; 19 overlapping paths auto-merge.
- Raw reports:
  - `reports/merge-tree-name-only.txt`
  - `reports/merge-overlap-summary.txt`
  - `reports/merge-conflict-size-table.txt`

Risk is **medium-high**. The conflict count is manageable, but several conflicts
combine two valid ownership/synchronization designs and cannot be resolved by
choosing one side wholesale.

Highest-risk resolutions:

- `VkRenderQuery.cs`: seven hunks combine local multiview query-count/reset
  behavior with the remote generalized capacity and submitted-result epoch.
- `VPRC_SMAA.cs`: five hunks combine declared lifecycle-owned resources with
  the remote stereo/resource-cache implementation.
- `VPRC_GTAOPass.cs`: the local declared-resource helper conflicts with a large
  remote GTAO material/resource rewrite.
- `OpenXRAPI.Vulkan.cs` and `VulkanRenderer.OpenXR.cs`: true-SPS output
  telemetry and desktop-swapchain-barrier exclusion must both survive.
- `Validate-VulkanPhase524b.ps1`: five conflicts span telemetry schema,
  environment setup, and validation gates rather than cosmetic script edits.

More mechanical resolutions:

- `DefaultRenderPipeline.Resources.cs` is a two-line extent calculation that
  should preserve generation-key dimensions while taking the remote scratch
  scaling helper.
- `DefaultRenderPipeline.cs` is a large 848-line remote legacy command-chain
  addition versus an empty local side. Restoring it would violate the completed
  compatibility-removal phases, so the lifecycle architecture should win
  unless the remote code contains a narrowly extractable hardening fix.
- `OcclusionTelemetry.cs`, the two conflicted test files, and the investigation
  note need combined intent but have small/localized conflict surfaces.

The 19 auto-merged overlaps still require semantic review, especially
`VulkanRenderer.ResourcePlannerState.cs`, `XRRenderPipelineInstance.cs`, frame
loop/synchronization/resource-lifetime files, and default-pipeline command/FBO
files. Auto-merge success is not evidence that their generation, retirement,
and device-loss ordering is correct.

Recommended integration order:

1. Preserve the current staged Phase 0-5 layer and unstaged Phase 6/fix layer as
   separate intentional commits before integrating remote work.
2. Merge the refreshed remote tip; do not use a blind `pull` into the dirty
   worktree.
3. Resolve query/OpenXR/validator contracts first, then GTAO/SMAA/default
   resource ownership, then tests/docs.
4. Review every auto-merged lifecycle/hardening overlap.
5. Re-run the 101 focused tests, editor build, desktop Vulkan MCP/screenshots,
   Phase 5.2.4b validation, and real XR validation where hardware is available.

## Current Status

Desktop Vulkan resource generation and rendering are stable without
exceptions/asserts/crashes in the validated session. The default OpenXR launch
also exits cleanly when this machine reports no available HMD. The lifecycle
TODO remains incomplete: Phase 6 needs stress/backend closure, Phases 7-9 are
open, residual Vulkan warnings/visual issues remain, and the remote integration
is now in progress with its textual conflicts resolved and focused validation
passing.

## Post-Merge Startup Recovery

After the Vulkan hardening merge was resolved, the editor was reported as
crashing during startup. The process was not terminating from a native or
managed crash: the ignored local `Assets/UnitTestingWorldSettings.jsonc` still
requested `VR.Mode=OpenXR`, and SteamVR returned
`ErrorFormFactorUnavailable` while Vulkan queried the runtime-selected physical
device. Engine initialization handled the failure and returned normally with
exit code 0, but the window disappearing after startup presented as a crash.

The configured failure was reproduced in engine session
`xrengine_2026-07-13_21-50-07_pid46676`. The local Unit Testing World setting
was changed explicitly to `VR.Mode=Desktop`; no implicit OpenXR or render-backend
fallback was added. Real OpenXR launches remain opt-in through the existing
settings, environment overrides, and dedicated VS Code launch profiles.

Validation after the local setting correction:

- the editor build completed with 0 errors;
- two launches using the ordinary `--unit-testing --mcp --mcp-allow-all`
  command, with no VR environment override, stayed alive and responsive;
- the first fixed-config session reached more than 3,500 rendered frames and
  the second reached 1,640 frames at 1920x1080 with 18 active viewport commands
  and resource generation 1;
- MCP engine state reported edit mode with no active VR runtime, and viewport
  screenshots were captured and visually inspected from live rendering;
- neither fixed-config session contained unhandled/first-chance exceptions,
  assertions, device-loss reports, generation-preparation failures, or engine
  initialization errors.

Evidence is under
`Build/_AgentValidation/20260713-render-lifecycle-recovery/logs/startup-after-merge/`
and
`Build/_AgentValidation/20260713-render-lifecycle-recovery/mcp-captures/startup-fixed-config/`.

## Directional Shadow Atlas Camera Flicker And Atlas-Off Black Output

The next user report identified two related Vulkan directional-shadow failures:

- camera movement usually, but not always, produces one black viewport frame;
- disabling `UseDirectionalShadowAtlas` leaves the viewport black.

The decisive capture isolated the failure to deferred lighting. On the exact
bad frame, the pipeline textures were populated normally:

- `AlbedoOpacityTexture` average was `0.7828`;
- `NormalTexture` average was `0.3332`;
- `DepthViewTexture` average was `0.9225`;
- `LightingAccumTexture` was exactly zero.

At the same camera pose after the pipeline settled, `LightingAccumTexture`
averaged `0.14788`. Deferred debug mode 15 behaved the same way: it remained
zero on the bad frame and produced nonzero cascade-debug output after settling.
The light-combine shader was therefore not merely sampling a wrong cascade; the
whole light-combine contribution was being skipped while the G-buffer remained
valid.

The common root was a render-context lifetime violation during Vulkan command
chain rebuild/replay. `VPRC_LightCombinePass.Execute` required
`RenderState.Scene` and read only `RenderState.WindowViewport`. Those values are
transient: `PushMainAttributes` establishes them and `PopMainAttributes` clears
them. `XRRenderPipelineInstance` separately retains `LastWindowViewport`,
`LastSceneCamera`, and `LastRenderingCamera` specifically for commands that are
rebuilt or replayed outside that scope. A camera cut and a directional-atlas
mode change both invalidate render resources or the reusable command schedule,
so either event could execute light combine after the transient scope was
popped. The pass then returned before drawing any lights, leaving its cleared
accumulation target black. Render-on-demand and command-buffer reuse could
preserve that bad result, explaining why the camera symptom was usually one
frame while the atlas-off symptom could remain visible.

The atlas transition also exposed a resource-publication problem. Atlas slot
metadata was published per cascade before the complete rendered generation was
ready, and the legacy raster-depth array could be non-null before its Vulkan
image, descriptor, and active layers were sampleable. These resources must be
published as complete rendered generations and must fail lit until the renderer
reports them ready.

Implemented fixes:

- light combine now falls back from transient `RenderState.WindowViewport` to
  `LastWindowViewport` for its world, cascade-policy, and light-collection
  lookups, and only skips when neither transient scene nor persisted world is
  available;
- directional cascade atlas slots are staged and published atomically as one
  complete generation after the grouped cascade render commits;
- legacy cascade receivers and directional atlas pages are advertised to the
  shader only after rendered-content and renderer descriptor-readiness checks;
- directional shadow resource changes explicitly invalidate Vulkan's cached
  command-chain schedule so rebuilt bindings cannot reuse an obsolete resource
  plan.

Validation after the fixes:

- disabling the directional atlas in the live editor no longer left the
  viewport permanently black;
- after the persisted-context fix, the live Vulkan lighting target at the test
  pose averaged `0.16655` with a maximum of `0.61719` instead of remaining
  cleared;
- `DirectionalCascadeAtlasStaleFrameTests` passed 10/10, including the persisted
  viewport-context regression;
- `DirectionalShadowAtlasFallbackTests` passed 16/16;
- the cleaned editor build completed with 0 errors. Its 218 warnings are the
  existing dependency advisories and unrelated compiler warnings in the merged
  tree;
- a cleaned Vulkan editor launch reached its first rendered frame in 4.9
  seconds, remained alive through the 12-second smoke window, and reported no
  unhandled exception, assertion, initialization error, or device loss. The
  session is
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-14_06-14-58_pid55044/`.

The final repeated immediate-frame capture loop could not be completed in this
Windows session. An earlier render-on-demand MCP capture wedged the serialized
MCP request queue; after that process was stopped, Windows HTTP.sys returned
`The handle is invalid` both to new `HttpListener` instances and to `netsh http`,
on multiple unused ports. Restarting HTTP.sys would disrupt other active
editor/OpenXR work, so it was deliberately not attempted. Evidence from the
successful live captures is under
`Build/_AgentValidation/20260713-render-lifecycle-recovery/live-shadow-fix/`.
