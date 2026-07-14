# Render Pipeline Resource Lifecycle Finalization Progress

Date: 2026-07-10  
Starting commit: `6c642c270899b1c0d86db57497ae3a8db0b96c57`  
Scope: TODO phases 0–2

## Working Agreement And Baseline

The TODO's branch-creation step was intentionally skipped at the user's
explicit request. Work was performed on the existing worktree. Pre-existing
changes outside the render-resource lifecycle files were excluded, including
the dependency/submodule work, OpenXR/Vulkan validation scripts, Vulkan device
bootstrap, and unrelated documentation/tests visible in `git status`.

The focused lifecycle baseline had four behavior failures: command-time cache
creation required a current pipeline, deferred G-buffer recreation depended on
live-registry mutation, and post-process output creation happened in the hot
path. Three additional failures were test source-path assumptions caused by the
split rendering project. Logs are under
`Build/_AgentValidation/20260710-render-resource-finalization/logs/`.

The most recent accepted Vulkan editor evidence predating implementation is
recorded in `vulkan-framerate-preview-2026-07-08.md`: zero missing declared
resources, nine planned generation replacements, and no Vulkan validation
errors. This is the Phase 0 churn/validation baseline; live runtime validation
remains a later validation-phase task.

## Execution-Aware Cache Command Inventory

The inventory counts authoring expressions, not command instances after
conditional-container expansion. Comments are excluded. Factories carry the
listed concrete format/attachment details; all active sites currently use
viewport/internal-size invalidation unless noted.

| Executable owner/path | Active sites | Resource contract | Classification | Planned disposition |
| --- | ---: | --- | --- | --- |
| `DefaultRenderPipeline` | 0 (104 at baseline) | Core, MSAA, prepass, post-process, AO/bloom/AA/temporal, atmosphere/fog, exact transparency, GI, debug, and fullscreen-quad helpers | Persistent, transient, history, or explicit external as declared in `DefaultRenderPipeline.Resources.cs` | Completed in Phase 2: redundant sites deleted and remaining resources migrated to specs |
| `DefaultRenderPipeline2.CommandChain.cs` | 112 | Internal/output-sized color and depth textures; depth views; MSAA targets; bloom/AO/AA/temporal/atmosphere/fog/GI/debug textures; attachment FBOs and attachmentless quad helpers. Samples/layers/mips and formats remain encoded by the named factories | Pipeline-owned persistent/transient/history | Remove with obsolete fork in Phase 3 after unique behavior is transferred; do not preserve its command-time lifecycle |
| `DefaultRenderPipeline2.ExactTransparency.cs` | 9 | PPLL head/counter/storage buffers, depth-peel textures, resolve/debug targets and FBOs; internal size, storage/attachment usage | Pipeline-owned transient/history | Remove with obsolete fork in Phase 3; the retained Default pipeline now has declared equivalents |
| `SurfelDebugRenderPipeline.cs` | 11 | Six core internal-size G-buffer/HDR textures, optional compute-debug output, and four internal-size FBO/quad helpers; factory formats and attachments are authoritative | Pipeline-owned persistent plus optional debug transient | Migrate to texture/FBO/quad specs in Phase 4 |
| `UserInterfaceRenderPipeline.cs` | 3 | `DepthStencil`, depth view, stencil view; internal size, `Depth24Stencil8`, one mip/layer/sample, depth/stencil aspect dependencies | Pipeline-owned persistent views | Migrate to texture and texture-view specs in Phase 4 |
| `TestRenderPipeline.cs` | 3 | Internal-size depth/stencil and HDR color textures plus the dependent internal-resolution FBO | Test-owned persistent | Replace with a declared-resource fixture in Phase 4 |

This accounts for all 138 active `Add<VPRC_CacheOrCreate*>` authoring
expressions remaining after Phase 2 (121 Default2, 11 surfel, 3 UI, 3 test).
The Default2 line inventory includes one commented FBO and four commented
texture authorings that are not included above. Cache command-class tests and
serialized fixtures are non-executable compatibility coverage and are assigned
to Phase 5 deletion. The deleted `GenerateCommandChainLegacy()` and its helper
tree were source-only duplicate authoring and never receive a second migration.

For all active entries, the named factory is the source of the exact format,
sample/layer/mip policy and attachment graph until its owning phase translates
that contract into specs. Registry resize predicates are the current
invalidation trigger. No entry is command-local CPU state, and none is an
external target. Window, caller scene-capture, and XR swapchain outputs are now
represented separately by `ExternalResourceSpec` with explicit ownership and
synchronization.

## Phase 1 Result

- `RenderPipelineResourceProfile`/`ResourceGenerationKey` now include the
  effective external-target kind alongside the existing dimensions, stereo,
  capture policy, AA, HDR/backend, and feature mask.
- The instance resolves window, caller-provided FBO, and external swapchain
  targets before layout construction.
- `ExternalResourceSpec` records kind, ownership, synchronization, and external
  lifetime and is never materialized or destroyed by the generation manager.
- `QuadMaterialSpec` now has a factory and is generation-owned while remaining
  attachmentless; existing bind/render commands see it through the FBO registry.
- Optional predicates select complete layouts. Texture/FBO descriptors retain
  transient/aliasing and history metadata for both backends, and materialization
  validation rejects required missing/wrong-kind resources.

## Phase 2 Result

`DefaultRenderPipeline` now has zero cache-or-create authoring or execution
sites. Its legacy command chain and caching helpers were removed. All remaining
MSAA, forward-prepass, post-process/final, atmosphere, fog, exact-transparency,
experimental GI, debug, motion-blur, depth-of-field and quad-material resources
are declared behind profile predicates. Factories resolve declared dependencies
from the pending generation context and fail visibly when a dependency is
missing instead of mutating the active registry.

## Validation

- Rendering/runtime/editor/unit-test projects compiled as dependencies of the
  focused test run.
- `RenderPipelineResourceLifecycleTests` plus the Default command-tree source
  contract: 40 passed, 0 failed.
- Static scan: zero cache-or-create commands or retired caching/legacy helpers
  under `Pipelines/Types/Default`.
- Scoped `git diff --check`: clean. Existing NuGet vulnerability warnings for
  `Magick.NET-Q16-HDRI-AnyCPU` remain unrelated to this work.

## V2 And Compatibility-Layer Completion

After the initial Phase 0–2 pass, the retained `DefaultRenderPipeline2` was
brought into scope. V2 now has a complete profile-selected declaration catalog
while preserving its independent `Append*` organization and GPU annotations.
UI, surfel debug, and test pipelines also declare their owned resources.

All 138 remaining active cache-command authoring expressions were removed. The
four cache-or-create command classes and their migration-only tests were
deleted, serialized pipeline fixtures now use supported execution commands, and
SMAA consumes declared edge/blend/output resources without an allocation
fallback. Final active command-site count: zero.

Validation after this extension:

- `XREngine.Runtime.Rendering` build: succeeded, 0 compiler errors.
- `RenderPipelineResourceLifecycleTests`: 44 passed, 0 failed.
- Both updated serialization fixtures pass when run independently; one combined
  run also exposed the existing Windows metadata-file cleanup race.
- The broader `AlphaToCoveragePhase2Tests` suite exposes 18 pre-existing stale
  source-contract/default-data failures once its migrated-path resolver works;
  these are not lifecycle regressions.
