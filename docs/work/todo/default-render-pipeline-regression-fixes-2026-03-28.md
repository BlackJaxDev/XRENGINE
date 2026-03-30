# Default Render Pipeline Regression Fixes

Tracked regressions confirmed against `git diff HEAD` on 2026-03-28.
Working tree already contains partial fixes for all items below — each step is about verifying, completing, and committing that fix cleanly.

Reference:

- [Regression diagnosis](../audit/default-render-pipeline-regression-diagnosis-2026-03-28.md)

---

## Fix 1 — Deferred geometry routing: AO FBO → Deferred GBuffer FBO

**Symptoms:** Deferred textured meshes render grayscale; MSAA deferred resolve feeds the wrong destination.

**Root cause:** In committed HEAD, the non-MSAA deferred geometry render target and the MSAA GBuffer resolve destination were both set to `AmbientOcclusionFBOName` instead of `DeferredGBufferFBOName`.

**Files:**

- [DefaultRenderPipeline.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs) — main default pipeline command chain / cache entry
- [DefaultRenderPipeline2.CommandChain.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs) — `AppendDeferredGBufferPass`
- [DefaultRenderPipeline2.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs) — deferred GBuffer recreation validator
- [DefaultRenderPipeline.FBOs.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs) — `CreateDeferredGBufferFBO`
- [DefaultRenderPipeline2.FBOs.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.FBOs.cs) — `CreateDeferredGBufferFBO`

**Steps:**

- [x] Confirm `AppendDeferredGBufferPass` uses `DeferredGBufferFBOName` for both the non-MSAA dynamic target and the MSAA resolve destination
- [x] Confirm `CreateDeferredGBufferFBO` exists in both FBOs partial classes with correct color/depth attachments (albedo, normal, RMSE, transform ID, depth-stencil)
- [x] Confirm the FBO cache/create entry in the command chain calls `NeedsRecreateDeferredGBufferFbo` (not the generic MSAA validator)
- [ ] Run with deferred + non-MSAA: verify textured meshes show correct albedo colors, not grayscale
- [ ] Run with MSAA deferred: verify the same

**Status:**

- Code is already present in the working tree for both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -nologo` succeeded on 2026-03-28.
- Runtime verification for deferred non-MSAA and deferred MSAA is still pending.

---

## Fix 2 — GLMaterial fallback sampler binding runs too early

**Symptoms:** Repeated `GL_INVALID_OPERATION: program texture usage` on mesh draw calls; probe/BRDF samplers fall back even when GI resources are loaded.

**Root cause:** `SetUniforms()` called `BindFallbackSamplers()` before mesh- and FBO-level `SettingUniforms` hooks had run, leaving late-bound samplers invisible to the fallback allocator. Additionally, raw `textureIndex` was used as the GL texture unit, causing collisions with fixed-unit pipeline bindings.

**Files:**

- [GLMaterial.cs](../../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs) — `SetUniforms`, `FinalizeUniformBindings`, `SetTextureUniforms`
- [GLMeshRenderer.Rendering.cs](../../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh%20Renderer/GLMeshRenderer.Rendering.cs) — mesh draw path now calls `FinalizeUniformBindings(...)` after `OnSettingUniforms(...)`
- [OpenGLRenderer.cs](../../../../XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs) — generic material uniform path also calls `FinalizeUniformBindings(...)`

**Steps:**

- [x] Confirm `SetUniforms()` no longer calls `BindFallbackSamplers()` inline
- [x] Confirm a new `FinalizeUniformBindings()` method exists and is called after all `SettingUniforms` hooks complete
- [x] Confirm `SetTextureUniforms()` compacts texture units to contiguous bound slots (skipping null/missing textures) rather than using raw `textureIndex`
- [ ] Verify fallback sampler binding no longer fires for `IrradianceArray`, `PrefilterArray`, and `BRDF` when GI probes are loaded
- [ ] Check OpenGL log: confirm `GL_INVALID_OPERATION` errors on mesh draws are gone

**Status:**

- Code is already present in the working tree.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -nologo` succeeded on 2026-03-29.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~GLMaterialTextureBindingContractTests" -nologo` passed on 2026-03-29.
- Runtime verification against a fresh OpenGL log with GI probes loaded is still pending.

---

## Fix 3 — SMAA and final present re-resolve SourceTexture during uniform binding

**Symptoms:** SMAA and TSR make the default render pipeline go black.

**Root cause:** `VPRC_SMAA` and `VPRC_RenderToWindow` each re-resolved their source texture inside `SettingUniforms` callbacks rather than caching it before the draw. If the source FBO or texture lifetime changed between render setup and the uniform callback, the wrong or null texture was sampled.

**Files:**

- [VPRC_SMAA.cs](../../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SMAA.cs) — `Execute`, `Edge_SettingUniforms`, `Neighborhood_SettingUniforms`
- [VPRC_RenderToWindow.cs](../../../../XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs) — `Execute`, `Present_SettingUniforms`

**Steps:**

- [x] Confirm `VPRC_SMAA.Execute()` caches source texture in `_resolvedSourceTexture` before calling any quad renders, and clears it in `finally`
- [x] Confirm `Edge_SettingUniforms` and `Neighborhood_SettingUniforms` read `_resolvedSourceTexture` first, falling back to re-resolve only as a guard (and suppressing the fallback warning when falling back)
- [x] Confirm `VPRC_RenderToWindow.Execute()` does the same with its `_resolvedSourceTexture`
- [ ] Run with SMAA enabled: verify scene renders correctly, not black
- [ ] Run with TSR enabled: verify scene renders correctly, not black

**Status:**

- Code is already present in the working tree for both `VPRC_SMAA` and `VPRC_RenderToWindow`.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -nologo` succeeded on 2026-03-29.
- No dedicated SMAA / present runtime regression test currently exists in `XREngine.UnitTests`; runtime smoke validation for SMAA and TSR is still pending.

---

## Fix 4 — AO mode evaluation ignores non-SceneCamera paths

**Symptoms:** All AO modes disabled on render paths where `State.SceneCamera` is null (e.g. rendering camera, last-used camera fallback).

**Root cause:** `EvaluateAmbientOcclusionMode()` in committed HEAD read AO settings directly from `State.SceneCamera` with no fallback, returning `AmbientOcclusionDisabledMode` if it was null.

**Files:**

- [DefaultRenderPipeline.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs) — `EvaluateAmbientOcclusionMode`, `ResolveAmbientOcclusionSettings`
- [DefaultRenderPipeline2.CommandChain.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs) — `EvaluateAmbientOcclusionMode`
- [DefaultRenderPipeline2.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs) — `ResolveAmbientOcclusionSettings`

**Steps:**

- [x] Confirm `ResolveAmbientOcclusionSettings()` exists in both pipeline classes and walks: `SceneCamera ?? RenderingCamera ?? LastSceneCamera ?? LastRenderingCamera`
- [x] Confirm `EvaluateAmbientOcclusionMode()` in both classes calls `ResolveAmbientOcclusionSettings()` instead of accessing `State.SceneCamera` directly
- [x] Confirm `ShouldUseAmbientOcclusion()` also calls `ResolveAmbientOcclusionSettings()`
- [ ] Test AO with a render path that uses `RenderingCamera` rather than `SceneCamera`: verify AO is not silently disabled

**Status:**

- Code is already present in the working tree for both pipeline classes.
- Regression coverage now checks the fallback camera chain and `ShouldUseAmbientOcclusion()` helper usage in `AlphaToCoveragePhase2Tests`.
- Runtime validation for a `RenderingCamera`-only path is still pending.

---

## Fix 5 — SSAO and MVAO noise textures bound to wrong sampler name

**Symptoms:** AO output looks flat, wrong, or spatially unstable even when the SSAO/MVAO pass runs.

**Root cause:** Noise textures were created with `SamplerName` set to the resource name string (`SSAONoiseTextureName`) instead of the GLSL uniform name the shader expects (`AONoiseTexture`).

**Files:**

- [VPRC_SSAOPass.cs](../../../../XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs) — `GetOrCreateNoiseTexture`
- [VPRC_MVAOPass.cs](../../../../XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs) — `GetOrCreateNoiseTexture`

**Steps:**

- [ ] Confirm `VPRC_SSAOPass.GetOrCreateNoiseTexture()` sets `SamplerName = "AONoiseTexture"`
- [ ] Confirm `VPRC_MVAOPass.GetOrCreateNoiseTexture()` sets `SamplerName = "AONoiseTexture"`
- [ ] Check the SSAO and MVAO generation shaders: confirm the noise sampler uniform is named `AONoiseTexture`
- [ ] Run with SSAO enabled: verify the AO pattern has visible spatial noise/rotation (not flat)

---

## Fix 6 — MSAA light combine FBO uses the wrong recreation validator

**Symptoms:** Stale MSAA light-combine FBO after format or attachment changes; MSAA-specific black or invalid resolve output.

**Root cause:** The MSAA light-combine FBO cache/create entry used the generic `NeedsRecreateMsaaFbo` validator, which doesn't account for light-combine-specific format requirements.

**Files:**

- [DefaultRenderPipeline2.CommandChain.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs) — MSAA light combine `VPRC_CacheOrCreateFBO` entry
- [DefaultRenderPipeline2.cs](../../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs) — `NeedsRecreateMsaaLightCombineFbo`

**Steps:**

- [ ] Confirm `NeedsRecreateMsaaLightCombineFbo(...)` method exists and is distinct from `NeedsRecreateMsaaFbo`
- [ ] Confirm the `VPRC_CacheOrCreateFBO` entry for `MsaaLightCombineFBOName` references `NeedsRecreateMsaaLightCombineFbo`
- [ ] Trigger a viewport resize or MSAA sample-count change at runtime: verify the light-combine FBO recreates correctly without a black frame

---

---

## Fix 7 — BindFallbackSamplers overrides layout(binding=X) GBuffer samplers in LightCombine

**Symptoms:** Deferred scene still renders grayscale even after Fix 1 is applied.

**Root cause:** `BindFallbackSamplers()` overwrites the LightCombine shader's `layout(binding=X)` GBuffer samplers with fallback 1×1 textures every frame.

The chain of failure:

1. `SetTextureUniforms()` binds GBuffer textures (AlbedoOpacity, Normal, RMSE, etc.) to units 0–5 using the textures' `.SamplerName` properties (`"AlbedoOpacityTexture"`, `"NormalTexture"`, etc.), which do NOT match the shader uniform names (`"AlbedoOpacity"`, `"Normal"`, etc.)
2. `glGetUniformLocation("AlbedoOpacityTexture")` returns -1 → the binding at unit 0 is **never** recorded in `_boundSamplerLocations`
3. `BindFallbackSamplers()` iterates `_uniformMetadata`, finds `"AlbedoOpacity"` is "unbound" (not in `_boundSamplerNames`, not in `_boundSamplerLocations`), and calls `glUniform1i(location_of_AlbedoOpacity, fallbackUnit)` — overriding the `layout(binding=0)` default with a fallback 1×1 texture
4. Same override fires for `"Normal"`, `"RMSE"`, `"AmbientOcclusionTexture"`, `"DepthView"`, `"LightingTexture"` → the LightCombine shader reads from all-fallback inputs regardless of what was written to the GBuffer

This explains why Fix 1's code is correct but the grayscale persists: the GBuffer is written correctly, but the LightCombine shader can't read it because its sampler uniforms were replaced.

**Fix:** In `BindFallbackSamplers()` ([GLRenderProgram.UniformBinding.cs](../../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs)), before binding a fallback, query the uniform's currently assigned texture unit via `glGetUniformiv`. If that unit is already occupied in `_boundSamplerUnits`, skip the fallback — the sampler is already served by a real texture via the `layout(binding=X)` path.

```csharp
int location = GetUniformLocation(name);
if (location < 0 || _boundSamplerLocations.ContainsKey(location))
    continue;

// NEW: if the sampler's currently assigned unit (layout(binding=X) default or prior glUniform1i)
// is already occupied, a real texture is serving it — don't overwrite with a fallback.
Api.GetUniform(BindingId, location, out int assignedUnit);
if (assignedUnit >= 0 && _boundSamplerUnits.ContainsKey(assignedUnit))
    continue;
```

This fix is general and handles any `layout(binding=X)` shader without requiring the caller to use matching `SamplerName` values.

**Files:**

- [GLRenderProgram.UniformBinding.cs](../../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs) — `BindFallbackSamplers()`

**Steps:**

- [ ] Add the `glGetUniformiv` guard in `BindFallbackSamplers()` after the existing `_boundSamplerLocations` check
- [ ] Verify no fallback sampler fires for `AlbedoOpacity`, `Normal`, `RMSE`, `AmbientOcclusionTexture`, `DepthView`, or `LightingTexture` in the LightCombine pass
- [ ] Run deferred scene: verify textured meshes now show correct albedo colors (not grayscale)

---

## Commit order

Recommended commit sequence (each fix is independently testable):

1. Fix 1 + supporting FBO creation (deferred routing — highest visual impact)
2. Fix 2 (GLMaterial texture unit compaction + deferred fallback binding)
3. Fix 3 (SMAA + RenderToWindow source texture caching)
4. Fix 4 (AO camera resolution fallback)
5. Fix 5 (noise texture sampler name)
6. Fix 6 (MSAA light combine validator)
