# Branch Integration Handoff

Updated: 2026-10-09. Work is paused at the user's request.

Code work: [Branch integration TODO](../../todo/runtime/branch-integration-todo.md)
Checks: [Branch integration validation](../../testing/runtime/branch-integration-validation.md)

## Saved State

| Ref | State |
|---|---|
| `master` | Base `41eb554e3`, followed by the requested branch naming rule and this handoff. No physics or browser integration was promoted. |
| `branch-integration` | `0011f22c5`: master base, naming-rule commit `17613a8fa`, and the physics merge. Retained locally. |
| `physics-chain-gpu-covered-rendering` | `55375186c`; includes Unity prefab branch `52f011649`. Already merged into the integration branch. |
| `browser-library-boundary` | Candidate `fb827fb5a`; includes WebGPU `ff0eabf95`. Not merged into the integration branch. |
| Local `codex/webgpu-readiness-audit` | Stale local tip `11ef1f646`. Do not use this as the current WebGPU candidate. |

The normal Editor build at `0011f22c5` passed with zero warnings and zero errors. The isolated `integration-physics` build was interrupted and the session was marked stopped. No live runtime acceptance or integration tests completed. No merge conflict resolution was started. No broker worker run was started.

## Branch Cleanup

The following branches are absent locally and on GitHub: `codex/runtime-modularization-phase5`, `codex/runtime-modularization-phase6`, `vulkan-refactor`, `engine-validation`, and `fix/s08-index-preparation-before-draw-admission`. All five tips were confirmed as ancestors of the starting master. The S08 worktree was clean and is retained with detached HEAD at `2ca9fe209`.

Git initially selected `david-intrepidautomation`, which had no write access. The user selected `BlackJaxDev`; the explicit HTTPS username worked. Two remote refs were already absent when the deletion retried. The remaining three were deleted successfully. A subsequent fetch pruned remote-tracking refs that were already absent on GitHub; it did not delete those remote branches. No global Git account setting was changed.

The untracked `Build/Dependencies/vcpkg/` directory and existing submodule working changes were retained. Do not include them in integration commits without review.

## Resume Order

1. Refresh refs and inspect both working trees. Preserve newer local work. Resume `branch-integration`; do not repeat its physics merge.
2. Bring forward the master naming rule and handoff as needed. The equivalent naming rule already exists on both branches.
3. Complete the physics desktop baseline in the linked validation document.
4. Merge the refreshed browser-library candidate. Resolve conflicts by contract, using the linked code TODO. The preview at the pinned tips found 28 conflicting files; a clean merge of either candidate into the old master does not imply they merge cleanly with each other.
5. Complete combined builds, focused tests, and live desktop checks. Keep unfinished browser work documented. Promote to master only after desktop validation.
6. Review remaining features and dependency updates separately. Recheck remote existence before acting. Remove redundant branches only after their work is retained.

## Remaining Branch Review

- The old `codex/s08-nonblocking-index-preparation` commit `8ac513639` changes immutable index capture and Vulkan draw admission. The initial inventory did not establish whether all behavior is superseded. Review `XRMesh.Geometry.cs` and `VkMeshRenderer.Preparation.cs` against the current code before any merge.
- Octahedral capture commit `1a72344d8` changes existing capture code substantially. The feature already exists; unique fixes still need review. Do not assume it is a duplicate.
- `origin/deploy` at `af4d9bde4` adds `.github/workflows/dispatch-deployment.yml`. It dispatches a deployment event using configured repository and token secrets. Keep it separate from feature integration and from the existing validation/promotion workflow.
- The first inventory found several overlapping CodeQL updates. Recheck current remote refs after pruning; do not merge each historical update.
- Dependency and submodule updates require their own review under `AGENTS.md`. No dependency update was approved or applied in this integration.

The conflict guidance is a partial static review of `55375186c` and `fb827fb5a`. It is not an implementation or runtime acceptance result.
