# Affine Matrix Phase 4 Closeout - 2026-03-19

Reference design: [Affine Matrix Integration Plan](../design/affine-matrix-integration-plan.md)

This closeout now serves as the consolidated final record for the affine rollout. It supersedes the earlier phase todo, baseline notes, and intermediate validation snapshots.

## Scope

Phase 4 is a decision checkpoint, not a new storage migration. The goal of this pass was to review the Phase 1-3 benchmark and validation data, rerun the required closeout validation, and decide whether any internal caches or arrays should move from `Matrix4x4` storage to `AffineMatrix4x3`.

## Consolidated Rollout Summary

- Phase 0 established the microbenchmark and scenario baseline.
- Phases 1 and 2 justified keeping the transform hierarchy and local-matrix fast paths where affinity is guaranteed.
- Phase 3 stayed intentionally narrow: the affine-only CPU bounds helpers were kept, but the measurements did not justify widening that work into broader storage changes.
- Phase 4 closes the rollout by keeping the current transient affine fast paths and explicitly rejecting a wider storage-boundary migration.

## Validation Performed

### Targeted tests

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~AffineMatrix4x3Tests|FullyQualifiedName~TransformAccessorFastPathTests|FullyQualifiedName~RenderableMeshBoundsTests"`

Result: 25/25 tests passed.

### Full solution build

- `dotnet build XRENGINE.slnx`

Result: build succeeded. The warning set remains pre-existing and is not specific to the affine rollout.

## Phase 4 Review

### 1. The transform fast paths remain justified

- The direct affine microbenchmarks still favor `AffineMatrix4x3` for local TRS construction, local-to-world multiplication, and point transforms.
- The runtime-facing hierarchy benchmarks still show the clearest wins at the wider hierarchy sizes that matter for transform propagation.
- Small hierarchy counts remain noisy, but there is no current evidence that the transform fast paths should be removed.

### 2. The Phase 3 bounds path should stay narrow

- The synthetic CPU bounds workload remains slower on the affine path in the current benchmark suite.
- The existing narrow bounds helpers are acceptable because they are correctness-covered and gated to affine-only paths, but the measurements do not support widening them into broader render-prep storage changes.

### 3. No storage-boundary change is justified

- The current wins come from transient affine intermediates, not from changing the stored engine-facing matrix shape.
- There is no benchmark evidence here that moving world/render caches, render-command payloads, GPU upload buffers, or other internal arrays to affine storage would pay for the churn.
- A wider storage migration would also expand validation scope into more rendering, interop, and tooling paths without a measured payoff.

### 4. Public and GPU boundaries should remain `Matrix4x4`

- The original rollout guardrails still hold.
- No public transform API change is justified.
- No GPU-facing layout change is justified.

## Decision

Complete the current affine rollout at the existing storage boundary:

- Keep the implemented Phase 1 and Phase 2 transform fast paths.
- Keep the current narrow Phase 3 bounds helpers.
- Do not migrate internal caches or arrays to affine storage in this rollout.
- Defer any future storage-model changes to a separate measured design proposal if future profiling shows a clear win.

## Remaining Follow-Up

- A manual editor rendering smoke check is still recommended for viewport-level confidence around culling and bounds-driven behavior.
- That follow-up does not change the Phase 4 storage decision, because the current benchmark data already rules out a broader affine storage migration.