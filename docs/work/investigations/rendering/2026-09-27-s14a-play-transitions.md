# S14a: keep the world live across play-mode transitions

Status: Validated for its gate (September 27, 2026); the exit exception's
cause is open as S14e. Predeclaration written before any change.

Gate record for
[S14a](../../todo/rendering/vulkan-stall-remediation-todo.md#s14a-keep-the-world-live-across-play-mode-transitions)
under the todo document's one-by-one protocol. Opened by the
[S14 gate record](2026-09-27-s14-core-update-owner.md), which holds the entry
evidence. Evidence root: `Build/_AgentValidation/20260927-095558-s14-core-update/`
(shared with S14, whose handoffs these are; ignored and disposable, findings are
copied here).

## Entry evidence (from S14)

- In 2 of 9 exits on the S13a fixture, one on each S14 build, the world stopped
  updating at the exit request and never resumed; no snapshot restore began.
  `RuntimeWorldHost.EndPlay` unhooks the world's update, fixed-update and
  collection callbacks first; the exit's exception handler only forces the
  state back to Edit, so the world stays unhooked.
- After one successful round trip, the next entry captured 7 assets instead of
  888, and later transitions restored no scene, on both builds.
- Entry stops world updates for 5.5 to 12 s (a 320 MB scene snapshot is
  captured and restored); exit for about 3.3 s.

## Why the exception was never visible

`Debug.Log`, `Debug.LogWarning` and `Debug.LogException` compile only when
`DEBUG` or `EDITOR` is defined, and no project defines `EDITOR`. Release builds,
which every S13 and S14 measurement used, therefore discard all engine log
output, including the exception that play-mode transitions catch. Auxiliary
logs such as the play-mode snapshot diagnostics are not compiled out.

## Plan and predeclaration

1. Reproduce in a Debug build, whose engine log is written, to capture the exit
   exception, the transition steps reached, and the snapshot scene trees of the
   second round trip. Nothing is changed for this step.
2. Fix the cause of the exit exception, once known; its change, hypothesis and
   acceptance are declared here before it is made.
3. Make a failed transition leave a live world: whatever step throws, the
   world's timer callbacks are relinked in edit mode, and the failure is
   written to an auxiliary log that Release builds keep. Acceptance: a forced
   failure leaves the world updating and the failure recorded in the log.
4. Find why the second round trip's capture omits the scene and fix it.
5. Measure where the entry and exit time goes (capture, restore, resolve,
   scene rebuild) and decide, with the numbers, whether shrinking the snapshot
   belongs in this item.

Gate (from the todo): repeated enter and exit cycles after camera, object and
play activity keep the scene, keep the world updating and return tick
membership to its pre-play state.

## Step 1: reproduction in a Debug build

A Debug isolated session ran the full S14 probe sequence (camera motion, an
object added, selected, moved and deleted, 20 s of play) and then three enter
and exit cycles. The engine log and first-chance exception traces were
written. Evidence: `reports/s14a-debug-probe`, `reports/s14a-debug-cycles`,
`logs/s14a-debug/`.

- **The exit freeze did not reproduce.** The probe's exit and all later exits
  completed (Debug exit 6.5 s). The frozen-exit evidence from S14 therefore
  stays the only record of it; see below for what the S14 counters narrow it
  to.
- **The scene loss reproduced deterministically, and its cause was found.**
  The second entry's capture threw while sizing the scene:
  `InvalidOperationException: This operation cannot be performed on a default
  instance of ImmutableArray<T>` from `ListCookedBinaryModule.TryAddSize` under
  `WorldStateSnapshot.Capture`. The chain:
  1. The cooked binary reader cannot rebuild an `ImmutableArray<T>`.
     `ReadList` constructs the recorded list type (for a struct, an
     uninitialized default instance) and appends items through `IList.Add`.
     An empty array appends nothing and comes back as `default`; a non-empty
     one would throw.
  2. Every shader's `SlangShaderOptions` holds four empty immutable arrays, so
     each restore (entry makes the play world from a restored copy, exit
     restores the edit copy) leaves every shader with default arrays.
  3. The next capture enumerates them and throws. The snapshot's serializer
     wrapper swallows the exception and returns nothing; the capture logs
     "serialized to a null payload" and still reports the snapshot valid,
     without the scene.
  4. The restore treats a scene missing from the snapshot as one "introduced
     after the snapshot" and removes it from the world. From then on the world
     has no scene, and every later capture and restore is empty.
- **Where the transition time goes** (Debug; the S14 Release numbers are in
  the S14 record): capture serializes 320,407,142 bytes in 6.24 s; the entry
  restore takes 12.99 s, of which 12.67 s is scene deserialization with asset
  references resolved as it goes (2,137 references per restore: the uber
  shader source 786 times and one texture 264 times); the exit restore takes
  6.28 s; the asset summaries logged around each step take 7 to 35 ms. Scene
  unload and reload are not separately visible and fit in the remainder.
  The cooked format has no shared-reference table: an object reachable twice
  is written twice, so materials, shaders and texture references repeat per
  submesh, and a component referenced from another component (the light-probe
  spawner's placement model, S14b) is written as a detached copy.
- **The spawner.** After each restore the spawner's placement model resolves
  with no meshes (`no valid bounds (models=1, meshes=0)`), so its retry never
  ends. That is S14b.
- **Also observed:** after each exit the Vulkan renderer readmits all 393
  meshes cold, since the restore creates new mesh objects; in the Debug build
  viewport screenshots fail for over a minute after exit while that
  admission defers the scene. Outside this gate; recorded for the rendering
  owner.

### What the S14 counters say about the frozen exit

In both frozen exits the tick counters show 11 removals and no registration
after the exit request; successful exits show 11 removals and 5
registrations. The node deactivation that removes those ticks therefore
completed, and the exception came after it and before any persistent root was
reactivated or the snapshot restore began: in the world host's backend
teardown (physics scene destroy, visual scene destroy, physics debug renderer
reset) or at the start of persistent-root reactivation. The visual scene's
teardown releases GPU scene buffers and lists on the update thread while the
render thread keeps presenting; that is a candidate, not a finding.

## Changes predeclared for steps 2 to 4

Hypotheses, acceptance and falsifiers are fixed here before any code change.

1. **Immutable arrays round trip through the cooked binary format.** The
   reader builds `ImmutableArray<T>` from the items it reads, and a default
   `ImmutableArray<T>` (a legitimate, distinct state) is written as null so it
   round trips instead of throwing.
   Hypothesis: the second capture fails only because of this.
   Acceptance: on the S13a fixture every capture after the first serializes
   the scene with a payload within 1% of the first and the same number of
   asset decisions, and every restore keeps the scene.
   Falsifier: a later capture still fails or differs materially; then another
   member does not round trip, and it is found before closing.
2. **A failed capture never deletes a scene.** A scene that fails to
   serialize makes the snapshot invalid and its exception is written to the
   play-mode snapshot diagnostics log; entering play refuses an invalid
   snapshot and leaves the world untouched in edit mode; a restore never
   removes a scene that was present at capture.
   Acceptance (forced failure): a build with this change but without change 1
   refuses the second entry, keeps the scene, keeps the world updating in edit
   mode, and records the failure and its exception in logs that Release builds
   keep.
3. **A failed transition leaves a live edit-mode world.** Entry and exit
   track the step in progress; on an exception, recovery ends play on every
   world, clears the game mode, restores the edit snapshot unless the
   transition already restored it, restarts every world in edit mode (which
   relinks its timer callbacks) and publishes the edit state, each step
   guarded. The failure (transition, step, exception) is written to
   `playmode-transitions.log`, an auxiliary log Release builds keep, and to the
   engine log.
   Acceptance: the forced entry failure of change 2, and any exit failure
   that recurs during the validation runs (the world keeps updating, and the
   exception with its stack is in the log). No test-only hook is added, so if
   no exit fails during validation the exit recovery is reported as reviewed
   but not exercised.
4. **Snapshot size.** Deferred from this item with the numbers above: the
   320 MB payload comes from the format writing shared objects repeatedly,
   and fixing that is a format change (a shared-reference table in the cooked
   format, or snapshot sharing of unmodified generated assets) with its own
   identity and restore semantics. It is opened as its own item. The one
   duplicate owned by a scene component, the spawner's placement model, is
   handled in S14b.

## Changes 2 and 3: forced capture failure (stage A)

Built with changes 2 and 3 but without change 1, so the second entry's capture
fails exactly as in the Debug reproduction. Release build, S13a fixture, two
gate cycles (camera motion, a cube added, selected, moved and deleted, then
play). Evidence: `reports/s14a-stagea`, `logs/s14a-stagea/`.

| Check | Cycle 1 | Cycle 2 |
| --- | --- | --- |
| Entry | Play; world update gap 9.9 s | Refused in about 1 s; state Edit; update gap 0.26 s |
| Exit | Edit; update gap 3.2 s | Not requested |
| After, 2 s window | 181 updates; 1 scene, 34 nodes, 27 probes | 180 updates; 1 scene, 34 nodes, 27 probes |

- The refused entry wrote `Play mode enter failed at 'snapshot capture'`
  with the `InvalidOperationException` and its full stack to
  `playmode-transitions.log`, and the scene's failure to
  `playmode-snapshot-diagnostics.log`, both in the Release session's log
  folder. No restore ran, and nothing in the world changed.
- Timing split on this Release build: capture 2.30 s for 320,407,132 bytes;
  entry restore 9.94 s (scene deserialization 9.57 s); exit restore 3.10 s.
- The spawner's deferred-spawn retry tick again stayed registered after the
  first exit (S14b).

Acceptance for change 2 met. Change 3's recovery path was not reached here: a
capture failure is refused before the world changes.

## Changes 1 to 3 together (stage B)

Release build with all three changes, S13a fixture, four gate cycles, each
with camera motion, a cube added, selected, moved and deleted, 15 s of play
and an exit. Evidence: `reports/s14a-stageb`, `logs/s14a-stageb/`.

| Cycle | Entry update gap | Exit update gap | After exit: state, updates in 2 s, scenes, nodes, probes |
| --- | --- | --- | --- |
| 1 | 11.7 s | 3.6 s | Edit, 181, 1, 34, 27 |
| 2 | 5.1 s | 4.3 s | Edit, 181, 1, 34, 27 |
| 3 | 5.9 s | 4.6 s | Edit, 180, 1, 34, 27 |
| 4 | 6.0 s | 5.1 s | Edit, 181, 1, 34, 27 |

- **Change 1, acceptance met on the payload; the decision count was the
  wrong measure.** Every capture after the first serialized the scene:
  320,125,744 bytes against 320,407,132 for the first (0.09% smaller), and
  every restore kept the scene. The asset decision count rose from 888 to
  4,259, because the decision log counts distinct object instances and a
  restore unshares objects the cooked format writes by value: the 25 shared
  materials come back as 786 (one per submesh, twice), and the graph holds
  two `Model` instances. The payload size shows the format already wrote each
  occurrence in full in the first capture, so the round trip itself is
  faithful. The criterion is corrected here rather than reinterpreted: the
  count is not comparable across a restore.
- **The second model is the light-probe spawner's.** Two `Model` instances
  after a restore (786 submeshes, twice the scene's 393) confirm that the
  spawner's placement-bounds model reference is serialized as a full copy of
  the Sponza model, so about half the snapshot is that duplicate. S14b
  removes it.
- **No transition failed** in the four cycles (no `playmode-transitions.log`
  was written), so the recovery path of change 3 was not exercised; the exit
  freeze did not recur.
- **Restore time.** The first entry restore takes 9.9 s; later restores of
  the same payload take 3.5 to 4.8 s, creeping up by about 0.25 s per
  restore across the run.
- **Tick membership.** After every exit the membership matched its pre-entry
  state except the spawner's deferred-spawn retry tick, which stays
  registered from the first restore on (S14b). The transform tool's display
  tick present before each entry belongs to the selection the cycle made;
  entry clears the selection.

## Gate validation

The gate was run on the build with S14b's spawner change, since the spawner's
retry tick alone kept membership from returning in stage B; see the
[S14b record](2026-09-27-s14b-probe-spawner-restore.md#result). Four cycles of
camera motion, a cube added, selected, moved and deleted, and 15 s of play:
after every exit the editor was in edit mode, the scene kept its 34 nodes and
27 probes, the world performed 180 updates in 2 s, and the tick membership
equalled its state before the first entry. Entry took 10.3 s the first time
and 2.6 to 3.2 s afterwards; exit 1.6 to 2.7 s.

Three runs of the full S14 probe sequence on the final build (with S14c),
each on a fresh process, gave the same result: exits of 1.8 s, entries of 9.6,
3.4 and 5.4 s, and membership after play equal to membership before play
apart from the transform tool's tick, which entry clears with the selection.
Evidence: `reports/s14a-final-probe-1` to `-3`.

## The exit exception

It did not recur. Since the recovery and the Release-kept failure log were
built, 13 exits ran (stage A 1, stage B 4, S14b gate 4, final probes 3, the
render check below 1), and the Debug reproduction added 4 uninstrumented
exits; none failed. In S14, 2 of 7 exits after the full probe sequence failed;
three clean full-probe runs would still happen about one time in three at
that rate, so this does not show that the cause is gone. The S14 counters
place the throw after node deactivation and before any persistent root was
reactivated, in the world host's backend teardown. S14b removed one
candidate, the spawner's background placement retry that ran through every
exit. A recurrence now leaves a live edit-mode world and writes the exception
with its stack to `playmode-transitions.log`. Opened as S14e.

## After an exit the viewport publishes no scene frame (opened as S14d)

Found while checking the final build, and present on every S14 build: after a
play exit, capturing the viewport fails with "The requested viewport has no
matching submitted Vulkan resource-planner generation" for at least 60 s
(the last submitted receipt carries resource generation 4 while the viewport
is on generation 8). Before play and in play the capture succeeds. The
renderer's frame lifecycle reports `AdmissionDeferred` at `ResourcePrepare`
with present-now readiness retries, after a cold meshlet import of all 393
meshes (540 ms): the restore makes every mesh a new object, so the renderer
admits the whole scene again, and in the Debug build it logged "Mesh resource
preparation yielded before publishing a partial scene" with 392 deferred
meshes. In one session with profiler frame logging left on, render frames
then grew from 0.5 s to 0.8 s over two minutes, starting with the first
failed capture; a fresh session without frame logging showed no stall after
failed captures. The viewport therefore appears to keep presenting the last
completed image after an exit. Evidence: `reports/s14-render-after-exit.log`,
`reports/s14a-final-probe-1/`.

## Snapshot identity and cost (opened as S14f)

The cooked format writes an object once per reference, with no shared
references. S14b removed the largest duplicate, but a capture is still
160 MB for a scene whose meshes hold 10 MB of vertex data, and a restore
does not return the scene it captured: the 25 materials shared by 393
submeshes come back as 393 separate copies (so an edit after play changes one
submesh), every mesh and material is a new object (the renderer readmits the
whole scene, S14d), and the first entry restore of a process takes 2.2 to
8.8 s against 1.4 to 2.3 s for later ones. Changing that is a format and restore-semantics
design, outside this item.

## Disposition

- **Scene loss:** fixed and Validated (changes 1 and 2).
- **Failed transitions:** a capture failure refuses entry without touching
  the world, and any other failure recovers into a live edit-mode world; both
  write the failure where Release builds keep it (changes 2 and 3). The
  refusal was validated with a forced failure; the recovery after the world
  changed was not exercised live, because no transition failed.
- **The exit exception:** not captured; S14e.
- **Transition cost:** entry 2.6 to 5.4 s (up to 10.3 s for a process's
  first entry) and exit 1.6 to 2.7 s, from 5.5 to 12 s and 3.3 s, mostly
  through S14b halving the snapshot. The rest is S14f.
- **Gate:** met.

## Tests

Focused run of the existing unit tests over the touched areas (snapshots,
cooked binary, play mode, light probes, scene node lifecycle, ticks, profiler,
runtime world, serialization): 369 tests, 334 passed, 35 failed. All 35 fail
the same way on the committed revision, built in a detached worktree without
this work, the S14 changes or any other uncommitted change, so none is
introduced here. They are source-contract tests over files this work did not
touch, skinned-mesh round trips rejected by the Core4 compute-skinning format
check, and prefab metadata identity tests. The passes include the spawner's
source contract, YAML round trip and deactivation tests, the world snapshot
restore test, the cooked serializer round trips and the tick lifecycle tests.
`ExitPlayMode_RestartsWorldsBeforePublishingEditState` looks for a call the
exit path already did not make before this work; it was left unchanged, since
changing tests needs the owner's clearance.

## Gate record

- Item / owner / status: S14a, Runtime Core with the editor play-mode owner;
  Validated for its gate, with the exit exception's cause open as S14e.
- Prior validated item and any approved reordering: S14; S14a to S14c were
  validated in sequence on successive builds; no reordering.
- Hypothesis and disconfirming check: as declared under "Changes predeclared
  for steps 2 to 4"; change 1's decision-count criterion was corrected
  because the count is not comparable across a restore; no falsifier
  occurred.
- Evidence: Debug and Release isolated sessions on the S13a fixture;
  `reports/s14a-debug-*`, `s14a-stagea`, `s14a-stageb`, `s14b-gate`,
  `s14a-final-probe-1` to `-3`, `s14-render-after-exit.log`, and
  `logs/s14a-*`.
- Docs: play-mode architecture (transition failures), profiler guide and
  light-probe guide.
- Handoffs: S14d, S14e and S14f.
