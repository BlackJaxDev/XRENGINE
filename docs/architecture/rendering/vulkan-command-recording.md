# Vulkan Primary And Secondary Command Recording

Last updated: 2026-07-30

This document explains how XRENGINE turns a frame's rendering work into Vulkan
command buffers. It covers the desktop path, the persistent secondary-recording
workers, primary-command-buffer reuse, resource lifetime rules, and the
differences in the OpenXR path.

For the exact primary-reuse correctness contract, see
[Vulkan Primary Command-Buffer Reuse](vulkan-primary-command-buffer-reuse.md).
For the surrounding frame lifecycle, see
[Vulkan Renderer](vulkan-renderer.md).

## The Basic Idea

A Vulkan command buffer is a recorded list of instructions for the GPU. Recording
does not execute those instructions. The CPU first records them, then submits the
finished command buffers to a Vulkan queue, and only then can the GPU execute
them.

Vulkan has two command-buffer levels:

- A **primary command buffer** is the top-level program submitted to a queue. It
  owns frame-wide ordering, render scopes, barriers, and execution of secondary
  command buffers.
- A **secondary command buffer** is a reusable subprogram. It cannot be submitted
  directly. A primary command buffer runs it with `vkCmdExecuteCommands`.

The engine uses that distinction to keep orchestration deterministic while
moving suitable, independent draw recording onto persistent worker threads.

```text
Render thread
  FrameOps
    -> prepare resources and immutable draw inputs
    -> lower work into packets and command chains
    -> dispatch dirty independent chains to workers
    -> merge worker results in original schedule order
    -> record or reuse the primary command buffer
    -> submit the primary to the Vulkan queue

Worker threads
  dirty command chain
    -> record one secondary command buffer
    -> publish completion or failure
```

## Terminology

| Term | Meaning |
| --- | --- |
| `FrameOp` | A high-level operation collected for the current frame, such as a mesh draw, query, or transfer. |
| Render packet | Prepared recording work derived from one or more frame operations. |
| Command chain | A stable, cacheable unit of compatible work that may be recorded into a secondary command buffer. |
| Render-pass chain group | Consecutive chains that share a compatible render target and render-scope context. |
| Command-chain schedule | The ordered description of which chains may run on workers and where their results belong. |
| Primary command buffer | The top-level command stream that owns global ordering and is submitted to a queue. |
| Secondary command buffer | A command stream executed from a primary, normally containing a compatible group of draws. |
| Dirty chain | A chain whose recorded secondary no longer matches its commands, resources, inheritance, or frame-local inputs. |
| Frame slot | One entry in the frames-in-flight ring. Pools and cached recordings are tracked per slot so in-flight GPU work is not reset. |

## Recording Modes

The renderer resolves the configured command-recording mode before building the
frame:

| Mode | Behavior |
| --- | --- |
| `Inline` | Record work directly into the primary command buffer. |
| `Hybrid` | Record eligible command chains into secondaries and keep global or unsupported work inline in the primary. |
| `Auto` | Let renderer capabilities and policy select the effective mode. |

The principal settings are exposed through the renderer configuration and
environment-variable overrides for command chains, parallel chain recording,
and parallel secondary recording. The historical
`XRE_VULKAN_PARALLEL_PACKET_BUILD` switch is still recognized, but current
packet lowering is sequential; it must not be interpreted as active parallel
packet construction.

Hybrid is not an all-or-nothing mode. A single primary may execute cached
secondaries, execute newly recorded secondaries, and directly record ineligible
operations.

## Desktop Frame Flow

### 1. Wait for a safe frame slot

Before resetting or reusing per-frame resources, the renderer waits until the
selected frame slot is no longer in use by the GPU. This protects command pools,
command buffers, descriptor state, and other frame-local resources from being
mutated while an earlier submission still references them.

### 2. Drain and sort frame operations

The render thread drains the queued `FrameOp` instances and sorts them into the
required render order. This order is authoritative. Parallel recording may
change where CPU work happens, but it must not change the order in which the
primary observes that work.

### 3. Prepare resources on the render thread

Resource creation, descriptor mutation, pipeline lookup, dynamic-state
resolution, indirect-buffer setup, and other mutable renderer work are prepared
before worker recording. Workers should consume stable prepared data, not race
the render thread while it changes renderer objects.

This phase is part of the thread-safety boundary: the engine parallelizes
encoding of known commands, not arbitrary mutation of Vulkan resources.

### 4. Lower work into packets and command chains

The renderer converts sorted operations into packet data and then into stable
command chains. A chain groups work that has compatible render context,
inheritance, and execution constraints.

The scheduled worker path currently targets normal, non-overlay, non-UI
`MeshDrawOp` work whose dependencies can be represented as frame-data-only
recording inputs. Compatible runs are grouped into useful chain sizes, typically
between 10 and 64 draws. Smaller or incompatible runs can remain as finer
packets or inline work.

Stable chain identity matters. It lets the engine compare the current chain
against the secondary cached for the same frame slot rather than recording
everything again.

### 5. Decide whether each secondary is reusable

For each chain and frame slot, the renderer compares the desired recording
against the cached secondary. Re-recording is required when any recording
dependency changes, including:

- the command sequence or chain identity;
- render-pass or dynamic-rendering inheritance;
- framebuffer, attachment formats, sample count, or view mask;
- pipeline or descriptor dependencies;
- vertex, index, indirect, or count buffer bindings;
- dynamic state baked into the command stream;
- a frame-slot-local resource generation;
- invalidation, retirement, or a prior recording failure.

Frame-varying data does not automatically require command re-recording. If a
recorded command still points at the same buffer range or descriptor and only
the contents are refreshed safely, the secondary can remain valid.

### 6. Record dirty chains on persistent workers

The command-chain worker domain is persistent; it is not recreated every frame.
The renderer chooses a bounded worker count based on available CPU concurrency,
with a current upper bound of eight and one logical processor reserved from the
simple `CPU count - 1` calculation.

Parallel dispatch is used only when there are at least two independent dirty
chains worth recording. Otherwise the coordination overhead would exceed the
benefit and the work stays serial.

Each worker owns command-pool state for each frame slot. Vulkan externally
synchronizes command-pool use, so a pool must never be reset or recorded from
multiple threads at the same time. Worker ownership makes that rule explicit.

The current implementation also pins a mutable mesh renderer to one worker for a
batch. If two candidate chains would cause conflicting access to the same
renderer-owned state, the conflict is kept on the serial path.

Before dispatch, the render thread prepares the batch's resource references and
lifetime tracking. A worker then:

1. selects its pool for the active frame slot;
2. allocates or resets the chain's secondary command buffer;
3. begins it with the required inheritance information;
4. encodes the prepared mesh draw commands;
5. ends the command buffer;
6. publishes success or a structured failure.

The render thread waits for the batch with a bounded timeout of two seconds.
Timeouts, exceptions, or invalid worker results prevent a partial batch from
being submitted. Failed workers are quarantined from immediate reuse so a
possibly inconsistent pool or job state cannot silently contaminate later
frames.

### 7. Merge results deterministically

Workers may finish in any order. Their completion order is irrelevant.

The render thread merges results using schedule positions established before
dispatch. Therefore, if chains were scheduled as A, B, and C, the primary
executes A, B, and C even when the workers complete C, A, and B.

This is the central correctness rule of parallel recording:

> CPU recording may be parallel; GPU-visible execution order remains the
> original render order.

### 8. Match secondary inheritance exactly

A secondary recorded for one rendering context cannot be executed in an
incompatible context.

For a legacy render pass, inheritance includes the compatible render pass,
subpass, and framebuffer information.

For dynamic rendering, inheritance includes the color attachment formats,
depth/stencil formats, sample count, view mask, and relevant rendering flags.

These values are part of the cache identity. A change invalidates reuse even
when the draw calls themselves appear unchanged.

### 9. Record the primary command buffer

The primary is the frame's conductor. In order, it records the required:

- image and buffer barriers;
- render-pass or dynamic-rendering begin/end commands;
- viewport, scissor, and other primary-owned state;
- execution of scheduled secondary command buffers;
- inline commands for work that is not secondary-eligible;
- final layout transitions and presentation preparation.

The primary does not simply execute one secondary per frame. It may alternate
between render-scope setup, several secondary executions, inline operations, and
additional transitions.

### 10. Reuse the primary when its full dependency signature matches

The desktop renderer can reuse a previously recorded primary for a frame slot
when the complete command stream is still valid. The signature covers more than
the visible draw list: it includes ordered operations, render targets, render
scopes, barriers, queue-family assumptions, dynamic state, and the exact
secondary command buffers the primary executes.

If the signature matches and the command buffer is not invalidated or in
flight, the engine skips primary recording. Otherwise it resets and re-records
the primary. The exact rules are documented in
[Vulkan Primary Command-Buffer Reuse](vulkan-primary-command-buffer-reuse.md).

### 11. Submit only complete work

After recording and validation, the renderer submits the primary with its wait
and signal semaphores and fence/timeline state. Secondary command buffers are
not submitted separately; their commands execute because the submitted primary
references them.

## Command-Buffer Lifetime And Retirement

Recorded command buffers retain references to every Vulkan object they encode.
A pipeline, descriptor set, image view, framebuffer, or buffer cannot be
destroyed merely because CPU code no longer wants it. It may still be named by:

- a cached primary;
- a cached secondary;
- an in-flight submission;
- a worker result waiting to be merged.

The renderer therefore tracks resources referenced while recording and merges
secondary references into the primary's tracked dependency set. Resets and
retirement are guarded by frame-slot completion. Replaced objects and command
buffers are retired through deferred destruction rather than destroyed
immediately.

This is why cache invalidation and lifetime tracking are one problem, not two:
an incorrect cache hit can execute stale commands, while premature retirement
can make an otherwise correct command buffer reference a dead Vulkan object.

## Work That Intentionally Stays Out Of Worker Secondaries

Some operations remain primary-owned or serial because their correctness model
is different from an ordinary prepared mesh draw:

- **Queries and timestamps:** ordering and reset/begin/end placement are tied to
  the top-level command stream.
- **Zero-readback indirect/count updates:** mutable command-stream preparation
  remains primary-owned so workers do not race producer state.
- **UI and overlay work:** it has specialized render ordering and context.
- **Compute and transfer work:** current command-chain worker eligibility is
  mesh-focused; these operations require explicit synchronization contracts
  before being widened.
- **Conflicting renderer ownership:** mutable renderer instances used by more
  than one chain are serialized.
- **Unsupported inheritance or dependency shapes:** uncertain work falls back to
  inline recording rather than silently weakening correctness.

The renderer contains multi-queue planning metadata, but the current desktop
submission path should be understood as a principally graphics-primary flow.
Metadata alone does not mean that a complete asynchronous compute/transfer
execution architecture is active.

## OpenXR Eye Recording Is A Separate Mechanism

The OpenXR path has a setting named `ParallelCommandBufferRecording` and
persistent workers for the two eye primaries. That mechanism is not the same as
desktop command-chain secondary recording.

Each eye worker can be assigned its own primary command buffer, but the actual
native recording section currently enters
`ParallelEyePrimaryRecordSharedStateLock`. Consequently, eye jobs may be
scheduled concurrently while the Vulkan recording section is serialized. The
lock protects renderer-global image/layout tracking that is not yet
command-buffer-local.

This distinction matters when reading telemetry:

- desktop parallel secondary recording distributes independent command chains;
- OpenXR parallel eye recording distributes eye jobs, but currently retains a
  shared-state serialization point.

## Diagnostics And Telemetry

Useful diagnostics should answer:

- Which recording mode was resolved for the frame?
- How many chains were reusable, dirty, serial, or worker-recorded?
- Why was a chain ineligible or invalidated?
- How long did preparation, worker recording, merge, and primary recording take?
- Was the primary reused?
- Did any worker time out, fail, or enter quarantine?
- Which fallback path was selected?

Counters must describe actual executed behavior. A feature flag or scheduled
job is not evidence of parallel native recording if a shared lock serializes the
critical section.

## Current Correctness Invariants

The architecture depends on these invariants:

1. A submitted command buffer is never reset while it is in flight.
2. A Vulkan command pool is externally synchronized and has one recording owner
   at a time.
3. Worker input is fully prepared before dispatch.
4. Parallel completion order never changes scheduled execution order.
5. Secondary inheritance exactly matches the primary render scope.
6. Cache reuse requires a complete recording-dependency match.
7. Primary lifetime tracking includes resources referenced by executed
   secondaries.
8. Partial or failed worker batches are never submitted as successful frames.
9. Unsupported or conflicting work has an explicit serial/inline path.
10. Frame-data refresh is distinguished from command-structure invalidation.
11. OpenXR shared image state remains protected until it becomes
    command-buffer-local.
12. Telemetry reports the path that actually executed.

## Future Optimization Work

Proposed changes, their Vulkan specification review, implementation phases, and
acceptance gates live in the
[Vulkan Command Recording Architecture Optimization TODO](../../work/todo/rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md).

## Source Map

The implementation is split across focused partial-class files:

| Responsibility | Primary implementation |
| --- | --- |
| Primary recording, reuse decision, and scheduled secondary execution | `VulkanRenderer.CommandBufferRecording.cs` |
| Packet and command-chain construction | `VulkanRenderer.CommandChainLowering.cs` |
| Persistent worker ownership, dispatch, wait, and quarantine | `VulkanRenderer.CommandChainWorkers.cs` |
| Secondary allocation and encoding | `VulkanRenderer.SecondaryCommandBuffers.cs` |
| Primary command-buffer variants and allocation | `VulkanRenderer.CommandBufferAllocation.cs` |
| Reset guards, referenced-resource tracking, and deferred lifetime | `VulkanRenderer.ResourceLifetimeTracking.cs` |
| Desktop queue submission | `VulkanRenderer.FrameLoop.Submission.cs` |
| OpenXR eye scheduling | `VulkanRenderer.OpenXR.EyeRendering.cs` |
| OpenXR persistent eye workers and shared recording lock | `VulkanRenderer.OpenXR.EyeRecordWorkers.cs` |
