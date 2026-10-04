# Caller-thread physics-chain scheduling

Updated: 2026-10-03.

## Implemented boundary

The shared physics-chain implementation now respects browser and explicitly
configured caller-thread execution at its existing CPU scheduling boundaries:

- Component activation recalculates the authored root hierarchy through the
  existing immediate traversal before constructing particles. Native desktop
  activation retains its parallel hierarchy operation.
- World input preparation, prepared CPU batches and compatibility CPU solving
  execute their existing complete ranges inline. They do not construct parallel
  work items, queue thread-pool work or wait for completion in caller mode.
- The persistent CPU range scheduler creates zero workers and no completion
  event in browser/caller mode. It reuses the same handle storage, range executor,
  failure accounting and disposal state. Native desktop worker counts and
  scheduling remain intact.

This changes scheduling, not authored solver selection. `UseGPU`,
`UseBatchedDispatcher`, `Multithread`, quality, interpolation and mirror options
are preserved. GPU execution still follows the rendering bridge and exposes its
backend state; unavailable GPU execution does not select either CPU solver.
No shared transform API or simulation algorithm was changed.

## Production-method evidence

Both the narrow Core Release build and the ignored probe build pass with zero
warnings and errors. Builds use the existing `tools/build-narrow.sh` in the
shared validation run, including its serialized build lock. No tracked tests
were added or modified during live integration.

The ignored executable configures the real caller-thread `JobManager`, installs
a controlled 60 Hz timing service and drives production component activation and
the actual callbacks registered by `PhysicsChainWorld`. Its initial scheduling run passes
735 checks. The later notification-corrected run preserves those behaviors and
adds 75 allocation and mirrored-pose checks, for 810 total:

- 78 authored three-particle hierarchies activate with their correct world-space
  rest poses. The world retains exactly three shared tick callbacks.
- 72 data-oriented CPU chains cross the former 32-component/batch thresholds;
  four freeze-axis compatibility chains and one serial CPU chain retain their
  selected algorithms and produce motion. One explicitly GPU-selected chain
  remains GPU-selected with the default unavailable bridge and produces no CPU
  output or CPU motion.
- After 385 ticks, all data-oriented outputs have matching simulation-frame
  counters and valid palette, bounds and history. Preparation/solve/publication
  complete on the caller; simulation flags and pending update counts clear.
  Parallel work-item and parallel-range storage remain empty.
- A scheduler requested with four workers creates zero workers and no completion
  event. Its 97 handles execute exactly once in ascending coarse ranges, on the
  caller, over 14 batches. A failed range remains observable while later ranges
  still execute. Repeated disposal is safe and execution after disposal rejects.
- Component detachment retires all runtime handles, CPU instances and deferred
  arena registrations. Reactivation receives a new generation and can detach
  cleanly again.
- After caller-runtime shutdown, the same CPU scheduler creates two native
  desktop workers, processes every handle once, preserves deterministic inline
  execution and disposes successfully.

## Allocation source and correction

Before the notification correction, the complete 78-chain fixture with its
original CPU transform mirrors enabled allocated **5,361,904 bytes over 256
warmed ticks**. All measured allocation occurred during late update. Disabling
only the 72 data-oriented mirrors reduced that measurement to 348,160 bytes;
removing the compatibility/serial cohorts then yielded zero. These diagnostic
variations did not change production mirror defaults.

A focused production-property measurement attributed **69,632 bytes over 512
root Rotation assignments** to the owning SceneNode's two ordinary property-event
subscriptions. Removing only those subscriptions from the disposable fixture
made those same mutations allocate zero bytes. Each mutation allocated changing
and changed event arguments even though SceneNode only consumes Parent and World.
Physics-chain preparation restores the root rotation, and transform publication
later writes the solved rotation, so this otherwise harmless observer caused
recurring simulation allocations.

XRBase now lets an observer identify one of its own callbacks as a guaranteed
no-op for a particular property. The query must be pure, thread-safe,
nonthrowing, allocation-free and independent of mutable subscriber state.
The publisher captures its event once and scans that immutable snapshot with
the allocation-free delegate enumerator. It omits argument creation only when
every callback explicitly opts out. Any ordinary, unknown or interested callback
restores the original complete multicast invocation and one fresh argument
object. Null/empty property names and failed optional queries also retain normal
dispatch. Argument pooling and individual callback dispatch are not used.

Sealed SceneNode opts out only for its exact existing transform callbacks:
changing still consumes Parent, and changed still consumes Parent and World.
Its existing subscriptions and cooked relinking remain intact. The publisher's
virtual notification overrides still run, including transform dirtiness and
world-object replication/tick behavior. No event fields, authored properties,
serialized instance state, or simulation algorithm changed.

The fresh managed production run passes **810 checks** with zero build warnings
or errors. All CPU mirrors remain enabled during the complete 78-chain run;
positions and output-frame counters advance, and teardown and native worker
checks remain intact. The measured result is now **zero caller-thread managed
bytes over 256 warmed full ticks**, with fixed, normal and late groups each at
zero. The 512 root Rotation mutations also allocate zero bytes with SceneNode's
normal subscriptions retained. The range scheduler still allocates zero over
2,048 warmed schedules.

Independent source review found no correctness defect in the gate. A separate production-method witness also passes multicast order and shared
arguments, duplicate/subsequence removal, static/wrapped callback fallback,
null/empty names, veto restoration, original exception boundaries, nested
subscription snapshots and subscriber collection. Actual parent/world activation
and matrix dirtiness remain correct. The pre-change cooked and YAML fixtures load
and reserialize byte-for-byte, and repeated cooked relinking restores exactly one
subscription pair. A bounded browser-wasm build also runs on the actual .NET 10.0.12 interpreter
hosted by Node, with OperatingSystem.IsBrowser returning true. Each SceneNode
callback's Delegate.Method lookup allocates zero bytes over 20,000 warmed reads.
Its normal subscribed Transform.Rotation likewise allocates zero bytes over
20,000 warmed alternating mutations. An ordinary-listener control allocates
960,000 bytes and receives all 22,000 callbacks including warmup, confirming that
the counter and fallback dispatch are active. Parent/World and unparent behavior
also pass. The pinned SDK build reports zero warnings/errors.

These native and Node-hosted interpreter measurements do not establish DOM,
graphics, real-browser timing or whole-browser simulation, visibility, recording
and submission acceptance. The current browser target is untrimmed and interpreted;
future AOT/trimming must separately retain or qualify the delegate metadata. A
metadata-query failure preserves dispatch but can lose the allocation benefit.

## Deterministic world ownership

`PhysicsChainWorld` now subscribes to the existing `RuntimeWorld.Disposing`
boundary. Teardown closes command admission before waiting for the current tick
and removes registry ownership once quiescent. It never holds the registry lock
while waiting for the tick gate. An internal hook before `RuntimeWorld.EndPlay`
waits for active chain work, or defers the whole world disposal when requested by
its current tick or worker. The existing disposal event remains after end-play
and before unload.

Callback registration publishes ownership before invoking the host, outside the
registry lock. Reentrant disposal and partial-registration failures roll back
the callbacks. Cleanup continues across independent owned resources after a host
unregister or readback-source disposal failure, then reports the first failure.
The world disposal exit path also releases chain and tick ownership when an
earlier end-play or disposal subscriber throws before the chain's event handler.
The original world failure remains observable even if readback cleanup also
fails. Chain and tick admission stay closed; after removing the failing
subscriber, retrying `Dispose` completes the remaining scene/capability cleanup.

Cleanup unregisters the three callbacks, invalidates live and pending chains,
detaches CPU backend records, frees live and retired arena registrations, clears
queued/component storage and per-world caches, and disposes owned batch
completion events. A chain already attached to another world keeps its new
owner. `RuntimeWorld` releases its registered and pending tick-queue ownership
after scene unload, so final removals do not require another tick. Late chain
registration and captured callbacks cannot reopen the disposed owner.

Readback operations share the lifetime gate. Cleanup cancels the owned staging
leases and disposes their fences exactly once without copying, polling, or
waiting for GPU completion. Renderer-owned backing buffers remain renderer
resources; the Core staging source releases only its lease. Public world
readback operations reject a disposed scheduler. Calls from a chain batch worker
are rejected before acquiring the lifetime gate, avoiding a wait on its own tick.

The active registry uses weak owner keys. Contexts without `RuntimeWorld`'s
disposal event must explicitly call `PhysicsChainWorld.Release(context)` to
release owned resources. Weak keys prevent the registry itself from retaining
an abandoned context; they do not replace deterministic cleanup. Released owner
identities remain terminal without being strongly retained. Reusing authored
chains requires a new world context after disposal; ordinary stop/start within
a live context retains its existing behavior.

A separate ignored production-world probe passes 180 checks across three 72-chain desktop
activation, stop/start, and disposal cycles. It checks pending add/remove and
dynamic commands, arena and CPU records, all owned batch events per cycle,
pending/in-flight readback cleanup, stale callbacks, terminal admission,
cross-world graph reuse, and disposal waiting for the tick gate. Weak-reference
collection is checked only after explicit graph destruction with normal engine
object-cache registration enabled. Separate fixtures exercise explicit context
release, reentrant or throwing callback registration, disposal requested by the
running tick and by a native batch worker, immediate worker readback rejection,
and cleanup after unregister or readback disposal failures. End-play is observed
only after the requesting worker callback returns. Additional cases cover earlier
disposal/end-play subscriber failures, compound readback failures, and a successful
disposal retry without reopening callbacks. At the lifetime-correction checkpoint, the original caller-thread probe
also passed all 735 checks with its historical allocation and desktop worker
results unchanged. The later 810-check notification run above records the new
allocation result separately. Narrow probe builds report zero warnings and errors. After the notification
change, the same disposal executable passes again with eight native batches per
cycle and 215 assertions; the earlier three-batch profile passed 180. The count
difference comes from checking every owned completion event at the available
processor topology, not from newly added scenarios.

These are native .NET production-method checks under explicit caller-thread and
desktop execution, not browser/WASM execution, rendered GPU evidence or desktop
pacing acceptance. Readback disposal uses controlled source/fence objects;
backend GPU teardown remains subject to its renderer lifetime contract. The
broader blocking-site inventory remains open.

Disposable reproduction and logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/`:

- `scratch/physics-chain-caller-probe/PhysicsChainCallerProbe.csproj`
- `scratch/physics-chain-caller-probe/Program.cs`
- `logs/physics-chain-caller-core-build.log`
- `logs/physics-chain-caller-probe-build.log`
- `logs/physics-chain-caller-probe.log`
- `scratch/physics-chain-disposal-probe/PhysicsChainDisposalProbe.csproj`
- `scratch/physics-chain-disposal-probe/Program.cs`
- `logs/physics-chain-disposal-probe-build.log`
- `logs/physics-chain-disposal-probe.log`
- `logs/physics-chain-caller-probe-disposal-build.log`
- `logs/physics-chain-caller-probe-disposal.log`

The allocation correction additionally uses:

- `scratch/physics-chain-notification-probe/PhysicsChainNotificationProbe.csproj`
- `logs/physics-chain-notification-probe-build.log`
- `logs/physics-chain-notification-probe.log`
- `scratch/property-notification-probe/baseline.bin` and `baseline.yaml` (captured before the source change)

- `logs/property-notification-build.log`
- `logs/property-notification-run.log`
- `logs/physics-chain-disposal-notification-build.log`
- `logs/physics-chain-disposal-notification.log`

- `scratch/property-notification-wasm/`
- `logs/property-notification-wasm-build.log`
- `logs/property-notification-wasm-run.log`
