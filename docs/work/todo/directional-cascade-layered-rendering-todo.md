# Directional Cascade Layered Atlas Integration Todo

Status: active follow-up as of 2026-05-04

## Goal

Make `CascadeShadowRenderMode` work for both directional cascade backends:

- Legacy texture-array cascades keep the current behavior:
  - `Sequential`: render one texture-array layer at a time.
  - `GeometryShader`: render all active cascades to the layered texture-array FBO.
  - `InstancedLayered`: render all active cascades to the layered texture-array FBO by deriving the cascade from `gl_InstanceID`.
  - `Auto`: choose instanced layered, then geometry shader, then sequential.
- Directional shadow-atlas cascades gain matching grouped single-pass options when all active cascade tiles can be allocated coherently on one atlas page.
- Existing sequential atlas tile rendering remains the compatibility/default atlas path and must be preserved.

## Non-Goals

- Do not change cascade split selection, receiver cascade selection, bias math, PCF/PCSS filtering, EVSM/VSM storage, or the public meaning of existing cascade settings.
- Do not require atlas mode for legacy texture-array cascades.
- Do not make Vulkan atlas layered rendering part of this pass; add capability seams and fallback cleanly.
- Do not remove the current per-tile atlas renderer.

## Implementation Tasks

- [ ] Create a dedicated branch for directional cascade atlas layered integration.
- [ ] Add source-contract tests that lock the current compatibility behavior:
  - [ ] `Sequential` remains the default mode.
  - [ ] Legacy non-atlas cascades still support sequential, geometry-shader layered, instanced layered, and auto fallback.
  - [ ] Atlas mode still renders with existing per-tile sequential rendering when grouped rendering is unavailable.
- [ ] Fix and keep the instanced legacy path current-frame stable:
  - [x] Feed the generated instanced vertex program from the same light shadow-uniform hook used by the geometry path.
  - [x] Generate the instanced vertex shader from a local world-position value instead of reading back the `FragPos` output varying.
  - [ ] Validate against moving-camera logs/screenshots with `GeometryShader` and `InstancedLayered` side by side.
- [ ] Add atlas grouped-cascade allocation support:
  - [ ] Let the directional atlas allocator request a group of active cascade tiles on the same page.
  - [ ] Publish a grouped allocation record containing page index, record indices, inner pixel rects, viewport/scissor indices, and per-cascade UV scale/bias.
  - [ ] Fall back to the existing independent tile requests when a coherent group cannot be allocated.
  - [ ] Keep existing atlas residency, stale-tile fallback, and priority behavior for ungrouped requests.
- [ ] Add OpenGL atlas grouped-render capabilities:
  - [ ] Detect and expose support for viewport/scissor arrays needed by atlas grouped rendering.
  - [ ] Add renderer helpers to push indexed viewport and indexed scissor rectangles for one atlas page render.
  - [ ] Ensure render-area and crop-area state restores correctly after grouped atlas rendering.
- [ ] Add grouped atlas render planning on `DirectionalLightComponent`:
  - [ ] Extend the directional cascade render plan with a backend: legacy texture array vs atlas page.
  - [ ] For atlas mode, choose `InstancedLayered` only when grouped allocation, viewport/scissor arrays, and vertex-stage viewport-index writes are available.
  - [ ] For atlas mode, choose `GeometryShader` only when grouped allocation, viewport/scissor arrays, and geometry-stage viewport-index writes are available.
  - [ ] Otherwise use existing sequential atlas tile rendering.
  - [ ] Publish diagnostics for effective mode, backend, and fallback reason.
- [ ] Implement geometry-shader grouped atlas rendering:
  - [ ] Add a directional cascade atlas geometry shader that loops active cascades.
  - [ ] Write `gl_ViewportIndex` per cascade instead of `gl_Layer`.
  - [ ] Reuse the same cascade world-to-light matrices and shadow material fallback rules used by the legacy geometry path.
  - [ ] Render once into the atlas page depth FBO with indexed viewport/scissor state.
- [ ] Implement instanced grouped atlas rendering:
  - [ ] Add a generated vertex-shader mode for atlas grouped rendering.
  - [ ] Derive cascade index from `gl_InstanceID` only for non-instanced, non-mesh-deformed casters.
  - [ ] Write `gl_ViewportIndex` per cascade instead of `gl_Layer`.
  - [ ] Route already-instanced and mesh-deformed casters through the atlas geometry-shader variant.
  - [ ] Reuse the direct light shadow-uniform hook for vertex-stage cascade matrices.
- [ ] Preserve atlas receiver data:
  - [ ] Keep per-cascade atlas records and UV scale/bias identical to sequential tile rendering.
  - [ ] Verify forward and deferred receivers sample the same atlas page depth texture.
  - [ ] Verify debug cascade colors and atlas tile previews still match assigned records.
- [ ] Add tests:
  - [ ] Source-contract tests for grouped allocation request shape and sequential fallback.
  - [ ] Source-contract tests for atlas geometry shader `gl_ViewportIndex`.
  - [ ] Source-contract tests for atlas instanced vertex `gl_ViewportIndex`.
  - [ ] Tests that atlas mode never routes through legacy texture-array FBOs.
  - [ ] Tests that legacy texture-array modes remain unchanged.
- [ ] Update docs:
  - [ ] `docs/architecture/rendering/default-render-pipeline-notes.md`.
  - [ ] `docs/work/todo/dynamic-shadow-atlas-lod-todo.md`.
  - [ ] Any ImGui/editor setting docs if diagnostics or labels change.
- [ ] Validate:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`.
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`.
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests --no-restore`.
  - [ ] Manual editor pass with atlas off and each cascade mode selected.
  - [ ] Manual editor pass with directional atlas on and each cascade mode selected.
  - [ ] Moving-camera validation with four cascades, masked foliage, skinned casters, already-instanced casters, and cascade transitions.
  - [ ] Compare atlas tile previews against visible receiver shadows for sequential, geometry-shader grouped, and instanced grouped modes.
- [ ] Merge the dedicated branch back into `main` after implementation and validation are complete.

## Current Compatibility Notes

- The legacy texture-array layered implementation is already present in the working tree.
- The current atlas renderer still renders cascades as independent 2D page tiles.
- Atlas grouped rendering must never be required for correctness; it is an acceleration path over the existing atlas tile renderer.
- If any grouped atlas precondition fails, the effective mode should be sequential atlas rendering with a clear fallback reason.
