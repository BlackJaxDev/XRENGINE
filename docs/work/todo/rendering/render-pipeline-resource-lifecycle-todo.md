# Render Pipeline Resource Lifecycle Finalization TODO

Last Updated: 2026-07-10
Owner: Rendering
Status: Active - phases 0–5 complete; hardening, validation, and closeout pending
Execution: current worktree (branch creation explicitly declined by user)

Design and architecture sources:

- [Render Pipeline Resource Lifecycle Design](../../design/rendering/render-pipeline-resource-lifecycle-design.md)
- [Render Pipeline Resource Lifecycle Architecture](../../../architecture/rendering/render-pipeline-resource-lifecycle.md)
- [DefaultRenderPipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Frame Lifecycle And Dispatch Paths](../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)

## Why This TODO Is Active Again

The first resource-lifecycle milestone was completed in June 2026. It added
declarative resource layouts, active/pending/retired generations, atomic
generation commit, staged resize, and the shared OpenGL/Vulkan descriptor
contract. That work successfully moved the stable core of
`DefaultRenderPipeline` out of command-time allocation.

The broader refactor was not completed. Executable command chains still contain
`VPRC_CacheOrCreateTexture`, `VPRC_CacheOrCreateFBO`,
`VPRC_CacheOrCreateBuffer`, and `VPRC_CacheOrCreateRenderBuffer`. These commands
still inspect and mutate the live resource registry during frame execution and
can resize, destroy, or create physical resources from the hot path.

The previous TODO was moved to `COMPLETED/` for the scoped core milestone. It is
active again because the intended v1 architecture is not reached until pipeline
resource ownership is fully declarative and cache-or-create commands no longer
participate in executable render command lists.

## Implemented Baseline

- `XRRenderPipeline.DescribeResources(...)` builds an immutable resource layout
  for an effective pipeline profile.
- `XRRenderPipelineInstance` owns active, pending, retired, and failed resource
  generations.
- Pending generations materialize and validate before atomically replacing the
  active generation.
- Resize and relevant settings changes request replacement generations instead
  of emptying the active registry.
- Texture, texture-view, renderbuffer, buffer, and framebuffer specs lower into
  the shared registry and Vulkan planner descriptor contract.
- `DefaultRenderPipeline` declares its stable core graph, post-process,
  temporal, transparency, bloom, and anti-aliasing resources.
- Vulkan planning can consume committed declared layouts before command
  execution.
- Focused lifecycle tests cover layout construction, generation transitions,
  validation failures, and representative default-pipeline resource coverage.

## Current Gaps

The remaining cache-command sites are not all equivalent. The implementation
must classify each one before changing it:

- Some are genuinely branch-local resources that were deliberately deferred,
  including atmosphere, volumetric fog, exact-transparency scratch targets,
  experimental GI outputs, and debug visualization targets.
- Some are compatibility checks for resources now declared by the layout and
  should be deleted rather than migrated again.
- Some framebuffer commands exist because their attachments are selected by a
  runtime branch or replaced by another feature path.
- `DefaultRenderPipeline2`, `UserInterfaceRenderPipeline`,
  `SurfelDebugRenderPipeline`, and `TestRenderPipeline` still author resources
  through cache commands.
- `DefaultRenderPipeline` retains `GenerateCommandChainLegacy()` and other
  legacy authoring code, obscuring which cache-command sites are executable.
- The cache command classes still perform per-frame lookup, recreation checks,
  resize callbacks, descriptor registration, destruction, and creation.
- Serialized test assets still name cache-command types, so command deletion
  requires an intentional serialization cleanup.
- Some lifecycle validation and Vulkan pending-allocation diagnostics from the
  original milestone remain incomplete.

The Phase 0 execution-aware inventory and Phase 1–2 implementation record are
in [the 2026-07-10 progress note](../../progress/rendering/render-pipeline-resource-lifecycle-finalization-2026-07-10.md).
Phase 3–5 subsequently migrated the remaining 138 authoring expressions: 121
in `Default2`, 11 in `SurfelDebugRenderPipeline`, 3 in
`UserInterfaceRenderPipeline`, and 3 in `TestRenderPipeline`. The final active
site count is zero and the command types have been removed.

## Target Architecture

1. Every pipeline-owned GPU resource is declared before command execution.
2. Feature predicates and profile keys select a complete resource layout.
3. A generation materializes, validates, and commits all selected resources.
4. Command chains only bind, clear, render, dispatch, copy, resolve, synchronize,
   read back, and present resources.
5. Frame execution does not create, resize, destroy, or register pipeline-owned
   textures, views, renderbuffers, buffers, or framebuffers.
6. Runtime feature or size changes request a replacement generation; they do not
   mutate the active generation.
7. External resources such as window, scene-capture, and XR swapchain targets
   are imported explicitly and are not disguised as pipeline-owned resources.
8. Old generations remain alive until all API-specific in-flight use has ended.

## Completion Criteria

- No executable render command chain contains a `VPRC_CacheOrCreate*` command.
- All production pipeline-owned resources have a layout spec or an explicitly
  documented external/imported-resource contract.
- Cache-or-create command classes and their script/serialization registrations
  are removed.
- Resource creation, resizing, destruction, and descriptor mutation are absent
  from steady-state frame command execution.
- Feature toggles, camera overrides, resize, HDR, AA/MSAA, stereo, scene capture,
  and XR target changes produce complete generation keys and layouts.
- OpenGL and Vulkan pass repeated resize and feature-toggle validation without
  black frames, missing-resource warnings, stale attachments, or device loss.
- Focused tests and source-contract tests prevent cache commands or live-registry
  mutation from returning to command execution.
- Architecture documentation describes the finalized system without migration
  exceptions that no longer exist.

## Phase 0 - Branch, Baseline, And Inventory

- [x] Branch creation intentionally skipped at the user's explicit request.
- [x] Record the starting commit and dirty-worktree exclusions in this TODO or a
  linked progress note.
- [x] Build an execution-path-aware inventory of every
  `VPRC_CacheOrCreateTexture`, `VPRC_CacheOrCreateFBO`,
  `VPRC_CacheOrCreateBuffer`, and `VPRC_CacheOrCreateRenderBuffer` site.
- [x] Separate active command-chain sites from `GenerateCommandChainLegacy()`,
  dead helpers, comments, serialization fixtures, and command-class tests.
- [x] For every active site, record the owner pipeline, resource name, resource
  kind, size policy, format, sample/layer/mip policy, dependencies, lifetime,
  feature predicate, and current invalidation trigger.
- [x] Classify each resource as pipeline-owned persistent, pipeline-owned
  transient, history, external/imported, or command-local CPU helper.
- [x] Identify command sites whose names are already present in the declared
  layout and can be removed without adding a second declaration.
- [x] Capture baseline resource churn, generation replacement, missing-resource,
  Vulkan validation, and resize behavior for the default editor viewport.
- [x] Run the focused lifecycle tests and record existing failures separately
  from regressions introduced by this work.

Acceptance criteria:

- [x] Every active cache command has exactly one planned disposition: delete as
  redundant, migrate to a spec, replace with an imported resource, or remove
  with an obsolete pipeline/path.
- [x] The inventory distinguishes source occurrences from commands reachable in
  an executable command chain.

## Phase 1 - Complete The Declaration Model

- [x] Confirm that resource predicates can express all remaining feature,
  camera, viewport, stereo, capture-policy, and backend distinctions without
  consulting mutable state during materialization.
- [x] Extend `RenderPipelineResourceProfile` and generation keys for any
  remaining layout-affecting inputs; do not hide layout changes in factories.
- [x] Make runtime camera/viewport overrides resolve to an effective immutable
  profile before layout construction.
- [x] Add an explicit imported-resource specification or binding contract for
  window targets, caller-provided scene-capture FBOs, OpenXR/OpenVR swapchain
  images, and other resources not owned by a pipeline generation.
- [x] Define how mutually exclusive attachment choices are represented: profile
  variants, optional specs, attachment aliases, or separate declared FBOs.
- [x] Finish or remove `QuadMaterialSpec`; command-local materials may remain CPU
  helpers only if they do not own or mutate pipeline GPU resources.
- [x] Define declared transient lifetime and aliasing semantics consistently for
  OpenGL and Vulkan.
- [x] Ensure history resources declare initialization, preservation, reset, and
  first-frame behavior explicitly.
- [x] Reject factories whose concrete output contradicts declared type, size,
  format, samples, layers, mips, usage, or ownership.

Acceptance criteria:

- [x] Every remaining resource in the Phase 0 inventory can be expressed without
  a command-time create/resize callback.
- [x] Layout-affecting mutable state is represented in the generation key.
- [x] External targets have explicit ownership and synchronization rules.

## Phase 2 - Finish `DefaultRenderPipeline`

- [x] Remove `GenerateCommandChainLegacy()` and helpers used only by the legacy
  chain after confirming no tooling or serialization path invokes them.
- [x] Delete redundant cache commands for resources already declared in
  `DefaultRenderPipeline.Resources.cs`.
- [x] Declare forward-MSAA depth/stencil resources and their texture views/FBOs.
- [x] Declare post-process FBOs whose attachment dependencies are now stable.
- [x] Declare atmosphere full/half-resolution textures, history, quad FBOs, and
  destination FBOs behind an effective atmosphere feature predicate.
- [x] Declare volumetric-fog full/half-resolution textures, history, quad FBOs,
  and destination FBOs behind an effective fog feature predicate.
- [x] Declare exact-transparency PPLL, depth-peeling, scratch, counter, buffer,
  history, resolve, and debug resources.
- [x] Declare experimental GI outputs for ReSTIR GI, light volumes, radiance
  cascades, surfel GI, and voxel-cone tracing with explicit feature predicates.
- [x] Declare overdraw and other debug-visualization resources without making
  debug modes mutate the active registry.
- [x] Finish bloom, AO, SMAA, FXAA, TSR, motion-blur, depth-of-field, temporal,
  and final-output coverage; remove any compatibility command that merely
  revalidates an already-declared resource.
- [x] Replace AO-dependent light-combine and other runtime attachment rebuilding
  with declared profile variants or stable declared attachment aliases.
- [x] Ensure optional branches bind only resources present in the effective
  layout and never create their resources on first execution.
- [x] Remove `CacheTextures(...)`, `AddConditionalTextureCache(...)`, and all
  `Append*ResourceCaching(...)` helpers after their last caller is migrated.
- [x] Audit every default-pipeline factory and resize callback for direct active
  registry mutation; route all legitimate materialization through the pending
  generation build context.

Acceptance criteria:

- [x] The active `DefaultRenderPipeline` command tree contains zero
  `VPRC_CacheOrCreate*` commands for every supported profile.
- [x] Enabling a previously inactive feature requests or selects a complete
  generation before that feature's passes execute.
- [x] Default-pipeline command execution cannot resize or recreate a resource.

## Phase 3 - Resolve `DefaultRenderPipeline2`

- [x] Retain `DefaultRenderPipeline2` as the cleaned-up, better-organized
  successor pipeline; it provides the intended production direction.
- [x] Record deletion of V2 as not applicable because the retained direction is
  explicit.
- [x] Give it a complete `DescribeResources(...)` implementation
  and migrate every active cache command to declared specs.
- [x] Keep V2's command-chain organization independent; record declaration
  deduplication as a later V2 cleanup rather than coupling it to V1 execution.
  pipeline helpers without coupling command execution to mutable state.
- [x] Update pipeline selection and serialization behavior for the chosen
  outcome; v1 does not require preserving the legacy type.

Acceptance criteria:

- [x] `DefaultRenderPipeline2` is either removed or contains no executable cache
  commands.
- [x] There is one clearly documented production default-pipeline direction.

## Phase 4 - Migrate The Remaining Pipelines

- [x] Add complete declared layouts to `UserInterfaceRenderPipeline`.
- [x] Add complete declared layouts to `SurfelDebugRenderPipeline`, including
  debug-only textures and FBOs.
- [x] Convert `TestRenderPipeline` to declared resources or replace it with a
  purpose-built test fixture that exercises resource layouts directly.
- [x] Audit `RvcRenderPipeline` overrides for resources created or replaced
  outside its inherited/extended layout.
- [x] Audit `CustomRenderPipeline`, `DebugOpaqueRenderPipeline`, and all pipeline
  subclasses for direct registry writes even when they do not use cache
  commands.
- [x] Document the authoring contract for new custom pipelines and provide a
  minimal declared-resource example.

Acceptance criteria:

- [x] Every repository pipeline either declares its owned GPU resources or is a
  documented resource-free/import-only pipeline.
- [ ] No pipeline subclass mutates the active registry during frame execution.

## Phase 5 - Remove The Compatibility Layer

- [x] Remove `VPRC_CacheOrCreateTexture`.
- [x] Remove `VPRC_CacheOrCreateFBO`.
- [x] Remove `VPRC_CacheOrCreateBuffer`.
- [x] Remove `VPRC_CacheOrCreateRenderBuffer`.
- [x] Remove command-specific descriptor inference, size-policy reflection,
  churn diagnostics, generation-stamp compatibility, and no-op compatibility
  tests that are no longer reachable.
- [x] Replace serialized fixtures/assets that name removed command types; add a
  clear load error or one-time development migration only if an in-repository
  asset still requires it.
- [x] Remove obsolete cache-command APIs from the pipeline script compiler and
  generated command documentation.
- [x] Add a source-contract test that fails when a render command creates,
  resizes, destroys, or registers a pipeline-owned GPU resource.
- [x] Add a source-contract test that fails if a removed cache-command type or
  `Add<VPRC_CacheOrCreate` authoring expression returns.

Acceptance criteria:

- [x] The retired command-family name returns only historical documentation.
- [x] Runtime and test assemblies contain no cache-or-create command types.
- [x] Command-list execution has no resource-lifecycle compatibility path.

## Phase 6 - Harden Generation And Resize Semantics

- [ ] Complete the audit for direct active-registry mutation from resize,
  settings, resource factories, feature setup, readback, and capture paths.
- [ ] Ensure internal size, output size, stereo layers, HDR, AA/MSAA, capture
  policy, and imported-target identity participate in the correct generation
  key without causing unrelated churn.
- [ ] Finish pending Vulkan image, buffer, view, and framebuffer allocation
  without destroying or invalidating the active physical plan.
- [ ] Validate logical generation and Vulkan physical-plan commit as one
  failure-safe transaction.
- [ ] Add diagnostics for missing declared resources, stale descriptors,
  attachment-generation mismatch, imported-resource mismatch, and old
  generation lifetime after commit.
- [ ] Remove routine `DeviceWaitIdle` from resize/recreation after fence-based
  retirement covers all old resources.
- [ ] Bound retired-generation and pending-generation buildup during rapid
  resize, feature toggling, capture, and device-loss recovery.
- [ ] Ensure failed or superseded pending generations destroy all partial
  logical and backend resources without touching the active generation.
- [ ] Verify planner/readback scopes always select the generation and imported
  target used by the rendered frame.

Acceptance criteria:

- [ ] Resize and feature changes never expose a partial generation.
- [ ] Failure to build a replacement leaves the active generation renderable and
  produces actionable diagnostics.
- [ ] Retirement is fence-driven and bounded on both OpenGL and Vulkan paths.

## Phase 7 - Tests And Static Enforcement

- [ ] Add layout-coverage tests for every retained pipeline and every feature
  profile with owned resources.
- [ ] Add tests for atmosphere, fog, exact transparency, debug views,
  experimental GI, forward MSAA, bloom, AO, SMAA, FXAA, TSR, temporal history,
  motion blur, depth of field, and final presentation dependencies.
- [ ] Add tests proving feature toggles change generation keys only when their
  resource layout changes.
- [ ] Add tests for camera and viewport overrides that affect resource profiles.
- [ ] Add tests for imported window, scene-capture, and XR targets and ownership
  boundaries.
- [ ] Add tests for rapid resize coalescing, stale pending generations, failed
  materialization, failed backend allocation, atomic commit, and retirement.
- [ ] Add descriptor/layout parity tests only where two contracts intentionally
  coexist; remove migration-era parity diagnostics after compatibility
  authoring is gone.
- [ ] Add static tests that enumerate command trees and assert no resource
  allocation commands or active-registry mutations are reachable.
- [ ] Add an allocation audit for steady-state command generation/execution and
  resource-plan lookup.

Acceptance criteria:

- [ ] Tests fail on undeclared resource consumption, dependency/order errors,
  attachment identity errors, or reintroduced command-time allocation.
- [ ] The focused suite is deterministic and does not require a visual editor
  session.

## Phase 8 - Editor, OpenGL, Vulkan, And XR Validation

- [ ] Create a bounded `Build/_AgentValidation/<run>/` root for each validation
  pass and record exact logs/captures in a progress or investigation note.
- [ ] Build the editor and run the focused resource lifecycle/unit tests.
- [ ] Run the Unit Testing World under OpenGL and Vulkan with MCP enabled.
- [ ] Continuously resize the scene panel and main window; inspect screenshots
  throughout the drag for black, stale, or partially rebuilt frames.
- [ ] Toggle internal resolution, HDR, AA modes, MSAA samples, bloom, AO,
  atmosphere, fog, transparency modes, temporal effects, and debug views while
  rendering.
- [ ] Validate mono, stereo, scene-capture, light-probe, OpenVR, and OpenXR
  resource profiles applicable to available hardware/runtime support.
- [ ] Capture at least two camera positions for visual failures to distinguish
  scene rendering from stale-resource sampling.
- [ ] Inspect OpenGL, Vulkan, rendering, profiler, and resource-generation logs
  after each run; separate steady-state failures from shutdown-only noise.
- [ ] Use RenderDoc when screenshots/logs cannot identify a stale attachment,
  descriptor, layout, or target-selection error.
- [ ] Run a long resize/feature-toggle session and confirm physical resource,
  descriptor pool, generation, and retirement counts remain bounded.
- [ ] Confirm no routine resource churn occurs after a profile reaches steady
  state.
- [ ] Confirm no missing-declared-resource warnings, skipped presentation,
  Vulkan validation errors, device loss, OOM, or silent CPU fallback.

Acceptance criteria:

- [ ] OpenGL and Vulkan complete repeated resize and feature changes without
  black frames or resource-lifecycle errors.
- [ ] Steady-state command execution reports zero pipeline resource creates,
  resizes, recreates, or destroys.
- [ ] Validation evidence is recorded outside ignored scratch data in a durable
  progress or investigation note.

## Phase 9 - Documentation And Closeout

- [ ] Update
  `docs/architecture/rendering/render-pipeline-resource-lifecycle.md` to remove
  first-milestone compatibility exceptions and describe the final ownership
  model.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` with
  the finalized feature/profile/resource mapping.
- [ ] Update custom pipeline authoring documentation with declaration,
  generation-key, external-resource, history, and resize examples.
- [ ] Update Vulkan and frame-lifecycle docs for atomic logical/physical commit
  and fence-driven retirement.
- [ ] Remove stale references that describe cache commands as supported pipeline
  authoring tools.
- [ ] Record final command-site count, resource-layout coverage, test results,
  editor validation, known limitations, and follow-ups.
- [ ] Move this TODO back to `docs/work/todo/COMPLETED/` only after every
  completion criterion is satisfied.
- [ ] Prepare PR notes covering what changed, why, validation, risks, and any
  intentionally deferred non-resource-lifecycle work.
- [ ] Merge branch `rendering/finalize-pipeline-resource-lifecycle` back into
  `main` after implementation, validation, documentation, and review are
  complete.

Acceptance criteria:

- [ ] Documentation names no production cache-command compatibility path.
- [ ] The TODO is not marked complete while executable cache commands or
  unchecked completion criteria remain.

## Explicit Non-Goals

- Rewriting mesh submission, pass ordering, or synchronization into a new frame
  graph beyond what resource declaration requires.
- Moving ordinary render, dispatch, copy, resolve, readback, or presentation
  work out of command lists.
- Hiding failed GPU resource creation behind CPU fallbacks.
- Preserving obsolete pre-v1 pipeline APIs or serialized cache-command authoring
  when deletion yields a cleaner v1 architecture.
