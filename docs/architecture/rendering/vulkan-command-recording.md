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
and parallel secondary recording. Packet lowering is deterministic and
sequential. There is no parallel-packet-build compatibility switch; Vulkan
recording concurrency belongs to the persistent command-chain workers.

Hybrid is not an all-or-nothing mode. A single primary may execute cached
secondaries, execute newly recorded secondaries, and directly record ineligible
operations.

## Linked Binding Schema

Each successful Vulkan program link compiles shader reflection into one
versioned `VulkanProgramBindingSchema`. Its auto-uniform operations retain the
reflected member for diagnostics but publish typed source identity, frequency
owner, destination range, conversion policy, and explicit fallback reason.
Descriptor entries similarly publish set-tier ownership, array policy, and
separate topology/content dependencies.

The Vulkan shader rewrite emits physically separate std140 auto-uniform blocks
for every declared frame, view, pass, material, object, instance, or
runtime-callback frequency present in a stage. The blocks occupy unique
bindings in the stage's reserved binding window. Engine-known names are
classified automatically. A loose numeric declaration can override the
default material classification with a trailing annotation such as
`uniform float Radius; // XRENGINE_FREQUENCY(View)`. Unsupported annotation
names fail shader rewriting with the accepted domains instead of silently
selecting a fallback owner. Each physical block publishes a semantic layout
signature over its byte layout and typed write operations.
Material-owned immutable write plans are cached by the Vulkan material using
that signature plus the material and runtime-binding revisions, so compatible
program-local schemas reuse one plan instead of rebuilding it per renderer or
linked program. Non-material and legacy plans retain their program-local cache.
The renderer reserves bounded physical owner slots globally by publication
layout signature, frequency, and owner identity, and each slot keeps a
per-frame-slot publication ledger. Compatible programs therefore share the
same stable backing range without allowing incompatible block layouts to alias.
An object change therefore cannot invalidate bytes owned by another frequency.
A stable owner generation is skipped; a changed owner clears its precompiled
coalesced byte ranges and patches only its declared operations. Captured runtime
uniforms publish a content signature with the snapshot, so unchanged callback
output no longer republishes merely because a frame advanced.
Callbacks that opt into the fast path implement `IRenderBindingPublisher`.
Each publisher declares one frame/view/pass/material/object/instance/runtime
owner and a non-zero monotonic content generation, then emits typed numeric
uniform values inside a thread-private Vulkan binding capture. The immutable
snapshot records the owner and generation per uniform; auto-uniform planning
uses that declared owner instead of treating every callback override as
per-draw runtime data. Publisher identity, frequency, and generation are also
part of the frame-material snapshot key. Invalid domains, generation zero, or
a publisher that changes its contract while emitting fail visibly. Typed
publishers also reject sampler, storage-image, and buffer writes because those
resources do not yet have frequency-owned descriptor publication; existing
unrestricted numeric callbacks retain explicit mutable provenance. On
non-shadow material draws their captured values inherit the physical block
frequency and participate in that owner's content generation, so value changes
republish the affected payload without changing batch topology. Shadow
material substitution remains on the conservative path because it can replace
the material source after capture.
The GTAO gather pass uses this contract for its view-annotated settings block.
Its typed publisher advances from the `AmbientOcclusionSettings` monotonic
generation, including nested mode-setting changes, while camera matrices and
viewport values remain owned by their existing engine view/pass sources.
Each enqueued mesh draw also freezes the frame/view/pass/object/instance owner
generations and late camera/mesh scalar inputs used by typed writes. Publication
therefore consumes one immutable owner snapshot instead of hashing the full
matrix set for every block or rereading mutable camera and mesh-renderer state.

### Payload publication ownership and lifetime

Frequency-owned payload storage follows one explicit three-generation contract:

- the publication-layout signature selects a byte-compatible reservation and
  changes only when the linked schema/topology changes;
- the owner content generation decides whether that reservation's bytes must be
  published for the selected frame slot;
- the recording-visible arena generation decides whether a prepared command
  artifact may retain the reservation's stable offset.

`VulkanAutoUniformPublicationIdentity` carries those values with frequency and
owner identity. A content-only change republishes bytes without changing the
recorded binding location. A layout change selects a different reservation. An
arena-generation change invalidates every prepared handle that retained the old
storage. These generations must not be substituted for one another.

| Domain | Stable owner | Content that advances it | Publication lifetime |
| --- | --- | --- | --- |
| Frame | renderer frame domain | render-frame generation and typed frame publishers | once for each in-flight slot used by that frame |
| View | camera/view family | current and previous view/projection state, camera scalars, stereo mode, typed view publishers | once per active view owner and in-flight slot |
| Pass | compatible pass state | render area, viewport/scissor, shadow state, typed pass publishers | once per pass generation and in-flight slot |
| Material | material plus compatible runtime-layout owner | material value revision and material-frequency publisher values | once per dirty material and required in-flight slot |
| Object | mesh renderer | current and previous object transform plus object deformation/scalar state | one stable owner range; only changed object ranges are published |
| Instance | mesh renderer and instance/batch identity | instance/batch state and typed instance publishers | one stable owner range; only changed instance ranges are published |
| Runtime callback | immutable capture plus material | declared callback values and generation | once for each changed captured owner and required slot |

Each reservation owns one publication ledger per in-flight frame slot. A changed
owner places its precompiled byte ranges into a fixed-capacity
`VulkanAutoUniformDirtyRangeQueue`; the queue merges adjacent/overlapping ranges
and collapses to the complete owner payload if its 16-range budget is exceeded.
The consumer visits only that owner's bounded queue, never all live or visible
objects. Successful writes publish the content generation and empty the queue;
failed writes invalidate the ledger and use the visible conservative fallback.

The frame-data arena is persistently mapped and owns the same reservation offset
in every frame slot. Offsets are not recycled while command artifacts can retain
the arena generation. Renderer release removes object references but leaves old
offsets inert; resize adds a ledger for the new frame slot without moving the
reservation. Arena teardown retires all slots, clears reservations, and advances
the recording-visible generation before later publication can be accepted.

Temporal history is data, not topology. Previous view/projection matrices live
only in the view domain, and the previous model matrix lives only in the object
domain. Advancing either history therefore republishes its owner range without
rewriting frame, pass, material, instance, or callback payloads and without
invalidating a command artifact whose stable binding location remains valid.

Descriptor preparation keeps schema/layout identity separate from resource
content identity. Auto-uniform descriptor schema entries inherit their exact
physical frequency owner. Descriptor tables containing only dynamic UBOs plus
resource-fingerprinted image or texel bindings use one owner slot instead of a
per-draw allocation identity; fixed buffers, storage buffers, and descriptor
heap draws retain exact draw-slot ownership. Owner-shared keys include the
complete backing-resource fingerprint so buffers from different renderers
cannot alias.

Every descriptor allocation publishes independent topology and resource-content
generations plus the generation last published into each in-flight descriptor
slot. Owner lookup is keyed by stable program/material/view-family/schema and
owner identity rather than by the transient draw occurrence. Stable reuse is
therefore one exact owner-generation check and does not recompute the complete
resource fingerprint for each reusable draw. Material numeric parameters
advance `BindingValueVersion`; texture or other descriptor-resource changes
advance the separate `BindingResourceVersion`, so numeric-only changes cannot
invalidate resource descriptors.

Mutable frame-source descriptors are excluded from the stable resource
fingerprint and retain an exact resource signature for each descriptor slot.
The complete binding/resource fingerprint remains an exact slow-path validation
backstop: a matching backstop result republishes the current owner generation,
while a mismatch performs the precise content/topology invalidation. Per-binding
write signatures suppress unchanged native updates, and prepared draws freeze
descriptor-set handles and bounded dynamic offset slices before command
recording. Descriptor write assembly uses thread-local scratch spans directly,
so a changed publication no longer creates temporary buffer, image, texel-view,
or write arrays before the native update.

Descriptor growth is bounded relative to declared ownership, not draw
occurrences:

- one allocation variant exists for each live compatible
  program/material/view-family/schema/resource-owner key;
- one variant allocates at most the declared set tiers for each in-flight frame
  slot;
- compatible allocations share pool slabs in groups of 64, so pool growth is
  the ceiling of live allocations divided by 64 for each pool-size and
  update-after-bind signature;
- a slab is retired when its final live allocation is released; it is not kept
  as an unbounded idle cache.

The runtime publishes current and high-water allocation-variant, pool,
allocated-set, and reserved-set gauges beside unique visible materials, frame
slots, frame-data reservation count, mapped bytes, and reserved bytes. These
are the amplification inputs: a draw-count increase with fixed owners must not
increase descriptor variants or sets, while adding an owner may increase them
only by its declared tiers times the in-flight slot count. The frame-data arena
has an independent 32 MiB-per-slot limit, at most eight slots, and at most
131,072 live reservation keys; exceeding any limit fails visibly rather than
reusing stale storage.

The qualifying mesh fast path consumes those typed operations. Engine, temporal
view-projection, and mesh-state sources no longer rediscover their source by
walking the reflected member-name resolution chain. Material/default bytes are
compiled into the existing material write plan; only runtime-owned operations
remain in its dynamic patch list. Reflected struct trees compile as
material-owned snapshot operations and write their captured leaf fields
directly. A missing optional loose uniform with no authored default preserves
GLSL's zero-initialization contract in the rewritten UBO. Unsupported types,
invalid ranges, and invalid arrays reject the schema fast path and use the
visible legacy writer. Schema and runtime rejections publish exact typed
fallback counters. If a dynamic typed write fails, the renderer rewrites the
complete block through the legacy path; it does not retain a silently cleared
member.

The canonical `Invoke-VulkanPerf.ps1 -Preset Gate` runner enables the
steady-state binding-fallback gate. The measurement harness aggregates fallback
draws and all typed reason counters across the capture window, persists them in
the summary, and fails the cohort if any legacy auto-uniform draw occurred.

Set `XRE_VULKAN_AUTO_UNIFORM_PARITY=1` for validation runs that must serialize
each qualifying auto-uniform block through both paths. The validator compares
the complete packed bytes, reports the first mismatching schema entry,
frequency domain, byte offset, and values, then copies the authoritative legacy
bytes into mapped storage and invalidates the fast publication ledger. The
normal fast path never rents the validation scratch buffer; clean performance
captures explicitly disable this diagnostic flag.

Schemas are owned by the linked program generation and are discarded on
interface destruction or relink. Reflection remains authoritative and
available for validation messages without being interpreted for every
qualifying draw.

## Prepared Mesh Recording

Scheduled mesh-chain recording copies enqueue-time draw and context snapshots
into a reusable `VulkanPreparedFrameRecording`. The storage records its frame
slot and generation, preserves source order, and is frozen before any worker is
released. Command-chain workers receive only indexed reads into that frozen
array; they do not reread the mutable `MeshDrawOp` source array.

`VkPreparedMeshDraw` contains the snapshotted viewport/context and a
`VulkanPreparedMeshDrawState`. The render thread materializes graphics
pipelines, descriptor handles, vertex/index handles, push constants, frame-data
slot/generation, immutable uniform payload handles, and primitive commands
before dispatch. Each payload handle identifies its Vulkan buffer range,
descriptor location, producer, frame/draw slot, arena generation, frequency
mask, and per-owner content generations. The worker validates those frozen
identities before issuing a draw.

The same frozen storage also publishes one `VulkanPreparedCommandChain` for
each scheduled secondary in execution order. A chain record owns the exact
prepared-draw slice, resolved render-pass or dynamic-rendering inheritance,
recording dependency signature, writable artifact handle/generation lease, and
worker-eligibility decision. Both the serial fallback and persistent worker call
the same encoder with that record. The encoder rejects the job before resetting
the native buffer if the mutable lifecycle owner no longer matches the frozen
key, source range, dependency identity, artifact handle, or artifact generation.
Mutable `CommandChain` state remains only the lifecycle/result publication
channel.

The frame storage also takes a value copy of the complete ordered
`VulkanPrimaryCommandPlan` and its identity before worker dispatch.
Thread-local plan-builder reuse therefore cannot change the primary
orchestration view frozen beside prepared draw resources, command-chain
inheritance/dependencies, artifact leases, and worker eligibility.

Reusable-primary data refresh has its own producer/consumer boundary.
Reservation-manifest construction emits a bounded ordered array of immutable
`VulkanReusableFrameDataRefreshRequest` values containing the frozen planner
key, source range, operation context, and exact mesh draw slot or compute
snapshot. The consumer groups those prepared values without revisiting
`FrameOp`, allocating draw slots, or rebuilding planner identities. Desktop
retains the arrays in recorder-thread scratch. OpenXR copies each eye's array
into generation-checked persistent storage and gives the worker a read lease;
publication is rejected while that lease is active. Retired reference-bearing
entries are cleared when either retained store shrinks.

The producer also derives a deduplicated work list keyed by compatible physical
publication layout, block frequency, owner identity, and owner content
generation. The retained batch signature contains only structural cohort
identity; mutable owner-content generations are deliberately excluded. After
one full publication establishes the exact batch signature and conservative
fallback indices, a stable reusable primary refreshes only those frequency
owners and retained fallback requests; it does not visit every prepared draw.
Frame, view, pass, material, object, instance, and runtime-callback work all use
the same owner-generation contract. Legacy renderer, material, scoped, and
shadow callbacks are captured with explicit mutable provenance so their current
values can be published by the owning frequency without making the complete
batch structurally dirty. The zero-initialized unresolved-descriptor fallback
engine UBO does not disqualify owner-only publication, while any other
unclassified engine UBO remains a conservative fallback.

Profiler and MCP telemetry split prepared refresh visits into primary and
dynamic-UI cohorts. A stable static primary is required to remain at zero draw
visits; independently changing UI text may retain its compact dynamic-UI visit
without obscuring that acceptance result.

The worker calls the state-only prepared encoder; it does not enter mutable
`VkMeshRenderer.RecordDraw`, traverse `XRMaterial`, mutate program bindings, or
read a `ComputeDispatchSnapshot`. Conventional descriptor-set binds and dynamic
offsets are copied into pooled bind records, and multi-viewport/scissor arrays
are copied into frame-owned pooled storage. Descriptor image transitions for
this path consume the same prepared descriptor handles instead of prewarming
the mutable draw a second time.

The prepared encoder is static over `VulkanPreparedMeshDrawState`. Worker
assignment hashes the stable command-chain identity rather than pinning every
chain from one `VkMeshRenderer` to one worker. Independent prepared chains may
therefore use different worker-owned pools without invoking mutable
mesh-renderer preparation during encoding. Prepared encoding also installs no
resource-planner or pipeline scope. A recording guard rejects planner-scope
publication and verifies that the global planner identity/signature stamp is
unchanged across the encoder.

## Typed Primary Plan

Before native primary recording starts, the renderer compiles the ordered
`FrameOp` array into a reusable `VulkanPrimaryCommandPlan`. Each node has a
compact operation kind, original source index, typed orchestration-action mask,
draw classification, and the immutable operation payload used during the
migration. The action mask owns barrier-batch evaluation, explicit
queue-ownership transfer classification, begin-rendering, secondary-range
execution, inline-operation dispatch, end-rendering, final present preparation,
and external-image release. Queue-ownership actions are compiled from the
barrier planner's exact per-pass image, buffer, and swapchain ranges. The
recorder requires the typed action to agree with the number of ownership
transfers actually emitted. The operation nodes are followed by explicit
terminal nodes for render-scope closure and the presentation or
external-release work required by the output.
Every operation pass index is resolved before the plan identity is calculated.
Native recording consumes that published pass index directly, so an inherited
sentinel or late fallback cannot make the plan's barrier/queue action disagree
with the pass whose barriers are emitted.
The primary recorder consumes those actions and dispatches on the typed node
kind rather than repeating runtime type-pattern matching across its main loop.

The plan identity covers node order, pass, recording context, target identity,
the complete action mask, and the complete frame-operation semantic signature.
A separately maintained direct-recorder projection produces its own emitted
command signature. Tests and debug builds require that signature to match the
typed plan, then combine each projection with the same authoritative
`CommandRecordingDependencySignature` identity components and require the
complete command/dependency signatures to match before reuse decisions.

Prepared worker draws carry the Vulkan backend and an immutable diagnostic mesh
name directly. Workers retain the originating mesh renderer only as an opaque
owner identity for sealed frame-data lease validation; they do not dereference
it to discover backend or diagnostic state. The prepared-command encoding guard
also rejects planner scopes and verifies that global planner identity and
publication stamps are unchanged across encoding.

## Shared Command Identity

`VulkanCommandIdentityComponents` is the common identity vocabulary for
primary plans, dependency snapshots, and nested recorded artifacts. It keeps
ordered nodes, resource generations, render-scope inheritance, queue
assumptions, exact nested artifacts, primary-only state, secondary-only state,
and data content in separate hashes. This prevents a valid primary dependency
from being conflated with a secondary draw dependency.

The desktop primary cache retains both the combined group signature and these
components. A reuse miss reports the first differing component. The combined
signature remains the fast comparison, while the existing complete dependency
signature remains the correctness backstop.

The complete signature has separately classified fields for output attachment,
render area, view mask, queue family, dynamic-rendering inheritance, pipeline
and layout generations, mesh/index/vertex binding identities, buffer/image/view
allocations, sampler and descriptor layout/set identities, resource-plan and
external-target variants, frame slot, descriptor publication, data
publication, and volatile indirect/count content. Command topology and dynamic
state remain in the full frame-operation signature; mutable indirect/count
contents are data-only and do not manufacture structural invalidations.

## Recorded Artifacts And Worker Arenas

Every cached command-chain secondary is owned by one reusable
`VulkanRecordedCommandArtifact`. The artifact publishes the native buffer and
pool owner together with its command level, frame slot, artifact and recording
generations, frozen inheritance, command dependency identity, exact referenced
resource generations, pending counters, lifecycle state, and typed invalidation
reason. Recording transitions mutate this existing slot; they do not allocate a
replacement managed object.

A primary cache identity embeds a `VulkanRecordedCommandArtifactReference`
rather than only a raw secondary handle. The reference includes the exact
artifact generation and recording/resource identity, while native lifetime
tracking pins the corresponding secondary buffer and its resources. Reset,
replacement, invalidation, and retirement advance the artifact generation, so
a primary cannot retain a cache hit after the secondary it executes changes.
Before desktop or OpenXR primary reuse, group validation compares every chain's
current shared dependency signature with its executable artifact's recorded
signature. Structural or binding disagreement rejects the cached primary;
data-only publication changes remain compatible. The same validation runs
after recording and before primary publication, where disagreement is an
invariant failure because the primary has already executed those exact
secondary artifacts.

Copy-on-write replacement and destruction call
`VulkanRecordedCommandArtifact.CaptureRetirement`. Its immutable
`VulkanRecordedCommandArtifactRetirement` snapshot preserves the old buffer's
exact pool/arena owner, generations, dependency/resource identities, and
pending counts while the reusable slot is immediately assigned a replacement.
The deferred-release queue carries that snapshot instead of reconstructing
ownership from mutable chain state. Artifact transitions reuse their internal
resource set and allocate no managed memory after warmup.

Each persistent command-chain worker owns one
`VulkanWorkerSecondaryCommandArena`. The arena contains that worker's command
pool for every indexed frame slot and the reusable artifact slots backed by
those pools. Attaching and detaching artifacts is explicit, and teardown rejects
pool destruction while any cached artifact remains attached. Allocation,
recording, and teardown enter the arena through an allocation-free ownership
lease; a second thread or nested owner is rejected before it can concurrently
touch the Vulkan command pool.

Worker-pool recycling is cache-aware. A whole-pool reset is legal only when the
arena is not recording and the selected frame slot contains neither an
executable artifact nor an artifact retained by a queued submission or recorded
primary. The normal cached workload therefore uses clean reuse and individual
command-buffer reset for its dirty subset. It does not reset the pool and
discard unrelated reusable secondaries.

Reusable packet secondaries retain `SIMULTANEOUS_USE`. An exact recorded-primary
reference remains a lifetime pin after the secondary becomes dirty, so the
current lifecycle does not prove that a secondary can be pending through only
one execution. Removing the flag requires a different single-pending ownership
contract plus a measured benefit.

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

Worker assignment hashes the stable command-chain key. Separate prepared chains
originating from the same mesh renderer can therefore use different
worker-owned pools; the worker encoder consumes only their frozen prepared
states and never re-enters mesh preparation.

Before dispatch, the render thread prepares the batch's resource references and
lifetime tracking. A worker then:

1. selects its pool for the active frame slot;
2. reuses a clean secondary, or individually resets a dirty reusable buffer;
3. begins it with the required inheritance information;
4. encodes the prepared mesh draw commands;
5. ends the command buffer;
6. publishes success or a structured failure.

The render thread waits for the batch with a bounded timeout of two seconds.
Timeouts, exceptions, or invalid worker results prevent a partial batch from
being submitted. Failed workers are quarantined from immediate reuse so a
possibly inconsistent pool or job state cannot silently contaminate later
frames.

Profiler output exposes process-lifetime worker-secondary reset, allocation,
and replacement-allocation counters in addition to the frame-reset global
command-buffer counters. These distinguish arena recycling from unrelated
per-frame command buffers such as overlays.

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
depth/stencil formats, sample count, view mask, local-read attachment-location
and input-index mappings, and the rendering flags inherited by the secondary.
The primary adds `CONTENTS_SECONDARY_COMMAND_BUFFERS` only to its own
`VkRenderingInfo`; that primary-only flag is excluded from the secondary
inheritance identity.

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
- **Compute and transfer worker work:** direct compute dispatches and exact
  buffer copies may now use serial non-graphics secondaries, but they are not
  admitted to the mesh worker scheduler or described as asynchronous
  multi-queue execution.
- **Conflicting renderer ownership:** mutable renderer instances used by more
  than one chain are serialized.
- **Unsupported inheritance or dependency shapes:** uncertain work falls back to
  inline recording rather than silently weakening correctness.

Worker admission publishes `EVulkanCommandChainWorkerEligibility` for every
dirty chain. The typed result distinguishes insufficient work, renderer
conflicts, unsupported commands or inheritance, primary-owned indirect streams,
worker quarantine, and resource-preparation failure. The same result selects
the serial fallback, is retained on the chain, and is reported through profiler
and MCP telemetry. Permanent unsupported shapes are distinct from transient
capacity, ownership, and health failures.

CPU-produced indirect work may opt into a narrower serial-secondary contract.
The producer opens an allocation-free backend capability scope only after the
indirect and optional count buffers are bound, uploaded, and no longer have a
pending write. Every enqueued draw freezes the exact Vulkan buffer identities,
uploaded/allocated sizes, draw/count offsets, count, and stride. Recording
rechecks those values before resetting a secondary. A changed buffer, incomplete
producer, invalid or overflowing range, disabled command-chain mode, unsupported
inheritance, or failed draw-state preparation records a typed
`EVulkanIndirectSecondaryEligibility` reason and executes the draw through the
existing primary path.

The CPU-built diagnostic reference stream is the first caller allowed to make
that declaration. GPU-generated zero-readback streams deliberately do not enter
the scope: even if they reuse the same buffer object, their current-frame
producer and synchronization contract remains primary-owned. Indirect
secondaries are recorded serially for now. Reuse and worker-parallel recording
require their own complete prepared-state identity and measured benefit before
being enabled.

Direct compute dispatches, buffer copies, and query operations use a separate
typed `VulkanSecondaryRecordingContract`. The frame-operation scheduler buckets
only contiguous compatible `ComputeDispatchOp`, `BufferCopyOp`, or `QueryOp`
ranges. Before recording, the primary closes any active rendering scope and
emits the exact compiled pass barriers and queue-ownership transfers. Admission
then requires:

- a known barrier-plan pass;
- a graphics-family command pool and executing primary whose queue family
  supports the operation;
- nonzero compute workgroups and a prepared program/snapshot; or
- unchanged transfer buffer handles, transfer usage flags, valid
  allocated-byte ranges, and non-overlapping same-buffer copies; or
- an ordered query `CopyResults` after a matching begin/end, timestamp, or
  specialized-property producer, with valid destination/stride alignment.

The secondary inherits no render pass because these commands execute outside
rendering. `VulkanQuerySecondaryInheritanceContract` records whether the primary
has an active query, whether the device enabled `inheritedQueries`, and the
exact `occlusionQueryEnable`, query-control, and pipeline-statistics fields
placed in `VkCommandBufferInheritanceInfo`. XRENGINE currently admits only the
no-active-query contract, with occlusion inheritance disabled and query/statistic
flags empty. This is deliberate: an active primary query requires exact
query-type/control inheritance that is not yet represented by the frame op.

Query-pool resets remain in the primary preamble because Vulkan forbids them
inside rendering. Begin/end pairs, timestamps, and specialized property writes
also remain primary-owned because the query epoch is keyed to the command
buffer that prepared and records it. A begin/end pair and every enclosed draw
therefore stay in one primary command buffer. Unsupported query operations are
still bucketed for typed telemetry, reject with their exact primary-owned or
ordering reason, and fall through to the unchanged primary encoder.

Each secondary's tracked resource references and image-layout journal are
merged into the primary by `CmdExecuteCommandsTracked`. Any family-disable,
global secondary disable, active rendering scope, inherited-query mismatch,
queue mismatch, missing barrier plan, query-ordering gap, or invalid operation
state records an exact
`EVulkanSecondaryRecordingEligibility` reason and uses the original primary
encoder.

Compute, transfer, and query result-copy secondaries are independently
controlled at process startup with
`XRE_VULKAN_COMPUTE_SECONDARY_COMMAND_BUFFERS` and
`XRE_VULKAN_TRANSFER_SECONDARY_COMMAND_BUFFERS`, and
`XRE_VULKAN_QUERY_SECONDARY_COMMAND_BUFFERS` (boolean `0/1` or `false/true`).
All default to enabled. The profiler packet, profile-capture NDJSON, and MCP
profiler report a separate last result and per-reason counts for each family.
This mechanism is serial recording today; it does not claim worker overlap or
a measured performance benefit.

Frame telemetry also distinguishes native command-buffer reset, command-pool
reset, allocation calls, successfully allocated buffers,
`vkCmdExecuteCommands` calls, and the number of secondaries invoked. This makes
arena recycling and secondary-reuse changes measurable without inferring
lifecycle work from recording time.

The renderer contains multi-queue planning metadata, but the current desktop
submission path should be understood as a principally graphics-primary flow.
Metadata alone does not mean that a complete asynchronous compute/transfer
execution architecture is active.

## Command-Buffer-Local Image State

Image layout and access planning is recorded into a journal owned by each
command buffer. Journal entries are keyed by image generation, mip, array layer,
and individual color, depth, or stencil aspect. Each access state includes the
layout, stage and access masks, queue family, descriptor-visible layout,
resource generation, recording serial, and typed external ownership. The
ownership states distinguish engine-owned images, OpenXR runtime-acquired
images, and OpenXR images whose recorded work will return them to the runtime.
Recording a command buffer therefore does not publish speculative state to the
renderer's submitted-state model.

Before a queue submission, the renderer validates journal entry states in the
exact order of `SubmitInfo.PCommandBuffers`. The final state of one command
buffer becomes the comparison state for the next, independently of which CPU
worker completed first. Secondary journals are likewise merged into their
primary in execution order. A missing, stale-generation, queue-family, or other
conflicting entry state rejects the submission or marks the primary for rebuild;
the renderer never guesses an `oldLayout`.

Only a successful queue submission publishes the journals' final states.
Recording failure, validation rejection, or queue-submission failure leaves the
submitted-state model unchanged. Completion tracking then advances submitted
state to completed state using the accepted submission's queue-domain sequence.
Cross-family image barriers additionally publish an explicit immutable
ownership requirement containing the exact image range, old/new layouts,
source/destination queue families, stage/access scopes, and resource
generation. A release submission retains source ownership and a
release-pending record until a matching acquire journal is accepted. An acquire
on an incomplete producer must name the producer's timeline semaphore/value
and include the required destination wait stage; a completed fence/timeline
also satisfies the dependency. Unpaired, mismatched, unrelated-queue, or
unsynchronized acquires are rejected before `vkQueueSubmit`. Queue-completion
watermarks are snapshotted before the image-state lock so validation never
inverts the lifetime/image lock order.

An OpenXR runtime acquire is published before primary reuse is considered. The
primary's exact color subresource journal then records the release-pending state
before command-buffer recording ends, so a cached primary whose expected
ownership does not match the current acquire state is rejected. The
release-pending prediction becomes submitted state only when the queue accepts
the command buffer.

The current journal covers ordinary Vulkan subresource layout/access tracking,
cross-queue release/acquire and semaphore requirements, and the engine/OpenXR
acquire/release ownership lifecycle. OpenXR eye recording uses that
command-buffer-local contract to record both primaries concurrently. Ordered
submission publication, image-generation checks, and swapchain/session cleanup
prevent a failed or retired eye recording from becoming global submitted state.

## OpenXR Eye Recording Is A Separate Mechanism

The OpenXR path has a setting named `ParallelCommandBufferRecording` and
persistent workers for the two eye primaries. That mechanism is not the same as
desktop command-chain secondary recording.

The two-eye API contract treats the first request as left and the second as
right. Both preparation phases finish before either worker records. Regardless
of worker completion order, submission builds one graphics-queue `SubmitInfo`
whose command-buffer array is `[left, right]`. Mirror/publish batches append
their publish command buffer after both eyes, producing `[left, right, publish]`.
The one fence covers the whole ordered batch.

The image-state contract for each eye consists of:

- the acquired runtime color image at color aspect, mip 0, layer 0;
- the engine-owned per-eye depth image at each bit in `DepthAspect`, mip 0,
  layer 0;
- exact mip/layer/aspect ranges of render-graph attachments and imported
  texture uploads referenced by that eye's operations;
- exact descriptor-image entry requirements recorded by executed secondaries;
- a planner-owned foveation attachment when the selected foveation mode exposes
  one.

Color and depth targets are normally distinct per eye. Render-graph,
descriptor, upload, history, or foveation images can be shared; those are the
subresources whose journal entry/final states must chain from left to right.
Every actual range is represented by a `VulkanTrackedImageSubresource` rather
than inferred from the eye worker's completion order.

Each eye worker owns its primary command buffer and records without a
renderer-wide shared-state lock. Mutable upload preparation is thread-local,
while ordinary Vulkan image state and the OpenXR ownership lifecycle remain
command-buffer-local until ordered submission validation and publication.
Successful submission merges the journals in `[left, right]` order regardless
of worker completion order.

This distinction matters when reading telemetry:

- desktop parallel secondary recording distributes independent command chains;
- OpenXR parallel eye recording distributes eye jobs and records their native
  primary command buffers concurrently.

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
11. Concurrent OpenXR eye recording owns separate primary command buffers,
    thread-local mutable preparation, and command-buffer-local image journals;
    global state changes only during ordered accepted-submission publication.
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
| OpenXR persistent eye workers and native-overlap telemetry | `VulkanRenderer.OpenXR.EyeRecordWorkers.cs` |
