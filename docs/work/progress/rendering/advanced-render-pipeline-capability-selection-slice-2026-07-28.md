# Advanced Render Pipeline Capability Selection Slice - 2026-07-28

Status: Complete
Parent TODO:
[01 - Pipeline Identity And Frame Contract](../../todo/rendering/architectural-refactor/01-pipeline-identity-and-frame-contract-todo.md)

Subsequent routing contract:
[Output-Purpose And Feature-Contract Slice](advanced-render-pipeline-output-purpose-and-feature-contract-slice-2026-07-28.md)

## Scope

Replace the temporary binary selector with a deterministic selection policy,
make backend requirements explicit, and publish one structured reason whenever
the advanced pipeline cannot be selected. This slice does not replace the
migration frame graph.

## Completed

- Added `Disabled`, `Available`, `Required`, and `Diagnostic` modes through
  `EngineSettings.AdvancedRenderPipelineMode` and
  `XRE_ADVANCED_RENDER_PIPELINE_MODE`.
- Added immutable capability, capability-result, selection-result, and
  machine-readable rejection contracts.
- Made integer visibility targets, compute, storage buffers, indirect
  submission, texture indirection, explicit synchronization, current/previous
  frame-slot storage, and stereo arrays explicit requirements.
- Kept buffer device address, descriptor indexing/heap, subgroup operations,
  mesh shaders, async compute, and timeline semaphores as optional
  acceleration signals.
- Added OpenGL and Vulkan capability snapshots with their selected backend
  encodings.
- Registered the application-owned selector with
  `RuntimeEngine.Rendering.NewRenderPipeline`, which is the shared factory used
  by desktop, stereo, OpenXR, light-probe, impostor, and offscreen-capture
  callers.
- Made `Required` throw
  `AdvancedRenderPipelineNotSupportedException` before constructing a pipeline
  when capability negotiation fails.
- Added capability details and rejection state to the render-pipeline inspector
  and render profiler protocol/UI.
- Added deterministic policy, rejection-order, mono/stereo, factory, identity,
  and profiler serialization tests.

## Selection Behavior

| Mode | Supported backend | Unsupported backend |
| --- | --- | --- |
| `Disabled` | Legacy default; capability probe skipped | Legacy default; capability probe skipped |
| `Available` | Advanced pipeline | Logged, observable legacy fallback |
| `Required` | Advanced pipeline | Pipeline creation rejected with structured exception |
| `Diagnostic` | Probe reported; legacy default retained | Rejection reported; legacy default retained |

This slice originally retained global RVC/debug precedence. The subsequent
output-purpose slice supersedes that routing: RVC owns OpenXR eye requests,
debug-opaque is desktop-only, and neither can capture the desktop/capture
standard selector implicitly.

## Architectural Boundary At This Slice

At completion of this slice, the capability report still described the renamed
migration composite. The subsequent frame-contract slice disconnected that
graph, removed the migration shader-family identity, and changed both production
backends to report `None` until the complete `VisibilityBuffer` family is
implemented.

No production-default cutover occurred. `Disabled` remains the default and
`DefaultRenderPipeline` remains the reference/fallback renderer.

## Validation

- Editor build: passed with 0 compiler errors. The only reported warnings were
  the pre-existing `Magick.NET` NuGet advisory.
- Unit-test project build: passed with 0 compiler errors and the same existing
  advisory.
- Unit-testing-world settings/schema generator: passed. It produced no tracked
  schema delta because the selector is a runtime rendering setting.
- Capability, identity, and profiler protocol tests: 23 passed.
- Runtime host, light-probe capture, RVC, OpenXR timing, render-capture,
  VR-view-mode, and Vulkan-upscale integration contracts: 167 passed.
- Render-notes and stereo-temporal documentation contracts: 59 passed.

## Worktree Exclusions

The following pre-existing workspace state was not modified as part of this
slice:

- `Build/Submodules/OscCore-NET9`
- `Build/Dependencies/vcpkg/`
- `Build/Submodules/Flyleaf/`
- `Build/Submodules/MagicPhysX/`

## Next Slice

Replace the copied frame graph with the named advanced-stage skeleton, keep
incomplete shader stages unavailable, and add command-tree/resource-layout
tests before implementing visibility-buffer resources.
