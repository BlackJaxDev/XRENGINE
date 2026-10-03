# S14: the actual Core update owner

Status: Deferred for tick optimization; the one predeclared change, removing
the tick dispatch's own heap allocation, is Validated (September 27, 2026).
On the S13a fixture the world tick path costs 24 to 26 us per update when
warmed, no registration is applied outside transitions, and no callback
averages more than 8.5 us, so none of the three predeclared entry thresholds
was reached and no tick optimization is justified. The dispatch itself
allocated 56 to 88 bytes per tick group on every update (about 19 KB per
second); it now allocates nothing, with order, membership and play semantics
unchanged. Play-mode validation exposed three pre-existing defects outside the
tick path, reproduced on the unchanged build and handed off: an intermittent
play exit that leaves the world frozen, a second play round trip that loses
the scene, and a light-probe spawner that retries forever after a snapshot
restore. The profiler's Component Timings panel has had no producer since the
modularization refactor.

Gate record for
[S14](../../todo/rendering/vulkan-stall-remediation-todo.md#s14-address-the-actual-core-update-owner)
under the todo document's one-by-one protocol. Evidence root:
`Build/_AgentValidation/20260927-095558-s14-core-update/` (ignored, disposable;
findings are copied here). Prior validated item: S13i, with no reordering.

## Entry evidence

- The historical 92.077 ms "update endpoint" came from a hottest-path report
  that pairs a descendant scope with its root's duration. It never measured
  the exclusive cost of the update callbacks, so it is not evidence of a slow
  update.
- The S13i captures on the S13a fixture recorded the duration of every whole
  update iteration (pre-update, update and post-update events together) in
  the per-frame stream. For the S13e increment and the cumulative binary:

| Phase of the run | Frames | Update p50 (ms) | p95 | p99 | Max | Frames at or above 1 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| First 30 seconds (scene load) | 1,212 to 1,822 | 0.047 to 0.059 | 0.150 to 0.178 | 0.216 to 0.311 | 3.10 to 4.99 | 2 to 6 |
| Rest of the 120-second warmup | 7,218 to 10,310 | 0.034 to 0.052 | 0.056 to 0.093 | 0.123 to 0.152 | 2.99 to 5.41 | 16 to 20 |
| Stationary window, 60 seconds | 4,836 to 6,962 | 0.034 to 0.053 | 0.058 to 0.088 | 0.116 to 0.147 | 2.89 to 3.06 | 7 to 13 |
| Motion window, 60 seconds | 1,623 to 1,917 | 0.038 to 0.061 | 0.085 to 0.119 | 0.139 to 0.173 | 2.77 to 2.96 | 2 to 5 |

  The whole iteration is two orders of magnitude below the render dispatch
  time. About once every five seconds an iteration takes 3 ms; its owner is
  not known.
- These numbers bound the tick path from above but do not show it: they do
  not say whether the world was in the playing state (the tick groups run
  only then), how many callbacks are registered, what the pending
  registration drain costs, or which callback owns the 3 ms iterations.

## Code reading (actual path)

- The actual path is `RuntimeWorld.Update` (Normal then Late tick group) and
  `RuntimeWorld.FixedUpdate` (PrePhysics, physics step, DuringPhysics,
  PostPhysics), subscribed to the engine timer by the world host. Both return
  immediately unless the play state is Playing.
- `RuntimeWorldLifecycle.TickGroup` snapshots the ordered queues of the group
  under a lock into a pooled array and dispatches each queue. A queue first
  applies its pending registrations, then invokes its callbacks in
  registration order.
- Pending application is linear per change: an add scans the callback list
  for a duplicate and a remove scans and shifts it. Registering or removing n
  callbacks of one queue in one update is therefore quadratic in n.
- `Engine.TickList` in the bootstrap assembly is the legacy list. Nothing
  constructs it, so it is dead code, and it is the only place that records
  per-component tick time. The profiler's component timing panel therefore
  receives nothing from the actual path.
- `UnregisterAnimationTick` wraps its argument in a new closure, which can
  never equal the closure registered by `RegisterAnimationTick`, so it cannot
  remove anything. It has no caller; registered animation ticks are removed
  when their object is destroyed, through the object's own tick cache.

## Hypothesis and acceptance (declared before measuring)

Hypothesis: on the reported workload the Core update is negligible when
warmed, the tick path is a small part of the 0.05 ms iteration, and pending
registration matters only during bulk activation.

Entry threshold for any behaviour change, measured with default-off counters
inside the actual tick path:

1. tick path (queue snapshot, pending application and callbacks of the Normal
   and Late groups) at or above 0.10 ms mean per update in a warmed window, or
2. pending application at or above 1.0 ms in total within any one-second span
   of scene load, play entry or play exit, or
3. one callback identity at or above 1.0 ms mean per invocation, which then
   becomes its own item for that exact owner.

Falsifier of the hypothesis: any of the three is reached. If none is reached
the gate's own rule applies: negligible warm update cost defers tick
optimizations, and S14 is dispositioned Deferred with the measured numbers
and a reopening condition.

Observer budget: counters are off by default, the dispatch loop with counters
off keeps its current shape and allocates nothing, and the counters-on cost is
reported.

Workloads: scene load, warmed stationary view, camera motion, play exit and
re-entry, and adding, moving and removing an object, all on the S13a fixture
in a named isolated Release session. Not planned: synthetic bulk registration
(it would need test code, which needs separate clearance) and physical XR.

## Measurement result against the entry thresholds (recorded before any change)

Two named isolated Release sessions with `XRE_WORLD_TICK_TELEMETRY=1`, the
second with the refined counters (slow-invocation buckets, garbage-collection
coincidence, maximum timestamps, observer work excluded from the pending
time). The world is in the playing state from launch in the unit-test editor
world: no update call was skipped in either session.

- Threshold 1 is not reached: the Normal and Late groups together take
  25.6 us per update in the warmed stationary window and 23.9 us in camera
  motion, 49.5 to 54.2 us while a selected object adds the transform tool's
  tick, against 100 us.
- Threshold 2 is not reached: warmed windows apply no registration at all;
  play entry applies 7 registrations and 10 removals and play exit 5 and 11.
  The longest single pending application of the session is 0.385 ms, during
  the first registrations at load; no transition raised it.
- Threshold 3 is not reached: ten kinds of callback were seen; the costliest
  mean is 8.5 us (the editor camera pawn's input tick).

The hypothesis holds, so no tick optimization is justified and the
performance disposition is Deferred.

## Change predeclared after measurement: per-dispatch allocation

The same counters show the dispatch itself allocating on every update. The
physics groups, which have no callbacks and no ordered queues, allocate
56 bytes per dispatch; the Normal group allocates 88 bytes and the Late group
72 bytes beyond what their callbacks allocate. Those three sizes are exactly
one `Stack<Node>` plus its node array for zero, four and two tree nodes: the
enumerator of `SortedDictionary.Values`, which `TickGroup` walks under its lock
on every dispatch to copy the ordered queues. At 90 updates and 30 fixed
updates per second that is about 19 KB per second of heap allocation in the
update and fixed-update loops, which the repository's code rules treat as a
defect regardless of size. (The observer budget above holds: the observer adds
no allocation; this allocation predates it.)

- Change: each tick group keeps an ordered array of its queues, published as a
  new copy whenever the first registration of a new order value creates a
  queue. Queues are never removed, so dispatch reads the published array
  without enumerating the sorted collection, without the pooled snapshot and
  without the group lock.
- Hypothesis: the dispatch allocates nothing of its own; order, membership and
  pending-registration semantics are unchanged.
- Acceptance: with the counters on, the physics groups allocate 0 bytes per
  dispatch and the Normal and Late groups allocate exactly what their
  callbacks allocate; the queue order, callback order and callback membership
  before, during and after a play round trip match the unchanged build; the
  existing tick lifecycle tests pass with the counters off and on; no new
  warning.
- Falsifier: any bytes allocated by the dispatch itself, any change in order
  or membership, or any failing test.
- Budget: the change must not add work to the dispatch; creating a queue for a
  new order value (a registration-time event) may copy the queue array.

## Instrumentation

Default-off world tick counters, enabled with `XRE_WORLD_TICK_TELEMETRY=1`
before launch and read with the read-only MCP tool `get_world_tick_telemetry`
(documented in the profiler guide). They sit inside the actual path,
`RuntimeWorldLifecycle` and the world's update entry points, and record per
tick group the dispatches, queue visits, callbacks, dispatch, snapshot,
pending and callback time, dispatch-thread allocation, pending adds, duplicate
adds, removes, removals that found nothing, membership comparisons and
high-water marks with timestamps; per kind of callback the invocations, total
and longest time with its timestamp, counts of invocations of at least 0.25,
1, 4 and 16 ms, how many of the 1 ms ones coincided with a garbage
collection, and the bytes allocated. A callback registered through a
compiler-generated closure (an animation tick) is named by the method the
closure forwards to. Identifying a newly registered callback is observer work
and is timed separately from the pending time. With the variable unset,
`TickGroup` and each queue take their unobserved path, and the only added work
is a static flag check per group and a null check per queue.

The legacy `Engine.TickList` is not instrumented: nothing constructs it. The
update-thread entries of the code profiler's frame-drop log are attributed by
method name, not by the event listener index they carry.

Evidence: `reports/probe-1` (first counters), `reports/probe-2` (refined
counters, before the change), `reports/probe-3` (after the change), probe
driver `scratch/s14_probe.py`, session environment `scratch/session-env.json`,
fixture `scratch/unit-world-s14.jsonc` (SHA256
`4533B5F71F644DF075CC8665EB5FBBE8640FEDADBADF6BBF8A608DB49052FB17`, the S13i
fixture). Named isolated Release sessions built from the working tree with
zero warnings and zero errors; host as in the S13i record.

## Measured tick path (before the change)

Per update: mean over the window; "tick path" is the Normal plus Late group
dispatch, which is everything `RuntimeWorld.Update` does. Physics groups are
per fixed update. Counters on; the unobserved path can only be cheaper.

| Window | Seconds | World updates | Normal us | Late us | Tick path us | Snapshot us | Pending us | Callback us | Bytes per update | Fixed updates | Physics groups us | Registrations | Removals |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| settle | 90.0 | 8,102 | 22.17 | 5.07 | 27.23 | 1.49 | 1.21 | 23.14 | 983 | 2,701 | 1.21 | 0 | 0 |
| stationary | 60.0 | 5,401 | 20.40 | 5.16 | 25.56 | 1.47 | 1.19 | 21.53 | 981 | 1,801 | 1.17 | 0 | 0 |
| camera motion | 60.0 | 5,405 | 20.39 | 3.46 | 23.86 | 0.96 | 0.79 | 21.17 | 1,028 | 1,801 | 0.97 | 0 | 0 |
| object added and selected | 5.2 | 465 | 44.95 | 9.29 | 54.24 | 1.34 | 1.24 | 50.29 | 1,328 | 155 | 1.09 | 1 | 0 |
| object moving | 6.4 | 575 | 40.26 | 9.26 | 49.52 | 1.42 | 1.36 | 45.29 | 1,239 | 192 | 1.27 | 0 | 0 |
| object removed | 5.0 | 452 | 43.13 | 9.46 | 52.59 | 1.52 | 1.26 | 48.41 | 1,213 | 151 | 1.29 | 0 | 0 |
| play entry (30 s window) | 30.2 | 2,033 | 13.77 | 6.01 | 19.78 | 1.40 | 0.88 | 16.20 | 817 | 715 | 1.76 | 7 | 10 |
| playing | 20.0 | 1,800 | 12.70 | 5.39 | 18.09 | 1.28 | 0.89 | 14.93 | 852 | 600 | 1.51 | 0 | 0 |
| play exit (30 s window) | 30.2 | 2,492 | 10.90 | 3.98 | 14.88 | 0.87 | 0.63 | 12.64 | 733 | 794 | 0.91 | 5 | 11 |
| after play | 30.0 | 2,700 | 12.96 | 4.31 | 17.27 | 0.97 | 0.73 | 14.76 | 696 | 901 | 0.94 | 0 | 0 |

The world updates at 90 Hz and fixed-updates at 30 Hz and is in the playing
state from launch, so the tick groups always run in the unit-test editor
world. No callback is registered in any physics group. Pending time per
dispatch without any change (about 1 us) is the counters' own timestamps
around an empty queue.

Callbacks in the warmed windows (all ten kinds seen are listed in
`reports/probe-2/probe.md`):

| Callback | Group, order | Stationary us per call | Motion us per call | Bytes per call | At or above 1 ms in 120 s |
| --- | --- | ---: | ---: | ---: | ---: |
| Editor camera pawn input tick | Normal, 200000 | 7.37 | 5.07 | 0 | 0 |
| Editor camera pawn tick | Normal, 200001 | 2.73 | 6.55 | 0 to 160 | 0 |
| Unit-test world FPS overlay (animation tick) | Normal, 400000 | 5.17 | 3.97 | 660 to 773 | 0 |
| Audio listener position | Late, 800000 | 4.09 | 2.84 | 48 | 1 (with a garbage collection) |
| Skybox | Normal, 800000 | 0.54 | 0.35 | 0 | 0 |

The FPS overlay's allocation is its text rebuild, which it already limits to
four times a second (about 15 KB per rebuild); it is diagnostic test-world
code. Since launch, slow tick callbacks were rare: the editor camera pawn's
first input and tick calls took 18.7 and 16.8 ms seven seconds after launch
(the 47 ms first Normal dispatch, a one-time cold start), and afterwards the
warmed session saw four invocations of 1 ms or more in about 5.5 minutes, two
of them during a garbage collection. The rare 3 ms whole-iteration spikes in
the S13i captures (about seven a minute) are therefore mostly outside the
world tick callbacks: in the other update-event subscribers or in the
harness's per-frame observer, which S13i showed adds its own periodic stall.
The update-thread entries of the frame-drop log in these sessions all sit
inside `RuntimeWorld.Update` and match the callback maxima above.

## Play transitions

| Session | Build | Entry: capture, restore, world update gap | Exit: restore, world update gap |
| --- | --- | --- | --- |
| probe 1 | before the change | 2.0 s + 9.4 s, about 12 s | 3.3 s, about 3.3 s |
| probe 2 | before the change | 2.3 s + 5.8 s, 8.7 s | 3.2 s, 3.4 s |
| exit comparison 1 | before the change | 5.5 s | 3.7 s |
| exit comparison 2 | before the change | 7.5 s | failed, no restore (world frozen) |
| repeated cycles, cycle 1 | before the change | 6.0 s | 3.4 s |
| probe 3 | after the change | 5.9 s | failed, no restore (world frozen) |
| repeated cycles, cycle 1 | after the change | 7.7 s | 3.7 s |
| exit comparison 1 | after the change | 5.7 s | 3.2 s |
| exit comparison 2 | after the change | 5.7 s | 3.3 s |

Update gaps come from polling the counters every quarter second; capture and
restore times from the play-mode snapshot log.

- **The world does not update during a transition.** Entry serializes the
  scene into a 320 MB snapshot and restores it into the play world; exit
  restores the edit snapshot. The world update is not called in between.
  When updates resume, the timer credits at most one second of the stall to
  the first update and pays the remaining debt with about 90 near-zero-delta
  updates, four per dispatch, within about 0.3 s: no simulated time is
  invented and no fixed step is dropped.
- **Registration during transitions is small.** Entry applies 7 registrations
  and 10 removals and exit 5 and 11; 4 and 5 of those removals find nothing
  (the same tick unregistered twice, harmless no-ops). No duplicate add
  occurred.
- **Membership returns to its pre-play state after every successful exit,
  with one exception.** Before play: editor camera pawn input and tick, FPS
  overlay, skybox, audio listener (and the transform tool's tick while an
  object is selected). In play the play pawn's input and tick replace the
  editor pawn's input tick. After exit the editor pawn's input tick returns
  and the play pawn's ticks leave, but the light-probe grid spawner's
  deferred-spawn retry tick, which registers during play entry, stays
  registered indefinitely; before play it resolved within five seconds of
  load. This happens on both builds.

## Change: result

The dispatch's own allocation is each group's bytes per dispatch minus what
its callbacks allocated in the same window.

| Build | Window | Group | Bytes per dispatch | Callbacks' own bytes | Dispatch's own bytes | Snapshot us | Dispatch us |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| before | stationary | Normal | 860.8 | 772.8 | 88.0 | 1.06 | 20.40 |
| before | stationary | Late | 120.0 | 48.0 | 72.0 | 0.41 | 5.16 |
| before | stationary | each physics group | 56.0 | 0.0 | 56.0 | 0.08 to 0.71 | 0.10 to 0.90 |
| before | camera motion | Normal | 907.8 | 819.8 | 88.0 | 0.73 | 20.39 |
| before | playing | Late | 127.0 | 55.0 | 72.0 | 0.47 | 5.39 |
| after | stationary | Normal | 776.4 | 776.4 | 0.0 | 0.06 | 22.20 |
| after | stationary | Late | 48.0 | 48.0 | 0.0 | 0.11 | 5.61 |
| after | stationary | each physics group | 0.0 | 0.0 | 0.0 | 0.02 to 0.13 | 0.04 to 0.22 |
| after | camera motion | Normal | 833.3 | 833.3 | 0.0 | 0.08 | 23.82 |
| after | playing | Late | 51.8 | 51.8 | 0.0 | 0.03 | 3.35 |

- Allocation: zero bytes of the dispatch's own in every group and window
  (was 56 to 88 bytes per group dispatch, about 19 KB per second).
- Order and membership: identical to the unchanged build after load, before
  play, in play and after play for every successful exit (probe 2 and the
  first pre-change exit comparison against probe 3 and both post-change exit
  comparisons). Differences exist only where an exit failed or where the
  first read came before the spawner's load-time retry had resolved, on both
  builds.
- Dispatch time moves with the callbacks, which dominate it; the queue
  snapshot step fell from 0.1 to 1.1 us to below 0.13 us.
- Existing tests: the 44 tests of `SceneNodeLifecycleTests`,
  `PhysicsChainWorldLifecycleTests` and `MonkeyBallWorldAssetContractTests`,
  which register, dispatch and re-register ticks through the world, pass with
  the counters off and on, before and after the change. A broader run of 892
  existing scene, world, component, pawn and play-mode tests has 826 passes,
  3 skips and 63 failures; the same 63 fail identically with the three
  tick-path files restored to their committed versions, so none comes from
  this change (they are in humanoid animation, shader source contracts,
  snapshots and editor source checks, several under edit by another session).
  No test was added or modified.
- Warnings: none in any of the builds.

The change keeps registration during dispatch unchanged: additions and
removals still wait in each queue's pending list until its next dispatch, and
a queue created during a dispatch joins the group's next dispatch, as before.

## Pre-existing defects found by the play validation

All three reproduce on the build without the change, so they are not caused by
it; they are outside the tick path and are handed off.

1. **A failed play exit leaves the world frozen.** In 2 of 9 exits (one on each
   build) the world stopped updating at the exit request and never resumed;
   no restore began, rendering kept presenting rejected frames, and viewport
   screenshots failed for lack of a submitted frame. `RuntimeWorldHost.EndPlay`
   unhooks the world's update, fixed-update and collection callbacks first,
   and only the end of a successful exit relinks them. The exit's exception
   handler forces the editor back to edit mode without restoring the snapshot
   or relinking, so any exception between ending play and the restore leaves
   the world unhooked. A four-second stack sample a minute later showed no
   thread still inside the exit. The exception itself was not captured:
   isolated sessions write no general log file and console redirection
   captures nothing. The failure followed the full probe sequence (camera
   motion, an object added, selected, moved and deleted, 20 s of play); the
   short enter-exit cycles never reproduced it.
2. **A second play round trip loses the scene.** After one successful round
   trip, the next entry captures 7 assets instead of 888, and by the third
   transition the snapshot holds no scene; later enter and exit requests
   change nothing visible. Same on both builds (`reports/play-cycles-fix`,
   `reports/exitcmp-prefix-cycles`).
3. **The light-probe grid spawner retries forever after a snapshot restore.**
   The restored spawner keeps its deferred-spawn retry registered: it runs on
   every update (about 1 us) and schedules a background placement-bounds
   check every 250 ms, while the grid's 27 probes already exist under its
   node. Measured 44,319 invocations over eight minutes after play. It may
   also be the source of the intermittent exception in item 1, since its
   background grid build can complete while the exit deactivates nodes; that
   is a hypothesis, not a finding.

Also found: the profiler's Component Timings panel receives nothing, because
its only producer is the legacy `Engine.TickList`, which nothing constructs
since the modularization refactor; the profiler guide now says so.

## Disposition

- Tick optimization: **Deferred.** None of the three entry thresholds was
  reached: the warmed tick path is 24 to 26 us per update against 100 us,
  pending registration is a handful of changes per transition with a 0.385 ms
  worst application at load against 1.0 ms per second, and the costliest
  callback averages 8.5 us against 1.0 ms. Reopen when a workload shows any
  of the three, or when registration churns outside transitions (the linear
  pending application becomes quadratic in the number of changes per queue).
- Per-dispatch allocation: **Validated** as the one retained change.
- Play transitions: the tick path's own semantics are correct across play
  entry and exit (order, membership, pending application, timer debt). The
  play-mode snapshot and restore path is not: items 1 and 2 above, and the
  5.5 to 12 s world stall at entry, belong to the play-mode transition owner;
  item 3 to the light-probe spawner owner.
- The original report stays open; S14 found no update-side cause of the
  frame-rate report.

## Gate record

- Item / owner / status: S14, Runtime Core; Deferred for tick optimization,
  allocation change Validated.
- Prior validated item and any approved reordering: S13i; none.
- Hypothesis and disconfirming check: as declared above; the three thresholds
  were the falsifier and none was reached.
- Baseline source/configuration/cache/scene identity: working tree at
  `9fee4b983` plus the uncommitted S13f-S13h and S14 diffs; S13a fixture;
  Release; named isolated MCP sessions; warm caches.
- Change scope and dependency/lifetime invariants: `RuntimeWorldLifecycle`
  only (plus the default-off counters and the world update entry points'
  counter calls); queues are never removed, the published array is replaced
  and never mutated, pending registration unchanged.
- Predeclared metrics, budgets, tolerance, repetitions and window: as above;
  60-second warmed windows, two builds, repeated play exits.
- Changed source diff and validated binary/session identity: see the
  retained diff below; session `s14-tick` rebuilt per variant.
- Focused build command and result, including warnings: session builds and
  `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj -c Release
  --artifacts-path <run-root>/temp-build/unit-tests`; zero warnings.
- Live scenarios and exact evidence paths: `reports/probe-1` to `probe-3`,
  `reports/exitcmp-*`, `reports/play-cycles-fix`, `reports/hang`,
  `logs/session-*`.
- Before/after distributions, sample validity and observer overhead: window
  means from cumulative counters; the counters' own cost is inside the
  measured numbers, so the unobserved path is cheaper.
- Correctness/images, failure cases, retention and adjacent regressions:
  membership and order compared across states; the exit failure and scene
  loss reproduced on the unchanged build.
- Pass/fail decision and reason; does it explain the original symptom?:
  change passes its predeclared acceptance; the update path does not explain
  the original frame-rate report.
- Test clearance state, approved focused checks and results: existing tests
  run only; no test added or modified.
- User confirmation / remaining risks / next permitted item: user
  confirmation pending; risks are the handed-off play-mode defects; next item
  is S15 or one of the new handoff items.
- Temporary settings and owned session cleanup: the three tick-path files were
  temporarily restored to their committed versions to attribute test
  failures and to build the comparison editor, and restored byte for byte;
  every session started for S14 was stopped by name.

Retained diff: `XREngine.Runtime.Core/World/RuntimeWorldLifecycle.cs`,
`RuntimeWorld.cs`, `RuntimeWorld.Transforms.cs`, six new files
`RuntimeWorldTick*.cs` in the same folder,
`XREngine.Data/Environment/XREngineEnvironmentVariables.cs`,
`XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs`,
`docs/developer-guides/diagnostics/profiler.md` and
`docs/developer-guides/ai/mcp-server.md`.
