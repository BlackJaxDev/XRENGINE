# Physics Chain Steady-State CPU Design

Date: 2026-10-08
Status: Partly implemented. The per-renderer bound version and the enlarged proxy bound are implemented; the [output and readback architecture](../../../architecture/physics/physics-chain-output-and-readback.md#canonical-gpu-bounds) owns them. The open code items are in the [todo](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md).

[Investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md) ·
[Validation](../../testing/physics/physics-validation.md#gpu-skinned-chain-scale) ·
[Distance cadence design](distance-cadence-gpu-presentation.md) ·
[Performance guide](../../../developer-guides/rendering/physics-chain-performance.md)

## Problem

The target is at least 100 completed rendered frames per second, with a frame-interval p95 of at most 10 ms, for 2,000 visible animated GPU physics chains. The target applies to two scenarios:

- **Shared mesh:** all chains use one skinned mesh, so the Advanced pipeline draws them as one indexed instance group.
- **Unique mesh:** each chain has its own mesh content, so no instance group can merge them.

The current result is 16–25 Hz. The frame is CPU-bound: the GPU is busy for about 8.8 ms of a 48 ms frame. With about 5 ms of baseline frame time, 2,000 chains get about 5 ms on the critical path, which is about **2.5 µs per chain**. The current cost is about **20 µs per chain**. The [investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md#root-cause-evidence) shows why: every loop in every frame phase visits every chain, and most of the work is in the object pattern (interface calls, locks, and dependent loads across scattered objects).

A dense row operation costs about 2 ns. An object-pattern operation costs 21–83 ns. Therefore 2.5 µs allows about 1,000 dense operations per chain, but only 30–120 object-pattern operations. Faster code generation cannot close this gap. Removing the per-chain work from the steady-state frame can.

## Target

An unchanged, fully covered GPU chain and its renderer cause no per-frame CPU work, except:

- one dense write of the chain's root affine record, and
- a fixed number of dispatches and draw commands for each bucket.

CPU work grows with the number of changes, not with the number of chains. A fully covered renderer gets its pose and bounds from the GPU, so it needs no per-frame CPU work until its source, material, mesh, or coverage changes.

## Design rules

1. **Dense rows on steady-state paths.** Steady-state loops read dense arrays. They do not call interfaces, take locks, or walk object graphs for each row.
2. **Local versions.** Each row or page has its own version, which increases only on its own real writes. A global counter must never act as a per-row generation.
3. **Skip unchanged pages; do not check data that always changes.** Use page versions to skip expensive per-row work. When a dense row changes in almost every frame, such as a moving root, do one full dense pass without a change check.
4. **Structural changes at sync points.** Registration, removal, coverage changes, and hierarchy changes apply at a defined boundary. Steady-state loops never see a half-applied structural change.
5. **Retained plans.** Build a draw, preparation, or dependency plan at registration. Rebuild it only when an exact dependency changes.
6. **Range reads.** Read many matrices under one sequence check, not one check and one memory barrier for each matrix.
7. **No hidden fallback.** A rejected fast path reports a counted reason and takes the existing strict path. It never substitutes a CPU readback or a stale result.

## Shared versioning contract

One shared page-version type serves all dense row stores: the chain world rows, the covered renderer rows, the Advanced publication rows, and the transform store.

- Rows are stored in fixed-size pages. Each page has a 64-bit version. A real write to any row in the page increases the version. A read path never increases it.
- A reader keeps the last version it read for each page. It skips each page whose version is unchanged. With no changes, a 2,000-row store costs one comparison for each page (6.6 ns for 32 pages in the microbenchmark).
- A new page starts at a version that no reader holds, so a reader treats it as changed. When a row is removed, the last row moves into the gap, and both affected pages increase their versions.
- 64-bit versions do not wrap in practice. This removes the periodic clamp scan that 32-bit tick schemes need.
- Writers and readers in different frame phases synchronize at the phase boundary. Readers that run concurrently with writers use page-local sequences, as in the transform store below.

Page size is a [decision](#open-decisions). With 1% scattered changes in 64-row pages, the work is about 60% of a full dense rewrite. Therefore rows that change together must share pages, for example all chains of one avatar.

`TransformHierarchyStore` is the existing dense store. Today one `_sequence` covers the whole store, each write takes `_gate` and increases the sequence twice, and each read uses a full memory barrier. A write to any of about 16,000 transforms therefore makes concurrent readers of every transform retry. The investigation recorded 1,041,463 contended render reads in one window. The store gets page-local sequences and a range read that copies many matrices under one sequence check.

## Physics steady state

| Current per-chain work | Design response |
| --- | --- |
| `GPUPhysicsChainDispatcher.ProcessDispatchesCore` calls `TryAcceptInput` and `AcceptGpuDrivenBoneBindings` for every active request, and `RefreshGpuDrivenBoneBindings` for every registered component, under the registration locks, in each render frame. | Request rows with page versions. The render thread visits only changed pages. |
| Rest dependency checks (opaque, owner, collider) run every tick, at about 4.4 µs per chain. They depend only on topology, ownership, and the collider list. | Versioned dependency sets: each range keeps the hierarchy topology version and the versions of the pages that hold its dependencies. It checks again only when one of them changes. |
| About 13 `IsActiveInHierarchy` calls per chain per tick. Each call recurses up to the scene root. | A dense active flag for each world slot, updated at the world boundary by activation and hierarchy changes. |
| Any collider disables the fast pack path (`PrepareGpuDispatchDataFromWorld`). Every benchmark chain has two colliders. | Collider data gets its own version. An unchanged collider set does not force the full particle pack. |
| Each mapped input page receives all colliders and the full transform catalog. | Each mapped bank records the versions it holds. Unchanged rows are not written again. |
| Root capture reads each root through the object layer. | One dense pass over a root-handle array, with range reads, into the mapped input bank. |
| Quality assignment and the activity scan visit every slot through the object layer. | One dense pass, skipped when the population, camera tier inputs, and settings are unchanged. |
| The CPU hierarchy still updates the 14,000 bones of covered chains. | Bones of fully covered chains are dormant on the CPU. They are materialized only for selective readback or loss of coverage. |

Lower simulation cadence alone does not solve the problem: the 15 Hz tier still spent about 7.3 ms in rest gathering per late tick, because the per-chain CPU work runs before the cadence decision. The [distance cadence design](distance-cadence-gpu-presentation.md) becomes a multiplier after this work.

## Change-driven renderer path

These items apply to both mesh scenarios. The first and third rows are implemented together: the committed bound is the proxy, and the proxy's version is the per-renderer version. A version alone does not help when every root moves, because the exact bound then changes on almost every output.

| Current per-renderer work | Design response |
| --- | --- |
| The committed-bounds generation is the global `ProducerEpoch`, which increases on every physics output. Every covered mesh takes the slow reconcile path in every frame, even when it did not move. | A per-renderer committed-bounds version that changes only when that renderer's bound changes. |
| `TryGetCommittedWorldBounds` runs about 9–12 times per chain per frame, with about 5 locks and 6 hash lookups for each call. | Resolve the committed bound once for each renderer in each frame and share the value. |
| Every covered mesh moves in the octree and the CPU BVH in every frame, and the BVH refits. | An enlarged proxy bound, as dynamic BVHs use. The spatial structures move a covered mesh only when its committed bound leaves the proxy. The margin trades culling precision against move frequency. |
| `RefreshGpuBoundsEligibility` rebuilds its list and set in each frame. `CanUseCanonicalGpuCollection` runs twice for each mesh. | A persistent GPU-culled draw set. A fully covered renderer enters at registration and leaves at removal or loss of coverage. |
| `CanCollectCoveredSourcesOnGpu` excludes shadow passes, so the primary directional shadow view runs `BeforeAdd` and `AddCPU`, with a lock and a sorted insert, for each covered mesh. | An explicit native GPU consumer for the primary shadow tile, with its own caster policy. |
| `TryBuildAndPreflightWholeScenePlan` plans every command in each publication. The reuse check runs after that plan. | Retained structural transaction plans and page-versioned command rows. The unchanged check runs before planning. |
| `AdvancedPreparationExtractor.ExtractCommand` does about 150–250 operations for each draw in each frame. Its `commandChanged` flag is a constant `false`. | Immutable preparation templates, refreshed only for changed rows. Pose, bounds, and frame resources stay fresh through GPU buffers. |
| Vulkan recording issues one indirect-count draw for each bin, but validates every visibility record for each view, bin, and phase. | Validate a sealed record only when it changes. |

Unreal's mesh drawing pipeline is the reference model: draw commands are cached when a primitive is added to the scene, invalidated only when a dependency changes, and fed per-frame data through persistent GPU buffers.

## Serial swap

`EngineTimer` runs the swap after collection and render both finish. Neither can run during the swap, which took 13–17 ms of a 43–47 ms cycle at 2,000 chains.

- Build the Advanced scene publication during collection, which overlaps render, and only commit it at the swap. A change that arrives after the build and before the swap invalidates the pending publication or applies as a delta.
- Skip unchanged render commands at the swap.
- `PublishRenderMatrices` and `DispatchNotifications` visit every transform row during collection and again during the swap. With dormant covered bones and page versions, they visit only changed pages.

## Measurement approach

Decide with the cost per chain of each frame phase, not with the frame rate alone. Window-to-window noise is 15–25%, so changes smaller than about 1 ms cannot be confirmed from frame rate.

1. Add cumulative collect, swap, and render-callback duration counters. Fit a fixed cost and a cost per chain over a chain-count ladder (1, 250, 500, 1,000, and 2,000).
2. Control the machine state: lock GPU clocks, use the High Performance power plan, and pin the editor to one CCD on the Ryzen 9 7950X3D.
3. Before the full implementation, measure two temporary ceilings: a frozen-scene rendering ceiling and a root-only physics ceiling. If both together do not approach 100 Hz, the design is not sufficient, and the next limit must be found first.

The [validation doc](../../testing/physics/physics-validation.md#bottleneck-measurements) owns these checks.

## Alternatives considered

| Alternative | Decision | Reason |
| --- | --- | --- |
| Broad ECS rewrite | Rejected | The ECS contracts (dense pages, change versions, sync points) apply to the existing stores. `TransformHierarchyStore` already uses dense storage. |
| Burst-style native compiler or source generator | Rejected | RyuJIT writes a dense 48-byte record in about 2 ns. The cost is the object layer, 10–45 times higher for each row. |
| Per-row caches inside full-scene walks | Not the main route | The registration lookup cache, source groups, and rigid rest-input cache each saved 1 ms or less. The ancestry cache and early admission were slower. Each still visits every row. |
| Event-driven dirty lists | Replaced by page versions | Every mutation path must send an event, and a missed event leaves stale data. The rigid rest-input cache needed a latch for this reason. |
| World-owned batch source for shared meshes | Deferred | The change-driven path is required for unique meshes anyway. Decide after measuring it with shared meshes. |
| Vulkan asynchronous compute | Deferred | The GPU is about 18% busy. It cannot help until the CPU frame meets its budget. |
| Lower simulation cadence alone | Not sufficient | The 15 Hz tier kept the per-chain CPU gathering cost. |

## Research background

The research looked at how data-oriented engines avoid per-object CPU work.

- **Unity Entities.** An entity is an ID, and components are plain structs. Entities with the same component set share 16 KiB chunks, with one packed array for each component type. When an entity leaves a chunk, the last entity moves into the gap. Structural changes go through command buffers and apply at sync points.
- **Unity change filters.** Each chunk stores a change version for each component type. A system records the version at which it last ran and skips chunks whose version is not newer. The filter works on whole chunks. A chunk counts as changed when a system with write access runs on it, even if no value changes, so systems declare read-only access when they do not write.
- **Bevy change ticks.** Each component of each entity stores 32-bit `added` and `changed` ticks. A `Changed<T>` query compares them with the system's last run, so it still visits each entity. A periodic scan clamps old ticks so that comparison stays correct after wraparound (`CHECK_TICK_THRESHOLD` = 518,400,000).
- **Unity Burst.** An LLVM-based compiler for a struct-only subset of C#. It does not support managed objects. It helps only after the data is already flat.
- **Unity BatchRendererGroup.** Meshes and materials are registered once, and batches share metadata. Each frame, a culling callback returns a job handle, and the jobs write draw commands grouped in draw ranges.
- **Unreal mesh drawing pipeline.** Retained mode: draws are prepared in advance, cached when a primitive is added to the scene, and invalidated only when a dependency changes. Per-frame data reaches cached commands through uniform buffers and GPUScene primitive data.

Sources:

- Unity Entities: [Archetypes and chunks](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/concepts-archetypes.html), [EntityQuery filters](https://docs.unity3d.com/Packages/com.unity.entities@1.4/manual/systems-entityquery-filters.html).
- Unity Burst: [Introduction to Burst](https://docs.unity3d.com/Manual/burst/introduction-to-burst.html).
- Unity BatchRendererGroup: [Overview](https://docs.unity3d.com/2022.3/Documentation/Manual/batch-renderer-group.html), [How BatchRendererGroup works](https://docs.unity3d.com/2022.3/Documentation/Manual/batch-renderer-group-how.html), [Initializing a BatchRendererGroup](https://docs.unity3d.com/2022.3/Documentation/Manual/batch-renderer-group-initializing.html).
- Unreal Engine: [Mesh drawing pipeline](https://dev.epicgames.com/documentation/en-us/unreal-engine/mesh-drawing-pipeline-in-unreal-engine).
- Bevy: [change detection source](https://doc.qu1x.dev/bevy_trackball/src/bevy_ecs/change_detection/mod.rs.html), [tick source](https://doc.qu1x.dev/bevy_trackball/src/bevy_ecs/change_detection/tick.rs.html). These pages mirror the `bevy_ecs` crate source.

## Open decisions

- Page size for each row store, and the enlargement margin for covered spatial proxies. Owner: physics runtime and rendering.
- Whether shared-mesh chains also need a world-owned batch source. Decide after the change-driven renderer path is measured. Owner: rendering.
- Whether concurrent readers of the chain world rows need page-local sequences, or whether phase-boundary synchronization is sufficient for every reader. Owner: physics runtime.
