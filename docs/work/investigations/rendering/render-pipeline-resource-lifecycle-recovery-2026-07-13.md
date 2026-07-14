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
has been assessed but not performed.
