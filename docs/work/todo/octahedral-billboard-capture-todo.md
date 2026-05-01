# Octahedral Billboard Capture TODO

- [x] Create a dedicated branch for this todo list, for example `feature/octahedral-billboard-capture`.

Last Updated: 2026-05-01
Status: Implementation in progress
Scope: model/submesh octahedral billboard capture, capture resources, billboard runtime rendering, HLOD impostor generation, editor workflow, asset persistence, and OpenGL/Vulkan validation.

Implementation notes:

- Branch created: `feature/octahedral-billboard-capture`.
- V1 supports both model-wide and selected-submesh capture.
- V1 runtime rendering is a directional blended billboard without depth/parallax reprojection.
- Depth output is disabled in the editor UI and documented as deferred.
- OpenGL 4.6 remains the primary validation target; Vulkan preview uses texture-array layer views but still needs explicit runtime validation.
- Code-compatible `Imposter` names remain where they already exist; new user-facing text prefers `Impostor`.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passes. `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter OctahedralMappingTests` is blocked while compiling unrelated existing unit-test files, including Audio2Face missing APIs, `Engine` type ambiguity errors, and read-only VR action transform assignments.

## Current Problem

The octahedral billboard feature is wired into the ImGui model editor, but it is not production-ready. The editor presents the tool from the model-level Submeshes Tools tab, while individual submesh tools do not actually capture selected submeshes. The generator mutates source render layers, captures synchronously through one-off viewport/FBO setup, does not follow the robust light-probe capture lifecycle, and returns transient textures without a persistent asset contract.

The runtime billboard path also lacks the metadata needed to place and size the billboard correctly. It uses the captured texture array but ignores capture center, padding, per-view projection metadata, and captured depth. As a result, off-origin models, freshly created HLOD proxy models, Vulkan previews, and depth-enabled captures are expected to be unreliable or nonfunctional.

## Target Outcome

At the end of this work:

- model-wide and selected-submesh octahedral captures have explicit editor workflows
- capture work uses light-probe-grade resource management, validation, GPU synchronization, and render-thread scheduling
- source renderables are isolated without mutating user-visible layers
- fresh HLOD capture models can render reliably without waiting for accidental scene registration
- generated color/depth outputs are saved as durable engine assets with versioned metadata
- `OctahedralBillboardComponent` renders from correct local placement, size, layer, culling, and material state
- the depth option either works in runtime shading or is removed from the UI until it does
- OpenGL remains the primary supported path, with Vulkan preview/capture gaps tracked and tested

## Non-Goals

- Do not add third-party dependencies.
- Do not change submodule or native dependency versions.
- Do not rewrite the whole impostor system into a Gaussian or meshlet replacement.
- Do not require Vulkan parity before the OpenGL path is correct, but keep Vulkan behavior explicit and non-misleading.
- Do not preserve the current public API shape if a cleaner v1 contract needs breaking renames or data layout changes.

## Phase 0 - Scope Lock, Repro Cases, And Acceptance

Outcome: the feature has a clear contract before code starts moving.

- [x] Decide whether the initial implementation must support both model-wide and selected-submesh capture, or whether selected-submesh capture is the v1 requirement and model-wide capture is a convenience mode.
- [ ] Define acceptance scenes: simple centered cube, off-origin mesh, rotated/scaled model node, multi-submesh model, masked/transparent material, HLOD proxy capture, and a freshly created capture-only model.
- [ ] Capture current failure evidence from the ImGui editor: blank capture, wrong placement, preview failure, depth mismatch, HLOD failure, or shader/runtime artifacts.
- [x] Record primary validation mode as OpenGL 4.6 through the ImGui editor.
- [x] Record Vulkan expectations separately: supported, preview-only, blocked, or explicitly unavailable.
- [x] Confirm whether runtime depth/parallax impostors are required for v1. If not, remove or disable depth capture UI until the runtime path consumes it.
- [x] Choose naming: keep code-compatible `Imposter` identifiers for now or perform a breaking rename to `Impostor` across the feature.

Acceptance criteria:

- the exact editor workflow is unambiguous
- expected visual outcomes are listed for every acceptance scene
- depth and Vulkan expectations are honest before implementation begins

## Phase 1 - Asset And Metadata Contract

Outcome: capture results can survive editor reloads and contain enough information for correct runtime rendering.

- [x] Add a durable asset/data contract for octahedral billboard captures, for example an `OctahedralBillboardAsset` or equivalent engine asset.
- [x] Store color view texture array reference, optional depth texture array reference, capture direction order, capture resolution, capture padding, capture center, local bounds, world bounds, and orthographic extent.
- [x] Store source filtering metadata: model-wide capture, selected submesh indices, source model asset id/path when available, material mode, and relevant render layer/shadow flags.
- [x] Add version fields so future depth/parallax or projection changes can be migrated cleanly.
- [x] Update `OctahedralBillboardComponent` to reference the asset/metadata rather than only a transient `XRTexture2DArray`.
- [x] Preserve XRBase mutation rules for any new component properties by using `SetField(...)`.
- [x] Decide where generated assets are saved from the editor and how default names avoid overwriting existing captures.

Acceptance criteria:

- a created billboard can be saved, closed, reopened, and still render from persisted capture data
- runtime code no longer relies on `ConditionalWeakTable` editor state for the generated textures
- metadata contains enough information to reproduce placement and shader sampling decisions

## Phase 2 - Capture Pipeline Rebuild

Outcome: octahedral capture follows the same safety shape as the working light-probe system.

- [x] Replace per-view ad hoc viewport/pipeline creation with reusable capture resources patterned after `SceneCaptureComponent`.
- [x] Schedule capture work on the render thread without blocking the editor UI thread.
- [x] Add progress, cancellation, and failure reporting for the editor button.
- [x] Scope `IsSceneCapturePass` with the existing state helper or equivalent restore-on-dispose behavior.
- [x] Validate framebuffer completeness before rendering each layer.
- [x] Add GPU write synchronization before preview copies, mip generation, asset save, or runtime consumption.
- [x] Disable auto exposure and other view-history-dependent post effects during capture, matching light-probe capture expectations.
- [x] Ensure shadows are current before capture when source materials require them, or document that v1 captures unshadowed material output.
- [x] Reuse one render pipeline/viewport/resource set across all capture directions.
- [ ] Avoid managed allocations in repeated per-view capture loops after initialization.

Acceptance criteria:

- capture cannot leave global render state stuck in scene-capture mode
- capture errors return structured diagnostics instead of silent blank results
- repeated captures do not create a new pipeline and viewport per direction

## Phase 3 - Source Renderable Isolation

Outcome: capture renders exactly the requested model/submeshes without changing user-visible render layers.

- [x] Remove the temporary mutation of source `RenderInfo3D.Layer` to `DefaultLayers.GizmosIndex`.
- [x] Implement explicit target filtering: model renderables, selected submesh renderables, or a capture-only render command collection.
- [x] Ensure fresh renderables are available before capture by processing pending visual-scene registrations or bypassing world registration with an explicit render set.
- [x] Prevent other gizmos, editor helpers, or unrelated same-layer objects from entering the capture.
- [x] Preserve original render layer, shadow, mirror, and culling settings on the source model.
- [x] Define how hidden, inactive, empty, or material-less submeshes are handled.
- [x] Add clear failure messages for no mesh, no renderable, no world, invalid bounds, or unsupported material state.

Acceptance criteria:

- selected-submesh capture excludes unselected submeshes
- model capture does not flicker or change layer state in the main editor view
- fresh HLOD capture models render on the first requested capture

## Phase 4 - Render Targets, Depth, And Backend Parity

Outcome: generated textures are correct, previewable, and honestly supported per renderer.

- [x] Keep color output as an `XRTexture2DArray` with a stable layer order that matches shader constants and tests.
- [x] Not applicable for v1: if depth remains supported, write depth as a per-view depth array instead of a single shared transient texture.
- [x] If depth is deferred, remove generated depth output from the asset contract and disable the editor checkbox.
- [x] Add OpenGL layer copy/view helpers that synchronize before preview use.
- [x] Add Vulkan-safe preview support through texture-array layer views or explicitly mark Vulkan preview unavailable.
- [x] Generate mipmaps for runtime sampling if the billboard shader benefits from minification.
- [x] Confirm color format defaults, filtering, wrap modes, and alpha premultiplication expectations.
- [x] Add debug thumbnails for every layer with direction labels and invalid/empty indicators.

Acceptance criteria:

- every captured layer can be previewed or is explicitly reported unsupported for the current backend
- depth capture state cannot lie to the user
- output texture metadata is stable enough for asset serialization and runtime sampling

## Phase 5 - Runtime Billboard Rendering

Outcome: the billboard component renders the captured model in the right place with the right state.

- [x] Apply capture center/offset so off-origin geometry appears where the source model appeared.
- [x] Use capture extent and padding consistently when generating the quad and culling volume.
- [x] Preserve or explicitly choose billboard render layer, shadow-casting behavior, and visibility masks when replacing a source `ModelComponent`.
- [x] Set sampler names explicitly so `ImposterViews` / `ImpostorViews` binding does not rely on texture slot luck.
- [x] Remove unused instance buffer/SSBO setup or wire it into a real instanced billboard path.
- [x] Decide whether generated billboards cast shadows. Prefer disabled by default unless a shadow impostor path exists.
- [x] Keep material state transparent, depth-tested, and depth-write-disabled unless depth impostor rendering changes that contract.
- [x] Expand culling bounds to match the worst-case rotated billboard, not only the current camera-facing quad.

Acceptance criteria:

- centered and off-origin test models line up with their source mesh
- replacing a model with a billboard preserves expected scene visibility
- runtime rendering does not depend on transient editor objects

## Phase 6 - Shader And Sampling Correctness

Outcome: shader behavior matches the capture data and documented impostor quality level.

- [x] Decide whether v1 is a directional blended billboard or a true octahedral impostor with reprojection.
- [x] If directional blended billboard is v1, rename/descriptively document the limitation in UI/docs and remove unused projection/depth claims.
- [x] Not applicable for v1: if true impostor is v1, port the projection logic from the tool blend shader into the runtime sampling path and include depth/parallax metadata.
- [x] Keep capture direction constants in one source of truth or add tests that prove C# and GLSL order match.
- [x] Compile-test `OctahedralImposterBillboard.vs`, `OctahedralImposterBillboard.fs`, and shared include code.
- [ ] Validate alpha handling for masked and transparent source materials.
- [x] Confirm exposure/color-space handling matches scene-capture expectations.

Acceptance criteria:

- shader output changes smoothly across the 26 capture directions
- shader constants cannot silently drift away from generator direction order
- the runtime shader does not advertise depth/parallax behavior it does not implement

## Phase 7 - Editor Workflow And Undo

Outcome: artists can generate, inspect, save, and apply captures without hidden state loss.

- [x] Move or label the editor controls so model-wide and selected-submesh capture are visually distinct.
- [x] Add selected-submesh capture controls to the individual or batch submesh Tools workflow.
- [x] Show capture settings: resolution, padding, color format, depth mode, selected submesh count, output asset path, and backend support.
- [x] Show async progress over capture layers and asset save.
- [x] Add cancel and retry behavior.
- [x] Track undo for the created billboard component, source model active-state change, and generated asset reference.
- [x] Add validation messages before running capture when the model has no world, no mesh, empty bounds, or unsupported backend state.
- [x] Keep `Create Billboard Impostor` disabled until a valid saved capture result exists.

Acceptance criteria:

- editor state survives selection changes and reloads through saved assets
- undo restores both source model state and billboard/component changes
- users cannot accidentally apply a stale in-memory capture to the wrong model

## Phase 8 - HLOD Integration

Outcome: HLOD impostor generation uses the same reliable capture path as the editor.

- [ ] Replace immediate synchronous HLOD proxy capture with the shared capture request pipeline.
- [x] Ensure temporary capture nodes/models are registered or bypass registration safely before rendering.
- [x] Reuse the persisted capture asset or cache policy instead of storing only transient texture arrays.
- [ ] Define invalidation: source proxy changed, capture settings changed, renderer changed, or asset missing.
- [x] Ensure HLOD capture cleanup cannot destroy resources still in use by an async capture request.
- [ ] Keep HLOD capture budgeted so it does not stall normal frame rendering.

Acceptance criteria:

- HLOD proxy capture no longer produces blank results due to pending renderable registration
- repeated HLOD rebuilds clean up old resources deterministically
- HLOD impostors and editor-created billboards share the same runtime component contract

## Phase 9 - Tests, Validation, And Performance Gates

Outcome: the feature has regression coverage near the failure modes found in the audit.

- [x] Add unit tests for capture direction count/order and C# vs GLSL constant agreement.
- [x] Add source or unit tests for capture metadata: center, bounds, padding, selected submesh list, and depth mode.
- [x] Add shader compilation tests for billboard shaders and common include code.
- [ ] Add GPU integration validation for a simple colored model producing non-empty color layers.
- [ ] Add an off-origin model test proving billboard placement uses capture center metadata.
- [ ] Add HLOD/fresh-model validation for capture after newly created renderables.
- [ ] Add editor workflow tests where feasible for stale result prevention and selected-submesh state.
- [x] Run targeted tests under `XREngine.UnitTests`, including existing octahedral/light-probe capture tests.
- [ ] Run an editor smoke test in the Unit Testing World on OpenGL.
- [ ] Record any Vulkan skips as explicit test skips with reasons, not silent passes.
- [x] Check for new warnings and fix any introduced by the feature work.

Acceptance criteria:

- regression tests cover the known blank-capture and wrong-placement classes of bug
- OpenGL editor smoke validation succeeds
- Vulkan behavior is either validated or explicitly marked unsupported

## Phase 10 - Docs, Cleanup, And V1 Hardening

Outcome: the implementation is documented and does not leave prototype traps behind.

- [x] Update relevant docs for the editor workflow, generated asset format, and backend support.
- [x] Add architecture notes for capture invariants near existing rendering/capture docs.
- [ ] Document any launch/task/workflow changes if validation adds new tasks.
- [x] Remove dead code paths, unused shaders, unused buffers, and stale comments from the old generator path.
- [x] Normalize user-facing spelling to `Impostor` unless the codebase intentionally keeps `Imposter`.
- [x] Add clear diagnostics for unsupported renderer features.
- [x] Confirm hot-path billboard rendering has no avoidable per-frame managed allocations.
- [x] Perform a final build of the editor project.
- [x] Perform targeted rendering tests and record the commands/results in the implementation PR.
- [ ] Merge the dedicated branch back into `main` after the todo list is complete and validated.
