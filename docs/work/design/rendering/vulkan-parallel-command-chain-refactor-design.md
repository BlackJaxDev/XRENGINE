# Vulkan Parallel Command Chain Refactor Design

Last Updated: 2026-06-17
Owner: Rendering
Status: Proposed
Implementation TODO: `docs/work/todo/rendering/vulkan-parallel-command-chain-refactor-todo.md`
Related Testing Guide: `docs/work/todo/rendering/vulkan-frame-loop-performance-todo.md`

## Problem

The Vulkan backend currently spends too much CPU time in the window frame loop,
especially in command-buffer recording and frame-resource churn. Recent profiling
showed that the actual default render pipeline can enqueue useful work quickly,
but the loop that drains frame ops, sorts them, plans resources, records command
buffers, and retires resources remains slow.

The root architecture problem is that Vulkan work is still shaped like an
immediate render-thread command stream. Visibility, pass scheduling, resource
planning, descriptor preparation, and command-buffer recording are too tightly
coupled to the final render-thread submit path. As a result:

- independent views and passes cannot record in parallel;
- dynamic overlay/text changes can poison otherwise reusable scene command
  buffers;
- command-buffer signatures mix structural state with per-frame data;
- render-thread work scales with total draw/pass count rather than with the
  amount of structural change in the frame;
- VR eye views, shadow views, reflection/probe views, and main views cannot be
  prepared as independent work chains.

Vulkan is well suited to a different shape: collect immutable render packets,
record secondary command buffers in parallel from those packets, then let the
render thread submit a small ordered primary command buffer.

## Design Goal

Refactor Vulkan rendering around parallel command-chain generation:

- `CollectVisible` and pass setup produce immutable, per-view/per-pass render
  packets.
- Worker threads build or record Vulkan command chains from those packets using
  per-thread command pools and scratch allocators.
- The render thread becomes an orchestrator: acquire image, select the resource
  plan, emit global barriers, execute already-recorded secondary command
  buffers, submit, and present.
- Static scene command buffers are reused across frames while dynamic per-frame
  data is refreshed separately.
- Volatile overlays, text, profiler UI, and debug tools record into their own
  small dynamic command buffers so they do not invalidate the main scene.

Target settled-scene behavior:

- Moving the camera updates per-frame uniform/storage data without rebuilding
  static scene command buffers.
- Stable shadow-map views reuse command chains until light/view/caster sets
  change.
- VR stereo views record in parallel where their resources and passes are
  independent.
- Dynamic UI/text/profiler work records every frame only in small overlay
  command chains.
- Render-thread `RecordCommandBuffer` median is dominated by primary
  orchestration, not full scene draw recording.

## Vulkan Reality Check

Vulkan supports parallel command recording, not unordered execution inside one
graphics queue.

Allowed and useful:

- Multiple threads may record different command buffers concurrently.
- Each recording thread must use its own command pool or externally synchronized
  pool.
- The primary command buffer can execute secondary command buffers with
  `vkCmdExecuteCommands`.
- Independent work can be submitted to separate graphics/compute/transfer queues
  when the device exposes useful queues and the dependency graph allows it.

Not true:

- A single render thread cannot make one graphics queue execute command buffers
  in parallel just by submitting them together.
- Secondary command buffers executed by one primary are logically ordered at the
  execution point.
- Multi-queue submission does not guarantee real hardware overlap; it must be
  treated as an optional backend optimization.

Therefore the first refactor target is CPU parallelism and command reuse. GPU
parallelism through multiple queues is a later scheduling layer.

## Non-Goals

- Do not rewrite the render graph wholesale.
- Do not require multi-queue graphics execution for correctness.
- Do not hide Vulkan failures behind CPU/OpenGL fallbacks.
- Do not make `CollectVisible` mutate renderer, material, descriptor, or FBO
  objects from worker threads.
- Do not make every draw a separate secondary command buffer. The granularity is
  per chain/bucket/pass, not per object.
- Do not rely on driver command-buffer reuse if our own signatures say the
  structural work changed.

## Target Architecture

### Current Shape

Current simplified Vulkan path:

1. Scene/pipeline code enqueues `FrameOp` objects.
2. Render thread drains `FrameOp`s.
3. Render thread sorts/coalesces ops.
4. Render thread prepares the resource planner.
5. Render thread records one primary command buffer, with limited secondary
   support for a few non-render-pass op types.
6. Render thread submits that command buffer.

### Proposed Shape

New simplified Vulkan path:

1. `CollectVisible` builds immutable visibility sets per view.
2. Pipeline/pass code turns visibility into immutable `RenderPacket`s.
3. The render graph/compiler builds an ordered `CommandChainSchedule`.
4. The resource planner resolves physical resources and barrier plans once per
   structural frame.
5. Worker threads record eligible `CommandChain`s into secondary command
   buffers.
6. The render thread records or reuses a tiny primary command buffer that:
   - emits swapchain/global barriers;
   - begins render pass or dynamic rendering scopes;
   - executes secondary command buffers for each chain;
   - emits required inter-pass barriers;
   - executes volatile overlay chains;
   - ends rendering and submits.

## Core Types

### `RenderViewKey`

Stable identity for a view that can own a command chain:

```csharp
internal readonly record struct RenderViewKey(
    int PipelineIdentity,
    int ViewportIdentity,
    int ViewIndex,
    RenderViewKind Kind,
    int LightIdentity,
    int CascadeIndex);
```

`Kind` examples:

- `Main`
- `VREye`
- `Shadow`
- `Reflection`
- `Probe`
- `Overlay`

### `VisibilityPacket`

Immutable output from collection:

```csharp
internal sealed class VisibilityPacket
{
    public RenderViewKey ViewKey { get; init; }
    public ulong SceneRevision { get; init; }
    public ulong CameraRevision { get; init; }
    public ReadOnlyMemory<VisibleRenderable> Renderables { get; init; }
    public BoundsFrustumSnapshot Frustum { get; init; }
}
```

Rules:

- It is immutable after publication.
- It stores stable handles/IDs and snapshots, not mutable live collections.
- It may contain pooled arrays, but ownership transfers to the frame scheduler
  until the frame retires.

### `RenderPacket`

Immutable per-pass work item produced from visibility:

```csharp
internal sealed class RenderPacket
{
    public RenderViewKey ViewKey { get; init; }
    public int PassIndex { get; init; }
    public RenderPassMetadata PassMetadata { get; init; }
    public XRFrameBuffer? Target { get; init; }
    public RenderPacketVolatility Volatility { get; init; }
    public ReadOnlyMemory<DrawPacket> Draws { get; init; }
    public ReadOnlyMemory<DispatchPacket> Dispatches { get; init; }
    public ulong StructuralSignature { get; init; }
    public ulong FrameDataSignature { get; init; }
}
```

`StructuralSignature` includes command-buffer-baked state:

- pass/target/rendering format;
- pipeline identity and shader variant;
- mesh/index/vertex layout;
- material shader/resource layout;
- descriptor layout, not descriptor contents;
- draw topology and indexed/non-indexed shape;
- fixed viewport/scissor count and attachment count.

`FrameDataSignature` includes data that can be refreshed without re-recording:

- camera matrices;
- object transforms;
- skinning/blendshape buffer contents;
- material constants that live in refreshable buffers;
- dynamic uniform offsets;
- descriptor sets when the set handles are stable.

### `CommandChain`

Unit of Vulkan recording/reuse:

```csharp
internal sealed class CommandChain
{
    public CommandChainKey Key { get; init; }
    public CommandChainState State { get; set; }
    public CommandBuffer SecondaryCommandBuffer { get; set; }
    public ulong StructuralSignature { get; set; }
    public ulong ResourcePlanRevision { get; set; }
    public int LastRecordedFrameSlot { get; set; }
}
```

Suggested key:

```csharp
internal readonly record struct CommandChainKey(
    int FrameSlot,
    RenderViewKey ViewKey,
    int PassIndex,
    XRFrameBuffer? Target,
    int ChainOrdinal);
```

The `FrameSlot` dimension avoids command-buffer use-after-free while previous
frames are in flight.

### `CommandChainSchedule`

Ordered plan for the render thread:

```csharp
internal sealed class CommandChainSchedule
{
    public ulong StructuralSignature { get; init; }
    public ulong ResourcePlanRevision { get; init; }
    public ReadOnlyMemory<RenderPassChainGroup> Groups { get; init; }
}
```

Each `RenderPassChainGroup` defines:

- pass index;
- target;
- rendering mode;
- attachment/load/store signature;
- barrier plan to emit before the group;
- ordered secondary chains to execute inside the pass;
- whether the pass may be recorded/executed as secondary command buffers.

## Volatility Classes

Every packet/chain gets a volatility classification. This is the key to making
reuse predictable.

### `StaticStructural`

The command sequence can be reused until structure changes.

Examples:

- static opaque scene mesh draw chain;
- stable shadow caster chain;
- stable skybox draw;
- stable full-screen post-process pass with stable descriptor-set handles.

### `FrameDataOnly`

The command sequence can be reused if frame data is refreshed before submit.

Examples:

- moving camera with stable draw list;
- moving object transforms written into a stable per-frame buffer;
- material constants written into stable per-frame buffers;
- same descriptor-set handles with updated buffer contents.

### `DynamicCommand`

The command sequence itself changes frequently and should be isolated into a
small chain.

Examples:

- UI text where instance count changes;
- profiler overlay;
- transient debug gizmo batches;
- editor selection outlines when object count changes;
- any pass with changing draw count that is not using indirect count buffers.

### `StructuralDirty`

The command chain must be re-recorded.

Examples:

- shader variant/pipeline changes;
- render target attachment format/count changes;
- descriptor layout changes;
- mesh/index buffer topology/layout changes;
- material resource layout changes;
- render-pass load/store policy changes;
- visible draw set changes for non-indirect static chains.

## Threading Model

### Threads

Main/game thread:

- owns scene mutation;
- advances simulation;
- produces stable scene revisions.

Visibility workers:

- read immutable scene snapshots;
- build `VisibilityPacket`s;
- do not touch Vulkan objects directly.

Recording workers:

- own per-thread Vulkan command pools;
- record secondary command buffers;
- use immutable `RenderPacket`s and resolved Vulkan handles;
- do not mutate shared descriptor pools, resource planner state, or render
  graph state while recording.

Render thread:

- owns swapchain acquire/present;
- owns primary command-buffer recording/submission;
- owns final command-chain schedule selection;
- publishes frame-retirement events.

### Worker Local State

Each recording worker owns:

- graphics command pool;
- optional compute command pool;
- scratch arena for sort keys and temporary draw arrays;
- small descriptor binding scratch;
- command-buffer bind state cache;
- debug label scratch buffer.

Per-thread command pools are mandatory. A shared command pool is legal only with
external synchronization, which defeats the purpose of parallel recording.

## Data Flow

### Phase 1: Snapshot

Create a frame snapshot:

- render frame ID;
- active render graph revision;
- scene structural revision;
- material table revision;
- resource planner revision;
- active camera/view list;
- active lights/shadow views;
- active XR eye views.

The snapshot must be cheap and immutable for the rest of the frame.

### Phase 2: Parallel Visibility

Build visibility packets in parallel for all independent views:

- VR left/right eye;
- mono/main editor camera;
- each shadow cascade/view;
- probe/reflection views;
- editor overlay view if it needs scene information.

Visibility output is a packet, not a command buffer. This keeps culling and
Vulkan recording decoupled.

### Phase 3: Packet Build

Convert visibility packets into render packets:

- group by pass;
- resolve material strategy;
- resolve mesh submission strategy;
- split static and dynamic work;
- compute structural and frame-data signatures;
- assign chain keys and ordinals.

This phase can run in parallel after required render graph metadata is frozen.

### Phase 4: Resource Plan

The resource planner remains centralized:

- allocate or reuse physical images/buffers;
- resolve FBO attachment signatures;
- produce pass barrier plans;
- produce descriptor/resource binding snapshots;
- publish a resource plan revision.

Workers must record against a completed resource plan. They should not cause
planner replacement while recording.

### Phase 5: Parallel Secondary Recording

Record eligible chains in parallel:

- one secondary per chain, or one secondary per contiguous compatible chain
  group;
- per-thread command pools;
- correct inheritance for legacy render passes;
- correct dynamic rendering inheritance for VK dynamic rendering;
- no descriptor allocation while recording unless the allocator is explicitly
  worker-safe and frame-owned.

For render-pass/dynamic-rendering secondaries:

- primary begins the render pass or dynamic rendering;
- secondary command buffers are recorded with render-pass-continue inheritance;
- primary executes them with `vkCmdExecuteCommands`;
- primary ends the pass.

For compute/transfer chains:

- record as standalone secondary only where legal and useful, or record as
  small primary sidecar command buffers in a later multi-queue layer.

### Phase 6: Primary Orchestration

The primary command buffer should contain:

- frame timing/profiler queries;
- global swapchain barriers;
- pass barriers from the centralized barrier planner;
- begin rendering/render pass;
- execute ordered secondary chains;
- end rendering/render pass;
- optional volatile overlay chains;
- swapchain present transition;
- final queries/labels.

The primary command buffer may itself be reusable if:

- pass group structure is stable;
- secondary command-buffer handles are stable per frame slot;
- resource plan revision is stable;
- profiler/query state is stable.

If the primary is reused, workers still may re-record volatile secondaries for
that frame slot before submission.

## Render Ordering

Parallel generation must not change final render order. The schedule is still
ordered by:

1. compiled render graph dependencies;
2. pass index/order;
3. target/rendering group;
4. pipeline/viewport scheduling identity;
5. material/mesh sorting policy;
6. original transparent order where required.

Opaque chains can be reordered for batching only where the existing renderer
already allows it. Transparent chains preserve depth/order semantics.

## VR and Multi-View Strategy

### Short Term

Record each eye as separate command chains:

- `RenderViewKind.VREye`, `ViewIndex = 0/1`;
- separate visibility packets;
- parallel secondary recording;
- primary executes left/right chains in the render graph's required order.

This is the least risky path and mirrors the current view model.

### Medium Term

Support multiview-compatible chain recording:

- one visibility packet may contain both eyes;
- draw packets carry view mask/indexed viewport data;
- shaders use multiview-compatible built-ins;
- command chain records once for both eyes when pipeline/material support it.

### Shadow Views

Shadow map views are strong candidates for parallel recording:

- each light/cascade can generate visibility independently;
- chains target independent atlas regions or FBOs;
- dependencies are mostly before lighting/composition;
- static shadow caster sets can reuse heavily.

If atlas packing changes, the affected shadow chains become structural dirty.

## Descriptor and Resource Rules

Descriptor/resource mutation is the most dangerous part of this refactor.

Rules:

- Workers record from immutable descriptor snapshots.
- Descriptor-set handles referenced by reusable command buffers must remain
  stable until the frame slot retires.
- Per-frame uniform/storage data may be updated without command re-record only
  when descriptor-set handles and dynamic offsets stay compatible.
- Descriptor pool retirement must be frame-slot based and never happen before
  all command buffers that reference the pool have retired.
- Resource planner replacements invalidate all chains that reference replaced
  physical resources.
- Texture streaming changes should update descriptors in a controlled
  descriptor refresh phase, not from arbitrary render-recording code.

Preferred descriptor model:

- stable per-material/per-program descriptor layouts;
- per-frame buffer slices for dynamic values;
- bindless/material table path for high-churn texture/material selection;
- descriptor generations tracked per chain.

## Pipeline Rules

Pipeline creation must not be hidden inside hot parallel recording where it can
serialize the workers or stall the render thread.

Rules:

- pipeline variants should be prewarmed or created in a bounded preparation
  phase;
- if a required pipeline is missing, the chain is marked not-ready with visible
  diagnostics;
- workers may read pipeline handles from a thread-safe cache;
- workers should not compile/link shader variants during command recording.

## Command Buffer Cache

Each chain cache entry tracks:

- frame slot;
- chain key;
- structural signature;
- resource plan revision;
- descriptor generation;
- pipeline generation;
- command buffer handle;
- last-recorded frame ID;
- retirement fence/timeline value.

Reuse decision:

```text
reuse if:
  command buffer exists
  and structural signature matches
  and resource plan revision matches
  and descriptor generation is compatible
  and pipeline generation is compatible
  and profiler/query mode is compatible
  and frame slot is not still in use by GPU
```

Refresh decision:

```text
refresh frame data if:
  chain is StaticStructural or FrameDataOnly
  and descriptor handles are stable
  and dynamic buffer slices can be updated for this frame slot
```

Re-record decision:

```text
re-record if:
  structural signature changed
  or resource plan changed
  or descriptor handles changed
  or volatile chain type requires command changes
  or validation/profiler mode requires different commands
```

## Barrier Planning

Barrier planning must remain centralized. Workers should not invent global image
layout transitions.

The scheduler owns:

- pass input/output resource usage;
- attachment load/store transitions;
- swapchain layout transitions;
- FBO physical group layout tracking;
- inter-pass buffer barriers;
- queue ownership transfers.

Workers may emit local barriers only inside a chain when the barrier is fully
contained and declared by the schedule, for example a known intra-chain buffer
copy-to-draw transition. Anything cross-chain remains in the primary.

## Dynamic Overlay Strategy

Dynamic overlays are intentionally separated:

- UI text;
- ImGui/profiler;
- editor selection;
- gizmos;
- debug visualizers;
- live diagnostics.

These should record into small volatile command chains. They execute after the
main scene and before final presentation. They must not affect static scene
chain signatures.

This directly addresses the observed UI text issue where changing instance
counts forced expensive primary command-buffer re-recording.

## Multi-Queue Extension

Multi-queue support is a later layer, after the single graphics queue path is
correct and fast.

Potential candidates:

- async compute culling/compaction;
- async skinning/blendshape compute;
- shadow-map rendering on a second graphics queue when hardware benefits;
- transfer queue uploads for streaming resources.

Requirements:

- explicit timeline semaphores;
- queue-family ownership transitions where needed;
- frame graph dependencies promoted to queue dependencies;
- fallback to single-queue scheduling with identical visible behavior.

## Migration Plan

### Phase 0: Stabilize Instrumentation

- Keep command-buffer cache outcome counters.
- Add per-chain counters:
  - chains scheduled;
  - chains recorded;
  - chains reused;
  - chains frame-data-refreshed;
  - chains volatile-recorded;
  - chain record worker time.
- Add per-view counters:
  - visibility packet count;
  - render packet count;
  - secondary command-buffer count.
- Add validation logs for descriptor/resource generation mismatch.

### Phase 1: Introduce Packets Without Parallelism

- Add `VisibilityPacket`, `RenderPacket`, and `CommandChainSchedule`.
- Generate packets on the render thread from existing frame ops.
- Preserve current ordering exactly.
- Keep recording single-threaded.
- Validate that packet signatures match existing frame-op signatures.

### Phase 2: Static/Dynamic Split

- Classify packets by volatility.
- Move UI text, profiler overlay, gizmos, and other dynamic overlays into
  isolated dynamic chains.
- Remove dynamic instance counts and per-frame matrix values from static chain
  signatures.
- Refresh frame data without re-recording static chains.

### Phase 3: Secondary Graphics Chains

- Extend secondary command-buffer support to normal mesh draws inside active
  render passes/dynamic rendering.
- Add correct inheritance for:
  - legacy render pass;
  - dynamic rendering color/depth/stencil formats;
  - secondary-command-buffer contents flags.
- Record static scene pass chains into secondaries.
- Primary executes chain secondaries in ordered pass groups.

### Phase 4: Parallel Recording Workers

- Add a bounded recording worker pool.
- Add per-thread command pools and scratch arenas.
- Dispatch independent chain recordings after resource planning.
- Keep primary recording on render thread.
- Validate against single-thread recording with identical screenshots and pass
  logs.

### Phase 5: Parallel Visibility and Packet Build

- Move visibility collection for independent views to worker threads.
- Build render packets from visibility packets in parallel.
- Keep scene access read-only through immutable snapshots.
- Validate VR eye, shadow, and main view packet ordering.

### Phase 6: Primary Reuse

- Reuse primary command buffers when the pass-group schedule is stable.
- Keep stable per-frame-slot secondary handles so the primary can execute the
  same handles while workers re-record volatile secondaries.
- Make profiler/query mode changes explicitly dirty primary command buffers.

### Phase 7: Optional Multi-Queue Scheduling

- Promote selected independent chains to sidecar queue submissions.
- Start with async compute/transfer before second graphics queue rendering.
- Add timeline semaphore diagnostics.
- Keep single-queue path as the correctness baseline.

## Validation Plan

Build/tests:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- `Test-VulkanPhase3-Regression`
- targeted render graph ordering tests
- command-chain signature tests
- descriptor lifetime tests

Runtime validation:

- Unit Testing World, Vulkan, mono editor view.
- Unit Testing World, Vulkan, VR stereo mode when available.
- Directional shadow map scene with multiple cascades.
- Dynamic UI/profiler text stress scene.
- Texture streaming scene.
- Material/shader variant switching scene.

Image validation:

- capture screenshots before/after for:
  - main scene;
  - shadowed scene;
  - AO scene;
  - UI overlay;
  - VR eye views.
- compare pass outputs for:
  - GBuffer;
  - depth;
  - ambient occlusion;
  - lighting accumulation;
  - swapchain final.

Performance gates:

- settled scene: command-buffer record p50 below 2 ms in Release;
- settled scene: command-buffer record p95 below 5 ms in Release;
- dynamic UI text: static scene chains reused while text chain records;
- shadow views: shadow chain recording parallelized when multiple views exist;
- VR: eye view packet build/recording parallelized;
- no steady-state resource-plan replacements during camera movement;
- no steady-state descriptor pool retirement caused by reusable static chains.

Diagnostics:

- log first structural-dirty reason per chain;
- log first descriptor-generation mismatch per chain;
- log first resource-plan mismatch per chain;
- include chain reuse counters in profiler packets;
- show primary reuse versus secondary reuse separately.

## Risks

### Descriptor Lifetime Bugs

Reusable command buffers can reference descriptor sets after the owning pool is
retired. Mitigation: frame-slot retirement, descriptor generation checks, and
validation-mode assertions.

### Ordering Regressions

Parallel packet/chain generation may accidentally reorder transparent draws or
post-process dependencies. Mitigation: schedule-first architecture and image
comparisons against the single-thread path.

### Hidden Pipeline Creation

Workers could serialize on shader/pipeline creation and make parallel recording
look slower. Mitigation: prewarm/prepare phase and missing-pipeline diagnostics.

### Resource Planner Races

If resource planning can change while workers record, command buffers can
capture stale image/framebuffer handles. Mitigation: freeze resource plan before
recording and make plan changes invalidate chains for the next frame.

### Too-Fine Granularity

One secondary per tiny draw can increase overhead. Mitigation: chain groups are
bucketed by pass/material/mesh strategy, with minimum size heuristics.

### Debuggability

Parallel recording makes failures harder to inspect. Mitigation: stable chain
names, labels, per-chain dirty reasons, and a single-thread debug mode.

## Open Questions

- Should packet generation replace `FrameOp` entirely, or should `FrameOp` be a
  compatibility layer that lowers into packets during migration?
- Should static mesh chains use direct draws, indirect draws, or both depending
  on material strategy?
- How should transparent order be represented in `RenderPacket` without
  blocking opaque parallelism?
- What is the best cache key for editor UI/profiler overlays where text changes
  every frame but atlas/material state is stable?
- Should VR eye chains initially be separate, or should multiview be a first
  class packet mode from the start?
- Which resource planner revisions are truly structural for a chain, versus
  refreshable descriptor/resource changes?

## Acceptance Criteria

The refactor is successful when:

- Vulkan can record independent command chains on multiple worker threads.
- The render thread submits an ordered primary command buffer with minimal draw
  recording work.
- Static scene command chains are reused across camera movement.
- Dynamic overlay/text changes do not dirty static scene chains.
- VR eye and shadow view chains can be generated/recorded in parallel.
- The single-thread debug mode and the parallel mode produce equivalent images.
- The profiler clearly reports primary record time, secondary record time,
  worker time, cache reuse, and dirty reasons.
