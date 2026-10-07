# Animation Validation

Architecture: [Baked Animation Value Compression](../../../architecture/animation/baked-value-compression.md), [Animation API](../../../developer-guides/animation/animation-api.md)
Code todos: [Baked Animation Value Compression](../../todo/animation/baked-value-compression-todo.md), [Humanoid Body/Root Parity And Compensation](../../todo/avatar/humanoid-body-root-compensation-todo.md)

## Setup

- Animation build: `dotnet build .\XREngine.Animation\XREngine.Animation.csproj`.
- Editor build: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- Isolated editor session: `pwsh Tools/Manage-McpEditorSession.ps1 Start -Name <unique-session>`, `pwsh Tools/Invoke-Mcp.ps1 -Session <unique-session> -Method ping`, then `Stop -Name <unique-session>`.
- Humanoid conformance runner: `Tools/HumanoidConformanceRunner/HumanoidConformanceRunner.csproj` with the versioned conformance manifest.
- Focused humanoid tests (run only after live validation and explicit owner clearance):
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Humanoid`
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UnityAnimImporter`
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~AnimationClipComponent`
- Allocation audit: the `Report-NewAllocations` task.

## Checks

### Baked value compression

Corpus for this group: continuous float, vector, and quaternion tracks; bool visibility or event tracks; matrix transform tracks; string, object, and method-backed discrete tracks; clips with long unchanged ranges; clips with frequent random seeks; scalar curves with small and large ranges; normalized blendshape weights; world-unit positions; colors and normalized vectors; noisy mocap data; smooth authored curves.

- [ ] Lossless baseline. Procedure: bake the corpus with `None`, `Constant`, `RunLength`, `Delta`, and `DeltaRunLength`. Expected: record raw sample counts, encoded bytes, bake time, and decode time per value family before format changes land. Last evidence: none.
- [ ] Playback allocation baseline. Procedure: profile baked playback. Expected: record the current allocation profile. Last evidence: none.
- [ ] Serialization baseline. Procedure: save and load baked clips. Expected: record behavior for requested algorithm, effective algorithm, and runtime-only stores. Last evidence: none.
- [ ] Cooked payload load. Procedure: load cooked encoded stores. Expected: no dense temporary arrays. A rejected payload gives one diagnostic and re-bakes from source. Last evidence: none.
- [ ] Delta checkpoint intervals. Procedure: benchmark intervals 8, 16, 32, and 64 on the corpus. Expected: random seek cost is bounded by the interval and long-track seek time improves without losing the delta memory benefit. Last evidence: none.
- [ ] Type-specific stores. Procedure: compare bool, matrix, numeric, and vector-lane stores with the baseline. Expected: smaller payload or lower decode cost, with exact decode. Last evidence: none.
- [ ] Lossy candidates. Procedure: compare each lossy codec with raw and lossless stores on the corpus. Record bytes, bake time, decode time, and measured error. Expected: each codec meets its error budget and beats raw FP32 size on representative tracks. Vector codecs beat `DeltaRunLength` on smooth and noisy vector tracks. Last evidence: none.
- [ ] Playback allocation audit. Procedure: run the allocation audit on baked playback after each new store or codec. Expected: zero heap allocation in steady-state playback. Last evidence: none.
- [ ] Editor estimates. Procedure: open the bake inspector on corpus tracks. Expected: an author can compare memory and decode cost before changing a codec, and see saved memory and introduced error before accepting a lossy bake. Last evidence: none.
- [ ] Builds and tests. Procedure: build `XREngine.Animation`, run targeted animation tests and any new editor tests. Expected: pass with zero new warnings. Last evidence: none.

### Humanoid body and root

Procedure for each row: select a manifest row; verify the reference schema, source hashes, coordinate spaces, Unity version provenance, avatar-definition signature, and import settings; start an isolated editor session; import the model and `.anim` normally; validate the automatic mapping or load persisted corrections; pause and sample key times, frame boundaries, half-frame points, random times, loop seams, and transition fractions; compare numeric Body, root, Hips, bone, endpoint, IK, event, and binding data first, then view PNGs from several cameras; stop the session and read the logs.

- [ ] Manifest rerun. Procedure: run all declared manifest rows after the vertical Body/Hips and Z-up basis fixes. Expected: every row within the ratified gates. Last evidence: 2026-08-31 (only three direct Sexy Walk rows rerun; conventional and lean rows fail by about 15 mm vertical; Z-up row fails by about 90 degrees and 124 mm).
- [ ] Root settings and behaviors. Procedure: every root setting and purpose-built clip on every compatible avatar and applicable route. Expected: within gates. Last evidence: none.
- [ ] Playback routes. Procedure: direct playback, exact seek, reverse, at least ten signed loop epochs, one-state playback, transitions, interrupted transitions, and 1D, 2D, and direct blend trees. Expected: projected root pose and delta, Body pose, Hips local and world transforms, all mapped bone rotations, selected endpoints, IK goals and contact intervals, events, and object bindings within gates. Last evidence: none.
- [ ] Authored IK and contacts. Procedure: rows with authored IK. Expected: IK applied and contacts observed within gates. Last evidence: 2026-08-31 (`InverseKinematicsApplied=false`, zero foot contacts).
- [ ] Rename and move invariance. Procedure: rename and move a model and `.anim` without changing contents, then repeat the row. Expected: the same imported identities and evaluated animation. Last evidence: none.
- [ ] Mapping persistence. Procedure: avatars with conventional names, arbitrary names and namespaces, different proportions, nonstandard bind axes, and missing optional bones; save, reopen, move, and reimport with locked corrections; then reimport an incompatible skeleton. Expected: each reaches a valid definition through automatic mapping plus editor correction; corrections persist; the incompatible case reports a precise conflict. Last evidence: none.
- [ ] Unseen pair. Procedure: an avatar and clip added after the solver design freeze. Expected: within gates with no code change, clip-specific setup, or manual coordinate flip. Last evidence: none.
- [ ] New avatar without code change. Procedure: import and play a newly supplied compatible avatar and `.anim`. Expected: no source edit, name table, path configuration, or recompile. Production avatar-definition loading works outside the Unit Testing World. Last evidence: none.
- [ ] Incompatible definition. Procedure: play a clip on an avatar definition that does not match the skeleton. Expected: a clear error; no silent remap or data substitution. Last evidence: none.
- [ ] Single path. Procedure: review the import, editor, runtime, test, and CI workflows. Expected: one conversion and evaluation path; no Unity executable or install dependency; no Unity-evaluated target-pose artifact; incomplete features fail explicitly. Last evidence: none.
- [ ] Live editor check. Procedure: all three redistributable avatars in an isolated session. Expected: numeric and visual evidence within gates. Last evidence: none.
- [ ] Builds, tests, and allocations. Procedure: focused builds, then the focused humanoid tests after clearance. Expected: zero new warnings; per-frame paths allocate nothing. Last evidence: 2026-08-25 (focused filter passed 113/113; protects internal contracts only).
- [ ] Final coverage report. Procedure: publish one passing conformance coverage report. Expected: the generic matrix passes; a named model and clip pair alone does not close this check. Last evidence: none.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Humanoid manifest rerun (conventional and lean rigs) | About 15 mm vertical Body/Hips and endpoint error | [Humanoid TODO: Solver](../../todo/avatar/humanoid-body-root-compensation-todo.md#solver), [Body frame investigation](../../investigations/avatar/humanoid-body-frame-compensation-2026-08-31.md) |
| Humanoid manifest rerun (arbitrary-name Z-up rig) | About 90 degrees and 124 mm model/root basis error | [Humanoid TODO: Solver](../../todo/avatar/humanoid-body-root-compensation-todo.md#solver) |
| Authored IK and contacts | IK not applied on direct route | [Humanoid TODO: Solver](../../todo/avatar/humanoid-body-root-compensation-todo.md#solver) |
