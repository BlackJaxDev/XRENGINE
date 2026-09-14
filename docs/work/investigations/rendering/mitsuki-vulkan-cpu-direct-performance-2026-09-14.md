# Mitsuki Vulkan CPU Direct performance investigation

## Problem and scope

The user reports approximately 10 Hz render cadence while the animated Mitsuki
avatar plays its animation using Vulkan, AdvancedRenderPipeline, and CPU Direct.
The task is to identify measured bottlenecks and their code paths. Existing
working-tree changes belong to ongoing work and are preserved.

## Configuration

- Saved unit-testing settings: Vulkan, AdvancedRenderPipeline,
  `GPURenderDispatch=false`, dynamic rendering, ImGui editor, desktop mode.
- Model: `misc/Mitsuki.fbx`, animated, deferred materials with forward transparency.
- Animation: `Assets/Walks/Sexy Walk.anim`, looped, one-state animation state machine.
- Skinning and character IK enabled; TSR camera override; VSync off;
  render FPS uncapped, update target 60 Hz, fixed target 30 Hz.
- Evidence root: `Build/_AgentValidation/20260914-132215-mitsuki-vulkan-perf/`.
- Owned diagnostic editor: `mitsuki-perf-0914`.

## Initial observations

- RenderDoc tooling passed `rdc doctor`.
- Read-only counters from the already-running named `vulkan-ownership-0914`
  session confirmed CPU Direct and AdvancedRenderPipeline. One snapshot reported
  10.028 Hz output cadence, 94.399 ms render wait for fresh scene collection,
  0.968 ms scene collection, and 0.677 ms scene rendering. Its viewport was 1x1,
  so these counters are **not** an avatar rendering performance baseline.
- The routine retention script failed on a locked native DLL in another active
  session. That session was left running. The verified inactive oldest task
  output directory was removed to keep the five-root limit.

## Hypotheses and validation

Investigation in progress: distinguish background/minimized cadence, scene-update
or collection stalls, CPU Vulkan preparation, and GPU pass execution using live
counters and profile captures at a usable viewport size.

No engine fix has been attempted. User validation is pending.
