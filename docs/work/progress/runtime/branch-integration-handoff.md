# Branch Integration Handoff

Updated: 2026-10-09. The browser merge is resolved and built in the working tree. It is not committed.

Code work: [Branch integration TODO](../../todo/runtime/branch-integration-todo.md)
Checks: [Branch integration validation](../../testing/runtime/branch-integration-validation.md)

## Saved State

| Ref | State |
|---|---|
| `master` | `b89e427c4`. No physics or browser integration was promoted. |
| `branch-integration` | Local only. `ce54e3c9b` merges `physics-chain-gpu-covered-rendering` at `55375186c` into `master`. The working tree holds the resolved, uncommitted merge of `origin/browser-library-boundary` at `fb827fb5a` (`MERGE_HEAD` is set). |
| `physics-chain-gpu-covered-rendering` | `55375186c`; includes Unity prefab branch `52f011649`. |
| `browser-library-boundary` | `fb827fb5a`; includes WebGPU `ff0eabf95`. |

The first `branch-integration` (`0011f22c5`) existed only in another clone. It was recreated from the same inputs; the physics merge has no conflicts. Remote refs were refreshed over HTTPS on 2026-10-09; the SSH remote fails with a public-key error in this environment.

## Merge Resolution Summary

- Physics CPU scheduler: the browser host worker group is kept. The callback now receives a stable worker index, so each worker keeps its own counter slot. Faults are recorded per slot. `DesktopWorkerBackend.EnsureRegistered` installs the desktop group factory, so unit tests and RenderBench get it too.
- Physics world: HEAD's transition gates, outer-tick context, deferred transfers, and exact-handle ownership are kept. The browser disposal lifecycle is added inside them. Deferred owner disposal runs after the tick releases its gates. A focused concurrency review found further issues; the confirmed ones are fixed, and the rest are open items in the TODO. See [world disposal](../../../architecture/physics/physics-chain-world-runtime.md#world-disposal).
- Readback: HEAD's nonblocking admission and per-service lock are kept. Disposed worlds do not throw. In-flight staging fences are released on the render thread.
- Frame views: one camera mask, `CullingLayerMask`, plus the browser `CameraOrthographicSize`. The Vulkan OpenXR producer now sets the mask from the eye camera.
- GPU scene: bounds ownership and route publication read the accepted command snapshot renderer. The command snapshot keeps the pass flags (ours) and the render options, meshlet-culling, and LOD fields (theirs).
- Advanced publisher and deformation: both sides' fields are kept. The material-plan reuse path now carries the browser companion records and binding counts. A mesh without skinning is no longer prepared again every frame.

## Resume Order

1. Get user decisions on the TODO items that need clearance (the test-fixture change and the commit).
2. Commit the merge on `branch-integration`. Exclude `Samples/MonkeyBallVR/` (old local build output that the browser `.gitignore` no longer ignores), `Build/Dependencies/vcpkg/`, and the untracked submodule folders.
3. Close the open code items, then finish the open checks in the validation document.
4. Promote to `master` only after desktop validation.
5. Review remaining features and dependency updates separately. Recheck remote existence before acting.

## Branch Cleanup

The following branches are absent locally and on GitHub: `codex/runtime-modularization-phase5`, `codex/runtime-modularization-phase6`, `vulkan-refactor`, `engine-validation`, and `fix/s08-index-preparation-before-draw-admission`. All five tips were confirmed as ancestors of the starting master. The S08 worktree was clean and is retained with detached HEAD at `2ca9fe209`. In the clone used on 2026-10-09, stale local branches `vulkan-refactor`, `codex/runtime-modularization-phase6`, and `fix/s08-index-preparation-before-draw-admission` still exist; their remotes are gone. They were not deleted.

Git initially selected `david-intrepidautomation`, which had no write access. The user selected `BlackJaxDev`; the explicit HTTPS username worked. Two remote refs were already absent when the deletion retried. The remaining three were deleted successfully. A subsequent fetch pruned remote-tracking refs that were already absent on GitHub; it did not delete those remote branches. No global Git account setting was changed.

The untracked `Build/Dependencies/vcpkg/` directory and existing submodule working changes were retained. Do not include them in integration commits without review.

## Remaining Branch Review

- The old `codex/s08-nonblocking-index-preparation` commit `8ac513639` changes immutable index capture and Vulkan draw admission. The initial inventory did not establish whether all behavior is superseded. Review `XRMesh.Geometry.cs` and `VkMeshRenderer.Preparation.cs` against the current code before any merge.
- Octahedral capture commit `1a72344d8` changes existing capture code substantially. The feature already exists; unique fixes still need review. Do not assume it is a duplicate.
- `origin/deploy` at `af4d9bde4` adds `.github/workflows/dispatch-deployment.yml`. It dispatches a deployment event using configured repository and token secrets. Keep it separate from feature integration and from the existing validation/promotion workflow.
- The first inventory found several overlapping CodeQL updates. Recheck current remote refs after pruning; do not merge each historical update.
- Dependency and submodule updates require their own review under `AGENTS.md`. No dependency update was approved or applied in this integration.

The conflict guidance is a partial static review of `55375186c` and `fb827fb5a`. It is not an implementation or runtime acceptance result.
