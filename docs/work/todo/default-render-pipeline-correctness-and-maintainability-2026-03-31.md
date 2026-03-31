# Default Render Pipeline Correctness And Maintainability Follow-Up

Review date: 2026-03-31.

This todo captures the next round of focused work for the default render pipeline after a code review of the current implementation. The goal is to fix correctness gaps that can affect per-camera behavior and pipeline lifetime, while also reducing the amount of duplicated or fragile pipeline orchestration code.

This work is intentionally scoped to the existing render pipeline architecture. It does not introduce new GI features, new AA algorithms, or a new pipeline selection workflow.

---

## Working Rules

- Apply correctness and lifecycle fixes to both `DefaultRenderPipeline` and `DefaultRenderPipeline2` unless a step explicitly says V1-only.
- Use `DefaultRenderPipeline2.CommandChain.cs` as the structural reference when decomposing the older V1 command-chain code.
- Keep render-pass order and visible output unchanged unless a step explicitly fixes incorrect behavior.
- Prefer small, reviewable commits by phase.

---

## Non-Goals

- No GI algorithm rewrite.
- No shader-language migration.
- No dependency upgrades.
- No editor UX changes beyond what is required to validate the pipeline behavior.
- No pipeline-selection changes or new launch flags.

---

## Expected Files To Change

Existing files expected to be touched:

- [XRRenderPipelineInstance.cs](../../../XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs) — add latched per-frame AA state alongside the existing HDR latch.
- [DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs) — fix AA resolution helpers, add lifecycle cleanup, remove hot-path probe rebuild work, and shrink the monolithic command-chain code.
- [DefaultRenderPipeline2.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs) — mirror the same correctness and lifecycle fixes so V2 does not drift from V1.
- [DefaultRenderPipeline2.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs) — reference shape for V1 extraction and any shared command-chain cleanup.
- [DefaultRenderPipeline.Textures.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs) — likely target if texture caching helpers are regrouped out of the monolithic file.
- [VPRC_TemporalAccumulationPass.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs) — ensure any AA-mode lookups route through the same effective per-frame state.
- [VPRC_MarkComplexMsaaPixels.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_MarkComplexMsaaPixels.cs) — same requirement for MSAA sample count resolution.
- [XRObjectBase.cs](../../../XREngine.Data/Core/Objects/XRObjectBase.cs) — lifecycle contract reference only; no functional change expected here unless a helper is needed.

New files likely to be created:

- `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs` — new partial file so V1 command-chain construction stops living in the main class file.
- `XRENGINE/Rendering/Pipelines/Commands/Features/GI/VPRC_SyncLightProbeResources.cs` — dedicated once-per-frame probe sync command so probe rebuilds stop happening inside shader uniform binding.
- `XRENGINE/Rendering/Pipelines/RenderPipelineAntiAliasingResources.cs` — shared AA resource-group helper so V1 and V2 stop carrying separate invalidation lists.

---

## Phase 1 — Latch Effective AA State Per Frame

### Problem

The pipeline already latches effective HDR output per frame, but anti-aliasing mode and MSAA sample count are still resolved from live camera state. That makes the AA behavior less robust during nested quad renders, null-push camera scopes, and scene/light-probe capture paths.

### What Will Change

- [x] Add `EffectiveAntiAliasingModeThisFrame` to `XRRenderPipelineInstance`.
- [x] Add `EffectiveMsaaSampleCountThisFrame` to `XRRenderPipelineInstance`.
- [x] Add `EffectiveTsrRenderScaleThisFrame` to `XRRenderPipelineInstance` so TSR scale is resolved once per frame rather than being recomputed from live state in multiple places.
- [x] Populate those fields inside `XRRenderPipelineInstance.Render(...)` at the same point where `EffectiveOutputHDRThisFrame` is assigned.
- [x] Update `DefaultRenderPipeline.ResolveAntiAliasingMode()` to prefer the latched value first, then fall back to the same camera fallback chain used by HDR resolution.
- [x] Update `DefaultRenderPipeline.ResolveEffectiveMsaaSampleCount()` the same way.
- [x] Update V2 to use the same latching logic so both pipelines resolve AA the same way.
- [x] Audit direct AA lookups in pipeline commands and route them through shared helpers instead of re-reading live camera override state.
- [x] Confirm `GetRequestedInternalResolutionForCamera(...)` and TSR-related decisions stay consistent with the new per-frame latched values.

Status:

- Phase 1 code changes completed on 2026-03-31.
- `Build Editor Validation` succeeded after the changes.
- Runtime smoke validation for FXAA, SMAA, TSR, and MSAA remains pending under Phase 6.

### Intended Outcome

AA mode, MSAA sample count, and TSR scale become frame-stable in the same way HDR output already is. Nested renders can no longer accidentally reinterpret the active camera AA settings partway through a frame.

---

## Phase 2 — Add Proper Pipeline Teardown And Event Cleanup

### Problem

Both default pipelines subscribe to global rendering events in their constructors, but there is no visible matching cleanup path in the pipeline classes. The engine object model expects event unregistration in `OnDestroying()`, so the current implementation risks stale callbacks, retained pipeline instances, and cleanup that only happens implicitly.

### What Will Change

- [x] Add `protected override void OnDestroying()` to `DefaultRenderPipeline`.
- [x] Add `protected override void OnDestroying()` to `DefaultRenderPipeline2`.
- [x] Unsubscribe `Engine.Rendering.SettingsChanged` in both overrides.
- [x] Unsubscribe `Engine.Rendering.AntiAliasingSettingsChanged` in both overrides.
- [x] Call `ClearProbeResources()` during destruction so owned buffers, texture arrays, and jobs are explicitly released.
- [x] Ensure any outstanding probe tessellation job is canceled as part of teardown.
- [x] Call `base.OnDestroying()` after pipeline-specific cleanup.
- [x] Add `IsDestroyed` guards to queued settings callbacks if needed so an already-destroyed pipeline cannot rebuild its command chain from a delayed main-thread invoke.

Status:

- Phase 2 code changes completed on 2026-03-31.
- `Build Editor Validation` succeeded after the changes.
- Runtime validation that destroys or swaps pipelines during an editor session remains pending under Phase 6.

### Intended Outcome

Pipeline lifetime becomes explicit. Destroyed or swapped pipelines stop receiving engine-wide events, and probe-related resources are released in the same lifecycle path rather than only by ad hoc calls.

---

## Phase 3 — Move Probe Resource Rebuilds Out Of Uniform Binding

### Problem

`BindPbrLightingResources(...)` currently mixes three different responsibilities: deciding whether probe resources are dirty, rebuilding probe resources, and binding the final resources to a program. That makes a program-bind path do allocation, cache invalidation, and background-job scheduling, which is the wrong place for work that should happen once per frame at most.

### What Will Change

- [ ] Add a dedicated render command, `VPRC_SyncLightProbeResources`, under the GI command feature area.
- [ ] Move `_pendingProbeRefresh`, `ProbeConfigurationChanged(...)`, and `BuildProbeResources(...)` decision-making behind that command so probe sync happens once before the lighting pass.
- [ ] Keep `BuildProbeResources(...)` and `ClearProbeResources()` in the pipeline class initially, but invoke them from the new command rather than from `BindPbrLightingResources(...)`.
- [ ] Update the lighting command-chain in both pipelines so the sync command runs immediately before the light-combine path that consumes probe resources.
- [ ] Reduce `BindPbrLightingResources(...)` to a read-only bind step: it should only bind textures/buffers, set uniforms, and return whether resources are currently usable.
- [ ] Preserve current debug logging, but keep logging on the sync path rather than the hot bind path whenever possible.
- [ ] Ensure tetrahedralization job launch remains tied to resource sync, not repeated program bindings.

### Intended Outcome

Probe resource generation becomes deterministic and frame-scoped. Uniform binding no longer causes hidden resource rebuilds or job scheduling, and repeated program binds within the same frame reuse stable probe resources.

---

## Phase 4 — Decompose V1 Command-Chain Construction To Match V2 Shape

### Problem

The main `DefaultRenderPipeline.cs` file still contains a very large `CreateViewportTargetCommands()` implementation and related helper logic that already exists in a cleaner decomposed form in V2. This increases drift, makes defects harder to spot, and has already allowed small inconsistencies such as duplicate FXAA output texture caching logic.

### What Will Change

- [ ] Create `DefaultRenderPipeline.CommandChain.cs` as a new partial file.
- [ ] Move `GenerateCommandChain()`, `CreateViewportTargetCommands()`, `CreateFBOTargetCommands()`, `CreateFinalBlitCommands()`, and `CreateVendorUpscaleCommands()` out of the main class file into that new partial.
- [ ] Port the same `Append*` helper structure already used by `DefaultRenderPipeline2.CommandChain.cs` into V1.
- [ ] Break V1 viewport-target construction into named helpers such as `AppendAmbientOcclusionSwitch`, `AppendDeferredGBufferPass`, `AppendLightingPass`, `AppendForwardPass`, `AppendTransparencyPasses`, and `AppendFinalOutput`.
- [ ] Move `CacheTextures(...)` into grouped helpers so texture caching is split by subsystem rather than remaining one long method.
- [ ] Remove the duplicated FXAA output texture caching path so the FXAA output resource has exactly one owner in V1.
- [ ] Keep command order, clear behavior, and pass dependencies unchanged unless required by the correctness fixes in other phases.

### Intended Outcome

V1 becomes readable in the same way as V2, and V1/V2 differences are reduced to deliberate behavior differences rather than accidental structural divergence.

---

## Phase 5 — Centralize AA Resource Invalidation Behind One Shared Helper

### Problem

AA invalidation currently depends on hand-maintained lists of texture and framebuffer names embedded directly in the pipeline classes. That is fragile because every new AA-owned resource has to be remembered in multiple places, and V1/V2 can silently drift.

### What Will Change

- [ ] Introduce a shared helper file, `RenderPipelineAntiAliasingResources.cs`, that owns the authoritative list of AA-related texture and framebuffer resource names.
- [ ] Move the current V1 AA dependency arrays into that shared helper.
- [ ] Update V2 to consume the same helper instead of carrying its own copy of the resource list and invalidation logic.
- [ ] Expose a shared invalidation method that removes all AA-owned textures and framebuffers from a `XRRenderPipelineInstance` resource registry.
- [ ] Ensure FXAA, SMAA, TSR, temporal history, MSAA deferred, forward MSAA depth, and related AA support resources are all represented in one place.
- [ ] Remove duplicate per-class AA invalidation lists once the shared helper is in place.

### Intended Outcome

AA invalidation becomes a single-source-of-truth operation. Adding or removing an AA resource changes one helper, not several arrays scattered across both pipeline classes.

---

## Phase 6 — Regression Coverage And Validation

### Unit-Level Coverage

- [ ] Add or extend targeted rendering tests for AA helper resolution so per-frame AA latching and fallback-camera behavior are covered by unit tests.
- [ ] Add a focused test for pipeline teardown if a practical harness exists, at minimum verifying that destroying a pipeline does not leave global event subscriptions active.

### Build Validation

- [ ] Run `Build-Editor` after each phase.
- [ ] Run `Build Editor Validation` before final handoff.

### Runtime Validation

- [ ] Start the editor and validate non-AA, FXAA, SMAA, TSR, and MSAA camera paths still render correctly.
- [ ] Validate a nested render path such as post-process quad rendering or light-probe capture to confirm AA state stays frame-stable.
- [ ] Validate a light-probe scene to confirm probe resources rebuild once when probes move or change textures, and not during every program bind.
- [ ] Destroy or swap a pipeline instance during a normal editor session and confirm no duplicate settings callbacks or repeated command-chain rebuilds occur afterward.
- [ ] Resize the viewport and switch AA settings to confirm shared AA invalidation removes and recreates the correct resources.

---

## Recommended Commit Order

1. Latch per-frame AA state.
2. Add lifecycle teardown and event cleanup.
3. Move probe sync out of uniform binding.
4. Decompose V1 command-chain construction.
5. Centralize AA invalidation.
6. Add regression coverage and run final validation.

---

## Exit Criteria

This todo is complete when all of the following are true:

- AA state is latched per frame the same way HDR is already latched.
- Destroyed pipelines no longer retain global rendering callbacks.
- Probe resource rebuilds no longer happen from `BindPbrLightingResources(...)`.
- V1 command-chain construction is split into named helpers rather than remaining monolithic.
- V1 and V2 share a single AA invalidation source of truth.
- Editor build succeeds and runtime smoke checks pass for AA modes, light probes, and pipeline destruction/recreation.