# S14b: stop the light-probe grid spawner retrying after a restore

Status: Validated (September 27, 2026). Predeclaration written before the
change; result and disposition below.

Gate record for
[S14b](../../progress/rendering/vulkan-stall-remediation-results.md#play-transition-results)
under the todo document's one-by-one protocol. Opened by the
[S14 gate record](2026-09-27-s14-core-update-owner.md); the reproduction and
the snapshot measurements are in the
[S14a record](2026-09-27-s14a-play-transitions.md). Evidence root:
`Build/_AgentValidation/20260927-095558-s14-core-update/` (ignored and
disposable; findings are copied here).

## Entry evidence

- After every play-mode snapshot restore, the spawner's deferred-spawn retry
  tick stays registered for the rest of the session and schedules a
  background placement check every 250 ms, while the grid's 27 probes exist
  under its node. Before any play it resolves within five seconds of load.
- The Debug reproduction logs each retry as
  `TryGetPlacementBounds: no valid bounds (models=1, meshes=0, validBounds=0)`:
  the restored spawner has one placement model and that model has no meshes.
- After one restore the scene graph holds two `Model` instances (786
  submeshes, twice the scene's 393), and every capture is about 320 MB.

## Code reading

- `PlacementBoundsModels` is a `ModelComponent[]` serialized like any other
  member. Neither the cooked binary format nor YAML has shared references: a
  referenced component is written as a full copy. After a restore the spawner
  therefore holds a detached copy of the scene's model component, with its
  own copy of the whole Sponza model. The copy has no scene node and is never
  activated, so it has no renderable meshes and no world bounds; the placement
  check can never succeed, and the retry never ends. The copy is also written
  into every snapshot next to the real model.
- The spawner's generated probes are ordinary child nodes and are serialized,
  but the lists that name them (`_spawnedNodes`, `_spawnedProbes`) are
  runtime-only. A restored spawner starts with empty lists, requests a new
  grid on begin play and activation, and applies a finished build without
  clearing anything (`ReplaceExistingGrid` is false and its lists are empty).
  Today the build never finishes because of the first defect; once it does,
  a second grid of 27 probes would be spawned beside the restored one.
- With start-of-play capture (`AutoSequentialCaptureOnBeginPlay`), every begin
  play rebuilds the grid in place and recaptures it; that is intended and
  unchanged.

## Hypothesis, change and acceptance (declared before the change)

Hypothesis: both defects come from serializing references by value. Storing
references by identity and rebinding them to the live objects removes the
retry and the risk of a second grid, and removes the model copy from the
snapshot.

Change:

1. The placement models are serialized as the identities of the referenced
   components (`PlacementBoundsModelIds`); `PlacementBoundsModels` itself is no
   longer serialized. The spawner binds the identities to the live model
   components of its world when it begins play, activates or builds its grid.
2. The spawner records the identities of the probe nodes it generates
   (`SpawnedProbeNodeIds`) and, when its runtime lists are empty after
   deserialization, re-adopts the child nodes with those identities before it
   decides whether a grid exists.

Acceptance, on the S13a fixture over repeated play round trips with editor
activity:

- after each exit, once the start-of-play capture has finished, the spawner's
  ticks match their state before the first entry (no deferred-spawn retry and
  no placement rebuild tick left registered);
- the probe count stays 27 at every check, in play and in edit mode;
- the capture payload falls below 200 MB, and a restore produces one `Model`
  instance.

Falsifiers: the retry tick remains registered, the probe count ever exceeds
27, or the payload stays near 320 MB (then the duplicate is not the
spawner's).

Tests that pin the spawner's source or internals
(`LightProbeArrayReadinessTests`, `SceneNodeLifecycleTests`,
`LightProbeComponentYamlDeserializationTests`) must keep passing; none is
changed.

## Result

Release build with the S14a changes and this change, S13a fixture, four gate
cycles (camera motion, a cube added, selected, moved and deleted, 15 s of
play, exit), each after-exit check taken once no spawner capture, retry or
rebuild tick remained (none was pending at any check). Evidence:
`reports/s14b-gate`, `logs/s14b/`.

| Cycle | Entry update gap | Exit update gap | After exit: probes (same identities as before play), ticks versus before play |
| --- | --- | --- | --- |
| 1 | 10.3 s | 2.2 s | 27 (27), identical |
| 2 | 2.9 s | 1.6 s | 27 (27), identical |
| 3 | 3.2 s | 2.7 s | 27 (27), identical |
| 4 | 2.6 s | 2.2 s | 27 (27), identical |

- **Ticks:** after every exit the world tick membership equals the membership
  before the first entry; the deferred-spawn retry never stays registered.
  In play the only differences are the play pawn's ticks replacing the
  editor pawn's input tick.
- **Probes:** 27 at every check, in play and in edit mode, and always the
  same 27 node identities: the restored spawner adopts its restored grid, and
  no second grid is spawned.
- **Snapshot:** the first capture is 160,250,128 bytes (320,407,132 before),
  later ones 160,109,434; a restore produces one `Model` instance (393
  submeshes). Captures take 0.8 to 1.1 s (1.6 to 2.3 s before) and restores
  after the first 1.5 to 2.3 s (3.5 to 4.8 s before); the first entry
  restore still takes 8.8 s.
- No transition failed (no `playmode-transitions.log`).

All three acceptance criteria are met and no falsifier occurred.

## Disposition

Validated. The spawner stores its placement models and generated probe nodes
by identity (`PlacementBoundsModelIds`, `SpawnedProbeNodeIds`) and rebinds them
after deserialization; `PlacementBoundsModels` is no longer serialized. No
saved asset in the repository contains a spawner, so no content migrates.
YAML round trips of the new members were not exercised here; object
identities are written to YAML, but whether every loader restores them was
not verified, so a YAML-loaded spawner whose identities do not resolve falls
back to the existing deferred placement.

Checked while validating: the fixture's spawner reports
`AutoSequentialCaptureOnBeginPlay` false. S14c's session read it before any
play and it was already false, so the snapshot does not lose it; the fixture
does not start a capture at begin play, and the Code reading note above about
start-of-play capture does not apply to it.

## Gate record

- Item / owner / status: S14b, Rendering (lighting); Validated.
- Prior validated item: S14a's changes, validated in the same run of
  builds; no reordering.
- Hypothesis and disconfirming check: as declared above; no falsifier
  occurred.
- Evidence: Release isolated sessions on the S13a fixture;
  `reports/s14b-gate`, `logs/s14b/`.
- Tests: unchanged. With the final build the tests that pin the spawner's
  source and internals pass; the focused run's failures all fail the same way
  on the committed revision (see the
  [S14a record](2026-09-27-s14a-play-transitions.md#tests)).
