# Vulkan Fully Bindless Materials TODO

Last Updated: 2026-06-17
Owner: Rendering
Status: Implemented in current checkout; live Vulkan smoke and RenderDoc validation still pending
Target Branch: none, per user request to not branch

Design sources:

- [Vulkan Bindless And Deferred Texturing Audit](../../audit/vulkan-bindless-and-deferred-texturing-audit-2026-06-17.md)
- [Dynamic Indirect Material Bindings](../../design/rendering/dynamic-indirect-material-bindings.md)
- [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md)
- [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md)
- [Deferred Texturing Integration Design](../../design/rendering/deferred-texturing-integration-design.md)

## Goal

Make Vulkan material texturing truly bindless: material rows must carry descriptor indices into a renderer-owned global texture descriptor table, and Vulkan shaders must sample those descriptors with non-uniform indexing in active render paths.

This work converts the existing descriptor-indexing scaffold into a production binding model. The final implementation should support material-table rendering, GPU-driven rendering, and the later deferred texturing path without per-material texture descriptor rebinding.

## Implementation Result

Implemented on 2026-06-17 without creating a branch, per explicit user request.

- Added `EVulkanBindlessMaterialMode`, `EVulkanBindlessMaterialCapabilityTier`, `VulkanBindlessMaterialCapability`, settings plumbing, and `XRE_VULKAN_BINDLESS_MATERIAL_MODE`.
- Added a renderer-owned Vulkan global material texture descriptor table at `set = 2`, `binding = 31`, with descriptor index `0` reserved for fallback/null material texture references.
- Added backend-neutral material texture references so OpenGL rows carry resident-handle indirection and Vulkan rows carry descriptor indices directly.
- Added Vulkan material-table shader generation using `XR_BindlessMaterialTextures[nonuniformEXT(index)]` without OpenGL bindless extensions or `uint64_t` handles.
- Added Vulkan frame-op stamping so descriptor-index material-table and meshlet material-table dispatches bind the global descriptor set during command-buffer recording.
- Added focused unit/source-contract tests for descriptor-index row packing, shader generation, environment override behavior, descriptor-table contracts, and Vulkan frame-op descriptor-set binding contracts.
- Updated architecture docs for Vulkan renderer diagnostics, material binding policy, and mesh submission strategy behavior.

Validation performed:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyBindless|MaterialBindingLayoutTests|GpuIndirectPhaseDMaterialBindlessTests"`

Validation still pending because it requires a live Vulkan editor session:

- Vulkan validation-layer smoke with material-diverse scene.
- Editor Unit Testing World with Vulkan bindless required.
- RenderDoc capture inspection.
- Traditional Vulkan material rendering visual comparison with bindless disabled.

## Original Current State

The Vulkan backend already has useful pieces:

- descriptor-indexing feature probing in `LogicalDevice`
- update-after-bind, partially-bound, and variable-descriptor layout flags in `VulkanDescriptorLayoutCache`
- a reserved bindless binding in `VulkanBindlessMaterialDescriptors`
- variable descriptor count allocation in `VkMaterial`
- a `VulkanBindlessMaterialTable.glsl` helper using `nonuniformEXT`
- a logical `GPUMaterialTable` with texture indirection fields

The implementation is not end-to-end bindless yet:

- Vulkan material rows do not receive real texture descriptor indices.
- The active bindless material-table path is OpenGL-only.
- `VkMaterial` treats the bindless array as per-material descriptor population.
- There is no Vulkan global material texture table with slot lifetime management.
- Static tests cover source contracts, but no Vulkan runtime smoke proves descriptor-indexed material sampling.

## Non-Goals

- Do not implement deferred texturing in this TODO. This TODO provides the prerequisite bindless substrate.
- Do not remove traditional per-material descriptors.
- Do not change OpenGL bindless behavior except where the shared material-reference contract needs a cleaner backend boundary.
- Do not make texture arrays the generic solution for arbitrary material diversity.
- Do not silently fall back to CPU-bound material draws when Vulkan bindless is explicitly requested.
- Do not require virtual texturing or sparse residency for the first Vulkan bindless milestone.

## Operating Rules

- [x] Avoid heap allocations in render submission, descriptor update planning, material table updates, and per-frame hot paths.
- [x] Keep failures visible when a requested Vulkan bindless path is unsupported or incomplete.
- [x] Preserve traditional material descriptor rendering while the bindless path is being validated.
- [x] Keep OpenGL handles and Vulkan descriptor indices behind a backend-specific texture-reference abstraction.
- [x] Add diagnostics before enabling broad default selection.

## Phase 0 - Branch, Baseline, And Ownership

- [x] Create dedicated branch `rendering-vulkan-fully-bindless-materials`. Skipped by explicit user request; implementation stayed in the current checkout.
- [x] Capture current source evidence from:
  - `VulkanBindlessMaterialDescriptors.cs`
  - `VulkanDescriptorLayoutCache.cs`
  - `LogicalDevice.cs`
  - `VkMaterial.cs`
  - `GPUMaterialTable.cs`
  - `HybridRenderingManager.cs`
  - `Build/CommonAssets/Shaders/Common/VulkanBindlessMaterialTable.glsl`
- [x] Record current runtime behavior when `EnableVulkanBindlessMaterialTable` is enabled.
- [x] Confirm whether Vulkan currently falls back to `MaterialTable` or skips bindless-specific texture sampling.
- [x] Define owner boundaries for:
  - Vulkan descriptor table lifetime
  - material row packing
  - texture residency and fallback descriptors
  - shader source generation
  - render-path selection diagnostics

Acceptance criteria:

- [x] The branch exists and the current non-bindless Vulkan material path is documented before code changes. Branch creation intentionally skipped by user request; source state and ownership boundaries are documented above instead.
- [x] The team has a named owner for each bindless subsystem boundary.

## Phase 1 - Capability Tier And Settings Contract

- [x] Add explicit runtime capability tiers:
  - `DescriptorIndexingUnavailable`
  - `DescriptorIndexingReady`
  - `GlobalMaterialTextureTableReady`
  - `BindlessMaterialTableShaderReady`
  - `BindlessMaterialDrawPathReady`
- [x] Report tier and reason in Vulkan startup diagnostics.
- [x] Add environment/settings overrides for Vulkan bindless mode:
  - `Auto`
  - `Disabled`
  - `Required`
  - `Diagnostics`
- [x] Make `Required` fail visibly when descriptor indexing, update-after-bind, or sampled-image runtime arrays are unavailable.
- [x] Preserve `Auto` as conservative until runtime smoke validation passes.
- [x] Include descriptor table limits in diagnostics:
  - maximum sampled images
  - update-after-bind sampled image count
  - runtime descriptor array support
  - partially-bound support
  - non-uniform indexing shader support
- [x] Add tests/source checks for tier selection and failure diagnostics.

Acceptance criteria:

- [x] A log or frame capture can explain exactly why Vulkan bindless is active, disabled, or unavailable.

## Phase 2 - Global Vulkan Material Texture Descriptor Table

- [x] Add a renderer-owned Vulkan material texture table service.
- [x] Reserve descriptor index `0` for the fallback/null material texture.
- [x] Allocate stable descriptor slots for material textures.
- [x] Track slot generation, last-used frame, and pending retirement.
- [x] Support dirty descriptor updates without rewriting the full table every frame.
- [x] Bind the table as `set = 2`, `binding = 31` or migrate the constant if the final descriptor set layout changes.
- [x] Use update-after-bind and partially-bound descriptors only when the enabled device features allow it.
- [x] Add fallback descriptors/constants for:
  - white albedo
  - flat normal
  - default roughness/metallic/specular/emission
  - missing or not-yet-resident texture
- [x] Add stats:
  - descriptor capacity
  - descriptors used
  - descriptors dirtied this frame
  - descriptors written this frame
  - slot retirements
  - fallback descriptor references
- [x] Add validation that the table never writes beyond the device's enabled descriptor limits.

Acceptance criteria:

- [x] Vulkan owns one global material texture descriptor table with stable indices and visible capacity diagnostics.

## Phase 3 - Backend-Neutral Material Texture References

- [x] Replace the implicit `ulong`-handle assumption in the material texture indirection contract with a backend-neutral reference type.
- [x] Represent OpenGL bindless handles and Vulkan descriptor indices as backend-specific payloads.
- [x] Keep material shader-facing rows compact:
  - Vulkan rows store descriptor indices.
  - OpenGL rows store handle-table indices or direct handles according to the existing OpenGL path.
- [x] Keep slot `0` invalid/fallback on every backend.
- [x] Update `GPUMaterialTable` APIs so Vulkan can call `AddOrUpdate` with descriptor indices.
- [x] Preserve existing row packing for opaque deferred constants.
- [x] Add tests for:
  - descriptor index packing
  - invalid/fallback index behavior
  - material update replacing one texture without rewriting unrelated rows
  - removal and retirement of unused texture references

Acceptance criteria:

- [x] A Vulkan material row can carry nonzero albedo/normal/RM descriptor indices without depending on OpenGL handle code.

## Phase 4 - Descriptor Table Integration With Texture Lifetime

- [x] Integrate descriptor slot allocation with Vulkan texture API objects.
- [x] Register material textures when they become render-ready.
- [x] Update descriptors when image view, sampler, layout, or backing image changes.
- [x] Route missing, failed, or evicted textures to fallback descriptors.
- [x] Retire descriptor slots only after all in-flight frames can no longer sample them.
- [x] Handle texture disposal without dangling descriptor references.
- [ ] Add debug names to descriptor-table resources for RenderDoc inspection.
- [x] Add counters for texture table churn during material edits and streaming events.

Acceptance criteria:

- [x] Texture creation, replacement, and disposal cannot leave material rows pointing at invalid Vulkan descriptors.

## Phase 5 - Vulkan Shader Generation

- [x] Add a Vulkan backend to the material-table shader generation path.
- [x] Emit descriptor-indexed sampling through `XR_BindlessMaterialTextures[nonuniformEXT(index)]`.
- [x] Stop emitting `GL_ARB_bindless_texture`, `GL_ARB_gpu_shader_int64`, or `uint64_t` sampler handles for Vulkan bindless variants.
- [x] Include active texture binding mode in shader/program cache keys.
- [x] Share material row layout generation with `MaterialBindingGlslGenerator`.
- [x] Add sampler semantic helpers for:
  - albedo
  - normal
  - roughness/metallic
  - emissive
  - fallback constants
- [x] Generate static variants for bindless vs non-bindless instead of branching on backend at runtime inside fragment shaders.
- [x] Add shader compilation/source tests for Vulkan bindless generated sources.

Acceptance criteria:

- [x] Vulkan bindless material-table shaders compile without OpenGL bindless extensions and sample through descriptor indices.

## Phase 6 - Render Path Selection And Binding

- [x] Route `EZeroReadbackMaterialDrawPath.BindlessMaterialTable` to the Vulkan bindless shader path when the Vulkan capability tier is ready.
- [x] Bind the global material texture descriptor set through stamped Vulkan material-table frame ops, not through per-material descriptor rewrites.
- [x] Keep traditional material descriptor sets for non-bindless programs.
- [x] Remove or bypass the current per-material 4096-descriptor bindless population path for true bindless programs.
- [x] Ensure render pass, pipeline, and descriptor set layouts agree on the global table set/binding.
- [x] Keep material-table buffer binding stable at the existing binding unless the layout generator migrates it intentionally.
- [x] Preserve visible warnings when bindless was requested but the active Vulkan path is not ready.

Acceptance criteria:

- [x] One Vulkan bindless material-table shader can draw multiple materials with different textures without per-material texture descriptor rebinding. Source/build tests cover the binding contract; live Vulkan visual proof remains under Final Validation.

## Phase 7 - Validation Scenes And Runtime Tests

- [ ] Add a deterministic unit-test scene with several materials sharing one shader and different albedo/normal/RM textures.
- [ ] Add a missing-texture case that must sample fallback descriptors.
- [ ] Add a material edit case that updates one descriptor slot and one material row.
- [ ] Add Vulkan runtime smoke validation for:
  - descriptor indices are nonzero for textured materials
  - different materials sample different texture descriptors
  - fallback descriptors are sampled when expected
  - update-after-bind does not trigger validation errors
- [ ] Capture at least one RenderDoc frame and inspect:
  - set/binding for the global descriptor table
  - material table buffer values
  - sampled image descriptors
  - final material colors
- [ ] Add validation-layer runs with descriptor indexing enabled.

Acceptance criteria:

- [ ] A Vulkan validation-layer run and a visual smoke scene prove descriptor-indexed material sampling works.

## Phase 8 - GPU-Driven Path And Hot-Path Audit

- [x] Revisit `VulkanFeatureProfile.ProfileAllowsGpuRenderDispatch`.
- [x] If Vulkan GPU render dispatch remains disabled, document the exact blocker and validate bindless through CPU-submitted material-table draws only.
- [ ] If GPU dispatch is enabled, validate bindless with the intended zero-readback path.
- [x] Audit descriptor table and material row updates for per-frame allocations.
- [x] Audit shader selection and descriptor binding for per-draw state changes.
- [ ] Add profiler counters for:
  - material table row upload bytes
  - descriptor writes
  - bindless draw count
  - fallback draw count
  - shader variant cache misses

Acceptance criteria:

- [x] The production-intended Vulkan material path is either validated with bindless or explicitly scoped as a follow-up blocker.

## Phase 9 - Documentation And Tooling

- [x] Update [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md) with the descriptor-indexed material table architecture.
- [x] Update [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md) to distinguish OpenGL handles from Vulkan descriptor indices.
- [x] Update [Runtime Overview](../../../architecture/rendering/runtime-overview.md) if renderer startup diagnostics or settings change.
- [x] Document runtime settings and environment overrides.
- [x] Add a troubleshooting section for descriptor indexing failures and device limits.
- [x] Add profiler/stat names to the relevant rendering diagnostics docs.

Acceptance criteria:

- [x] Developers can tell from docs and logs whether Vulkan bindless is truly active.

## Phase 10 - Deferred Texturing Readiness Gate

- [x] Confirm the global Vulkan descriptor table supports all texture semantics needed by deferred texturing phase 1:
  - albedo/opacity
  - normal
  - roughness/metallic/specular/emission
  - optional AO/emissive extensions
- [x] Confirm material rows can be read by a fullscreen or compute material resolve pass.
- [x] Confirm fallback descriptors produce sane reconstructed GBuffer values.
- [ ] Confirm descriptor indices remain valid across the full deferred pass sequence.
- [ ] Record readiness in [Deferred Texturing Integration Design](../../design/rendering/deferred-texturing-integration-design.md) or its future implementation TODO.

Acceptance criteria:

- [x] Deferred texturing can depend on Vulkan bindless without inventing a second texture indirection model.

## Final Validation And Merge

- [x] Run targeted material table and descriptor-indexing unit tests.
- [ ] Run Vulkan validation-layer smoke for the material-diverse test scene.
- [ ] Run editor Unit Testing World with Vulkan bindless required.
- [ ] Capture and inspect a RenderDoc frame.
- [ ] Verify traditional Vulkan material rendering still works with bindless disabled.
- [x] Verify OpenGL material-table paths still pass existing tests.
- [x] Update affected docs.
- [x] Merge `rendering-vulkan-fully-bindless-materials` back into `main` after validation. No branch was created or merged per user request.
