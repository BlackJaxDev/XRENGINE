# Render Pipeline Resource Lifecycle TODO

Last Updated: 2026-06-11
Owner: Rendering
Status: Proposed
Target Branch: `rendering-resource-lifecycle`

Design source:

- [Render Pipeline Resource Lifecycle Design](../../design/rendering/render-pipeline-resource-lifecycle-design.md)
- [DefaultRenderPipeline notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
- [GPU Mesh BVH](../../../architecture/rendering/gpu-mesh-bvh.md)

## Goal

Move `DefaultRenderPipeline` core graph resources from frame-command cache
commands into declared pipeline resource layouts and generation-owned resource
registries. Resize should prepare a pending generation, keep the active
generation rendering until the pending one is complete, then swap generations
atomically and retire old resources after GPU use has finished.

The first production milestone should deliver:

- declared resource specs for `DefaultRenderPipeline` core textures, buffers,
  renderbuffers, texture views, and FBOs,
- descriptor parity diagnostics against the existing cache-command path,
- pre-execution materialization of declared resources,
- active/pending/retired `RenderResourceGeneration` ownership,
- staged internal-size and display-size resize without emptying the active
  registry,
- visible diagnostics when pending generation preparation fails.

## Scope

- `DefaultRenderPipeline` mono desktop path.
- OpenGL correctness first, with API shapes that support Vulkan prepare/swap.
- Existing cache commands remain as migration compatibility for unmigrated
  resources.
- Resource specs lower into the existing `*ResourceDescriptor` records used by
  `RenderResourceRegistry` and `VulkanResourcePlanner`.
- Resize covers both internal-resolution resize and display-region resize.
- Generation commit bumps the existing `XRRenderPipelineInstance.ResourceGeneration`
  integer stamp for cache-command compatibility.

## Non-Goals

- Do not migrate `DefaultRenderPipeline2` in this work item.
- Do not integrate XR/stereo swapchain images or per-eye targets; reserve key
  space for that follow-up.
- Do not rewrite the whole render graph.
- Do not remove command containers, pass metadata, mesh render commands, or
  render-graph synchronization.
- Do not hide explicitly requested GPU paths behind CPU fallback.
- Do not require every dynamic feature resource to be declared in the first
  migration step.

## Relevant Files

- `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs`
- `XREngine.Runtime.Rendering/Rendering/Resources/RenderResourceRegistry.cs`
- `XREngine.Runtime.Rendering/Rendering/Resources/RenderResourceDescriptorFactory.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_CacheOrCreateTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_CacheOrCreateFBO.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_CacheOrCreateBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_CacheOrCreateRenderBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline*.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RenderPipelineAntiAliasingResources.cs`
- `XREngine.Runtime.Rendering/Rendering/XRViewport.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanResourcePlanner.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanResourceAllocator.cs`
- `docs/architecture/rendering/default-render-pipeline-notes.md`

## Invariants

- A committed generation contains every required resource for its effective
  profile.
- Active generation resources are never destroyed while they can still be
  presented or referenced by in-flight GPU work.
- Resize never empties the active registry.
- Pending generation failure keeps the active generation rendering.
- FBO attachments reference resources from the same generation.
- Texture views reference source textures from the same generation.
- Command containers allocate execution helpers against the committed
  generation.
- Core pipeline resources are visible before frame execution begins.
- Vulkan planning consumes the declared layout rather than command side effects.
- Cache commands are transitional compatibility tools, not the final resource
  architecture.
- New code must not introduce per-frame heap allocations in render hot paths.

## Phase 0 - Branch, Baseline, And Contracts

- [ ] Create dedicated branch `rendering-resource-lifecycle`.
- [ ] Inventory current `DefaultRenderPipeline` cache commands by resource name,
  kind, size policy, format, sample count, attachment set, and feature predicate.
- [ ] Record the current internal-resolution resize path:
  `XRViewport.SetInternalResolution(...) -> XRRenderPipelineInstance.InternalResolutionResized(...) -> InvalidatePhysicalResources()`.
- [ ] Record the current display-region resize path:
  `XRViewport.ResizeRenderPipeline() -> ViewportResized(...) -> DefaultRenderPipeline.HandleViewportResized(...)`.
- [ ] Capture current resize behavior in the editor scene panel and main window,
  including black-frame or missing-resource symptoms if present.
- [ ] Capture representative resource counts for the default profile
  (textures, renderbuffers, buffers, FBOs, texture views).
- [ ] Decide the initial declared-resource coverage list. Start with
  always-needed GBuffer, depth/stencil, forward, lighting, post-process, AA, and
  present-chain resources.
- [ ] Decide which resources stay command-owned for the first milestone
  (fullscreen quad renderers, command-local materials, cached mesh renderers,
  branch-local helpers).
- [ ] Add or update a short architecture note in
  `docs/architecture/rendering/default-render-pipeline-notes.md` once
  implementation begins.

Acceptance criteria:

- [ ] The baseline explains every cache-created core resource and both resize
  branches before behavior changes begin.
- [ ] The first milestone resource coverage list is documented and intentionally
  excludes only dynamic or branch-local resources.

## Phase 1 - Resource Spec Model And Layout Builder

- [ ] Add `RenderPipeline.DescribeResources(RenderPipelineResourceLayoutBuilder builder)`
  as a protected virtual hook.
- [ ] Add immutable `RenderPipelineResourceLayout`.
- [ ] Add `RenderPipelineResourceLayoutBuilder`.
- [ ] Add spec types:
  `TextureSpec`, `RenderBufferSpec`, `FrameBufferSpec`, `BufferSpec`,
  `TextureViewSpec`, and optional `QuadMaterialSpec`.
- [ ] Add typed fields required by the design: name, kind, lifetime, size
  policy, format, samples, layers, mip policy, usage, dependencies, predicate,
  history policy, and debug label.
- [ ] Keep `RenderResourceLifetime` limited to `Persistent`, `Transient`, and
  `External`; model history with persistent specs plus history policy.
- [ ] Add lowering from specs into the existing `TextureResourceDescriptor`,
  `FrameBufferResourceDescriptor`, `RenderBufferResourceDescriptor`, and
  `BufferResourceDescriptor` records.
- [ ] Add deterministic dependency ordering and validation for duplicate names,
  missing dependencies, invalid attachment references, and unsupported size
  policies.
- [ ] Add resource profile inputs for output HDR, AA mode, MSAA sample count,
  feature set, display size, and internal size.
- [ ] Add `ResourceGenerationKey` with mono desktop fields and reserved stereo
  room for the later XR follow-up.

Acceptance criteria:

- [ ] A pipeline can produce a stable immutable resource layout without
  executing render commands.
- [ ] Invalid specs fail with actionable diagnostics that name the resource and
  field.
- [ ] Specs can be lowered to existing registry/planner descriptor records.

## Phase 2 - DefaultRenderPipeline Declared Descriptors

- [ ] Teach `DefaultRenderPipeline` to declare depth/stencil, depth view,
  GBuffer textures, transform-id texture, deferred GBuffer FBO, forward FBO, and
  light-combine resources.
- [ ] Declare MSAA deferred resources behind the effective MSAA profile
  predicate.
- [ ] Declare AO resources and FBOs behind the effective AO/profile predicate.
- [ ] Declare post-process, bloom, motion blur, DoF, temporal accumulation, and
  final output resources that are stable enough for first migration coverage.
- [ ] Declare AA/upscale resources for FXAA and TSR predicates.
- [ ] Declare transparency resources that are stable and pipeline-owned; leave
  branch-local experimental resources on cache commands when needed.
- [ ] Add shared helpers or templates for repeated texture format, size, sample,
  and lifetime boilerplate.
- [ ] Keep `DefaultRenderPipeline2` unchanged.
- [ ] Add descriptor parity diagnostics comparing declared layout descriptors
  with descriptors registered by existing cache commands.
- [ ] Emit warnings for missing layout entries, format mismatches, size-policy
  mismatches, sample-count mismatches, lifetime mismatches, and attachment
  mismatches.

Acceptance criteria:

- [ ] Default pipeline resource layout covers the core steady-state graph
  resources for the default mono desktop profile.
- [ ] Runtime behavior still uses cache commands for materialization in this
  phase.
- [ ] Parity diagnostics are quiet for migrated resources or report clear,
  intentional differences.

## Phase 3 - Generation Ownership Skeleton

- [ ] Add `RenderResourceGeneration` with immutable key, layout, registry,
  validation status, diagnostics, backend handles/links, and retirement state.
- [ ] Add `ActiveGeneration`, `PendingGeneration`, and `RetiredGenerations` to
  `XRRenderPipelineInstance`.
- [ ] Preserve the existing integer `ResourceGeneration` stamp during migration.
- [ ] Ensure committing a new `RenderResourceGeneration` increments the existing
  integer stamp.
- [ ] Add active/pending/retired diagnostics, including generation key, status,
  build duration, resource count by kind, commit reason, and retirement reason.
- [ ] Add a conservative retired-generation cap and a safe fallback sync path
  when retired resources accumulate during resize.
- [ ] Add generation lifetime tests for active, pending, commit, failed pending,
  retired, and superseded pending states.

Acceptance criteria:

- [ ] A pipeline instance can own generation objects without changing frame
  output yet.
- [ ] Failed or canceled pending generations dispose partial resources without
  touching the active generation.
- [ ] Existing cache-command generation-stamp checks continue to observe
  committed generation changes.

## Phase 4 - Scoped Resource Build Context

- [ ] Add a scoped pending-generation build context on `XRRenderPipelineInstance`.
- [ ] Route size helpers such as internal width/height and full width/height
  through the active build context when one is present.
- [ ] Route `GetTexture`, `GetFBO`, `GetBuffer`, and `GetRenderBuffer` to the
  pending registry first while materializing a pending generation.
- [ ] Route `SetTexture`, `SetFBO`, `SetBuffer`, and `SetRenderBuffer` into the
  pending registry while inside the build scope.
- [ ] Keep active frame execution reads pointed at the active generation.
- [ ] Add guardrails so nested or cross-thread build contexts fail loudly with
  diagnostics.
- [ ] Preserve the existing render-thread mutation guard for physical object
  creation.

Acceptance criteria:

- [ ] Existing resource factories can build into a pending generation without
  mutating the active registry.
- [ ] Active rendering remains stable while a pending build context exists.
- [ ] Build-context misuse produces a clear error instead of corrupting the live
  registry.

## Phase 5 - Resource Manager Materialization

- [ ] Add `RenderPipelineResourceManager`.
- [ ] Materialize declared textures, texture views, renderbuffers, buffers, and
  FBOs into a generation registry before command execution.
- [ ] Validate FBO attachments by same-generation identity, dimensions, sample
  counts, formats, and backend completeness.
- [ ] Validate texture views by same-generation source texture identity, mip
  range, layer range, format/aspect, and target interpretation.
- [ ] Validate required resources before a generation can commit.
- [ ] Allow history resources to commit invalid/empty when their history policy
  permits seeding from the current frame.
- [ ] Make cache commands no-op when a declared matching resource already exists
  in the active generation.
- [ ] Keep cache commands functional for unmigrated dynamic resources.
- [ ] Add focused tests for materialization order, missing dependency failure,
  FBO validation failure, texture-view validation failure, and cache-command
  compatibility no-ops.

Acceptance criteria:

- [ ] Declared resources can be created before render command execution.
- [ ] Cache commands stop recreating migrated resources every frame.
- [ ] Missing or incompatible required resources prevent pending commit and keep
  the active generation rendering.

## Phase 6 - Staged Resize And Coalescing

- [ ] Add explicit requested internal size and active internal size tracking on
  `XRViewport` or `XRRenderPipelineInstance`.
- [ ] Funnel internal-resolution resize into `RequestResourceGeneration(...)`
  instead of destructive `InvalidatePhysicalResources()`.
- [ ] Funnel display-region resize into the same generation request path instead
  of destructively evicting AA/post-process/present resources through
  `InvalidateViewportResizeResources(...)`.
- [ ] Keep presentation using the current display region while render passes use
  the active generation size until pending commit.
- [ ] Prepare pending generations incrementally at the requested size.
- [ ] Time-slice physical resource creation and FBO completeness checks across
  frames.
- [ ] Add resize debounce for interactive scene-panel and window drag
  (target 100-150 ms of no change, plus a maximum interval cap).
- [ ] Supersede in-flight pending generations when a newer generation key is
  requested, disposing abandoned partial resources safely.
- [ ] Commit pending generation atomically once all required resources validate.
- [ ] Retire the old generation after GPU completion or through the conservative
  bridge path.
- [ ] Ensure pending generation failure records diagnostics and leaves active
  rendering untouched.

Acceptance criteria:

- [ ] Editor scene-panel resize keeps displaying the active image until the
  replacement generation commits.
- [ ] Main-window resize keeps displaying the active image until the replacement
  generation commits.
- [ ] No resize path empties the active registry.
- [ ] Failed resize generation attempts never present a missing-resource or
  black-frame state.

## Phase 7 - Vulkan Prepare/Swap Physical Plan

- [ ] Sync `VulkanResourcePlanner` from complete declared layouts before command
  execution.
- [ ] Add Vulkan pending physical resource plan support.
- [ ] Allocate pending Vulkan images, buffers, views, and framebuffers without
  destroying the active plan.
- [ ] Validate render-pass metadata and dynamic-rendering attachments against
  declared resources before recording.
- [ ] Commit the pending Vulkan physical plan with the logical generation.
- [ ] Retire old Vulkan physical resources through fences instead of requiring
  unconditional `DeviceWaitIdle()`.
- [ ] Keep an initial conservative idle-wait bridge where needed, but keep it
  outside the long-term API contract.
- [ ] Add Vulkan diagnostics for missing declared resource, stale descriptor,
  attachment-generation mismatch, and retired-resource lifetime.

Acceptance criteria:

- [ ] Vulkan planning no longer depends on descriptors discovered during cache
  command execution for migrated core resources.
- [ ] Vulkan can prepare replacement physical resources without destroying the
  active plan first.
- [ ] Any remaining idle wait is explicit, diagnosed, and removable.

## Phase 8 - Remove Migrated Core Cache Commands

- [ ] Remove `CacheTextures(c)` calls for migrated core resources.
- [ ] Remove FBO cache commands for migrated core FBOs.
- [ ] Remove renderbuffer and data-buffer cache commands for migrated core
  resources.
- [ ] Keep compatibility cache commands only for dynamic, branch-local, or
  experimental resources that are intentionally not declared yet.
- [ ] Remove descriptor-parity warnings for resources whose command-side authoring
  has been deleted.
- [ ] Update command-chain comments and docs so render commands are described as
  execution order, state binding, dispatch, blit, and presentation rather than
  the primary resource lifecycle.
- [ ] Audit for direct active-registry mutation in `DefaultRenderPipeline` resize
  and resource factory paths.

Acceptance criteria:

- [ ] Migrated core graph resources are authored only in the layout.
- [ ] Command chains no longer allocate or recreate migrated core resources in
  steady-state frame execution.
- [ ] Dynamic or branch-local compatibility cache commands are named and
  justified.

## Phase 9 - Diagnostics, Tests, And Documentation

- [ ] Add diagnostics for active generation key.
- [ ] Add diagnostics for pending generation key and status.
- [ ] Add diagnostics for generation build duration.
- [ ] Add diagnostics for resource count by kind.
- [ ] Add diagnostics for missing required resources.
- [ ] Add diagnostics for failed backend generation.
- [ ] Add diagnostics for incomplete FBO name and attachment summary.
- [ ] Add diagnostics for commit and retirement reasons.
- [ ] Add diagnostics for old generation lifetime after commit.
- [ ] Add tests for descriptor parity, spec lowering, dependency ordering,
  generation commit/failure, cache-command no-op compatibility, and resize
  generation requests.
- [ ] Create or update
  `docs/architecture/rendering/render-pipeline-resource-lifecycle.md` after
  implementation details settle.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` with
  the final resource-layout and resize invariants.
- [ ] Update Vulkan rendering docs if planner/allocation behavior changes.

Acceptance criteria:

- [ ] A failed pending generation log identifies the pipeline, target size,
  resource, reason, and active generation that remains in use.
- [ ] Tests cover the resource lifecycle contract without depending on visual
  editor runs.
- [ ] Architecture docs match the implemented behavior and name any remaining
  compatibility commands.

## Final Validation And Merge

- [ ] Build the editor:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- [ ] Run focused resource lifecycle/unit tests.
- [ ] Resize the editor scene panel continuously; confirm no black frames or
  missing-resource warnings.
- [ ] Resize the main window continuously; confirm no black frames or
  missing-resource warnings.
- [ ] Toggle output HDR while rendering; confirm resources regenerate without
  stale FBO attachments.
- [ ] Toggle AA modes and MSAA sample counts; confirm active generation remains
  valid until replacement generation commits.
- [ ] Validate OpenGL FBO completeness diagnostics remain quiet after repeated
  resize.
- [ ] Validate Vulkan Sponza/deferred path after resize when Vulkan is stable
  enough to test.
- [ ] Confirm no missing-resource warnings from light combine, post-process, or
  final present for migrated resources.
- [ ] Run `Report-NewAllocations` or equivalent targeted allocation audit for
  render submission and resize hot paths.
- [ ] Update PR notes with what changed, why, validation performed, risks, and
  follow-ups.
- [ ] Merge branch `rendering-resource-lifecycle` back into `main` after
  implementation, validation, and documentation updates are complete.

## Follow-Up Questions

These should be answered before Phase 5 or Phase 6 starts, because they affect
materialization, resize, and diagnostics contracts.

- Should resource predicates be evaluated per camera, per viewport, or per
  pipeline instance for the first implementation?
- Should optional feature resources be prewarmed when disabled but likely to be
  enabled soon, such as debug visualizations?
- Should transient resources be aliasable in OpenGL, or should aliasing remain a
  Vulkan-only physical allocation optimization?
- Should fullscreen quad materials become declared `QuadMaterialSpec` resources
  in this milestone, or remain command-owned until the core graph is stable?
- Should descriptor parity tests require exact descriptor equality, or tolerate
  equivalent legacy factory output during migration?
