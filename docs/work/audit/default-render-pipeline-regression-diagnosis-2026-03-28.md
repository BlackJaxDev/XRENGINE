# Default Render Pipeline Regression Diagnosis - 2026-03-28

Reference work docs:
- [Default render pipeline V2 TODO](../todo/default-render-pipeline-v2-todo.md)
- [Ambient Occlusion](../../features/gi/ambient-occlusion.md)
- [Ambient Occlusion Testing](../testing/ambient-occlusion.md)

## Scope

Investigate recent regressions in the default render pipeline:

- all AO modes appear broken
- MSAA, SMAA, and TSR can turn the default render pipeline black
- deferred textured meshes can render in grayscale

This note is based on committed `HEAD`, targeted `git blame` / `git log` review, and runtime evidence from the March 28, 2026 logs. The working tree is currently dirty, and several uncommitted edits in the suspect files already look like partial fixes; this document calls that out where it matters.

## Executive Summary

This does not look like a single bug.

The strongest diagnosis is that several recent regressions overlap:

1. committed `HEAD` routes deferred geometry into the AO path instead of a dedicated non-MSAA deferred GBuffer
2. the AO mode evaluator was narrowed to `State.SceneCamera`, which can disable AO depending on camera ownership
3. OpenGL sampler binding now has a late-binding / fallback-binding ordering problem, with real runtime `GL_INVALID_OPERATION` errors on mesh draws
4. the fullscreen AA / present commands resolve `SourceTexture` too late, which is a strong explanation for SMAA and TSR presenting black
5. SSAO and MVAO noise textures no longer bind to the shader's expected sampler name

The first four items are enough to explain the observed AO, AA, and deferred regressions without needing a single root cause.

## Runtime Evidence

### OpenGL log

The most important runtime evidence is in:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_opengl.txt`

Key pattern:

- line 108: fallback sampler bound for `IrradianceArray` at texture unit 24
- line 109: fallback sampler bound for `PrefilterArray` at texture unit 25
- line 110: fallback sampler bound for `BRDF` at texture unit 26
- line 111: `GL_INVALID_OPERATION error generated. State(s) are invalid: program texture usage.`
- lines 114-116: stack lands in `OpenGLRenderer.RenderCurrentMesh(...)` and `GLMeshRenderer.Render(...)`

That same pattern repeats across many mesh draws in the same frame range.

### Rendering log

Related evidence from:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_rendering.txt`

Relevant observations:

- line 36 and later lines 50, 53, 60-64: `ProbeGI` reports probes bound successfully in the same session
- lines 62-65: the SSAO pass executes and generates samples, so AO is not failing only because the pass never runs

That matters because it weakens the idea that the repeated fallback bindings for `IrradianceArray`, `PrefilterArray`, and `BRDF` are just expected "resource missing" behavior. In this session, probe resources do become ready and AO does execute.

## Findings

### 1. Deferred geometry is bound to the AO FBO instead of a dedicated deferred GBuffer

Confidence: confirmed in committed `HEAD`

Committed `HEAD` binds deferred geometry to `AmbientOcclusionFBOName` when non-MSAA deferred is active, and resolves the MSAA deferred GBuffer into that same AO FBO. The current local worktree changes both sites to `DeferredGBufferFBOName` and adds a dedicated deferred GBuffer cache/create path.

Relevant files:

- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs)

Why this is important:

- `AmbientOcclusionFBOName` is part of the AO generation / blur path, not the deferred geometry target
- the dedicated `DeferredGBufferFBOName` exists specifically for albedo, normal, RMSE, transform ID, and depth-stencil
- the AO resolve chain later blurs `AmbientOcclusionFBOName` into `AmbientOcclusionBlurFBOName` and then into `GBufferFBOName`

Likely symptoms explained:

- deferred textured meshes rendering incorrectly or grayscale
- AO reading the wrong deferred inputs
- MSAA deferred resolve feeding the wrong destination

Likely regression commit:

- `c2621be8` on 2026-03-17

### 2. OpenGL sampler binding is failing on real mesh draws

Confidence: confirmed runtime failure, with the exact root cause still partly inferred

The OpenGL log shows repeated fallback sampler binding for probe and BRDF samplers immediately followed by `GL_INVALID_OPERATION` on actual mesh draw calls. This is not just a warning-only situation; the driver is rejecting the texture usage state during rendering.

Relevant files:

- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLMaterial.cs)
- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs)
- [XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs](../../../XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.UniformBinding.cs)
- [Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_opengl.txt](../../../Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_opengl.txt)
- [Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_rendering.txt](../../../Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-03-28_22-42-45_pid11616/log_rendering.txt)

Strongest explanation from current code and local fixes:

- `GLMaterial.SetUniforms(...)` currently binds fallback samplers before later mesh / FBO `SettingUniforms` hooks have finished
- the local worktree introduces `FinalizeUniformBindings(...)` so fallback binding happens after the full binding batch
- `GLMaterial.SetTextureUniforms(...)` in committed `HEAD` uses the raw material `textureIndex` as the GL texture unit
- the local worktree compacts material texture units to contiguous bound units instead

Why that matters:

- fixed-unit bindings now exist for other pipeline resources
- fallback samplers start at texture unit 24 in `GLRenderProgram.UniformBinding.cs`
- if real material and late-bound samplers are not fully visible before fallback allocation runs, the program can end up with invalid or conflicting texture state
- the rendering log shows probe GI resources are available in the session, so repeated fallback binding for `IrradianceArray` / `PrefilterArray` / `BRDF` should not be the steady-state outcome

Likely symptoms explained:

- black output from some AA or composite paths after bad sampler state propagates
- invalid mesh shading and incorrect deferred texturing
- repeated driver errors during scene draws

Likely regression window:

- `c25b410e` on 2026-03-17 introduced the fallback-binding path that now appears to interact badly with later sampler bindings
- newer probe / lighting work after that made the collision more visible

### 3. Fullscreen AA and final present resolve `SourceTexture` too late

Confidence: strong inference from committed `HEAD` and the current local fix

In committed `HEAD`, `VPRC_SMAA` and `VPRC_RenderToWindow` re-resolve `SourceTexture` inside `SettingUniforms` instead of capturing the resolved source texture before the fullscreen draw. The local worktree adds `_resolvedSourceTexture` caching in both commands.

Relevant files:

- [XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SMAA.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SMAA.cs)
- [XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs](../../../XRENGINE/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs)

Why this is a strong black-frame candidate:

- the default pipeline presents through `VPRC_RenderToWindow`
- SMAA is implemented as a multi-pass fullscreen chain that depends on the same source texture remaining stable across the pass
- if `SourceTexture` is re-resolved after the pass has already switched targets or lifetimes, the fullscreen material can sample the wrong texture, no texture, or a fallback texture

Likely symptoms explained:

- SMAA turns the scene black
- TSR turns the scene black
- final present can show invalid output even when earlier passes produced valid textures

Likely regression commit:

- `2a61d0a8` on 2026-03-22 is the most likely AA-chain regression point

### 4. AO mode evaluation was narrowed to `State.SceneCamera`

Confidence: confirmed code regression, but not sufficient by itself to explain this entire session

Committed `HEAD` evaluates AO mode through `State.SceneCamera?.GetPostProcessStageState<AmbientOcclusionSettings>()` instead of using the broader `ResolveAmbientOcclusionSettings()` helper that falls back through rendering and last-used camera state. The current local worktree restores the helper call.

Relevant files:

- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs)

Why this matters:

- it can disable AO when the effective render camera is not `State.SceneCamera`
- it is a real regression in committed `HEAD`

Important nuance:

- the March 28 rendering log shows the SSAO pass executing in this session
- that means this camera-selection regression is not required to explain the reproduced failure here
- it should still be treated as a valid bug because it can break AO depending on camera ownership and render path

Likely regression commit:

- `c2621be8` on 2026-03-17

### 5. SSAO and MVAO noise textures bind to the wrong sampler name

Confidence: confirmed

Committed `HEAD` creates the SSAO and MVAO noise textures with the resource name as the sampler name. The current local worktree changes both to shader-expected `AONoiseTexture`.

Relevant files:

- [XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_SSAOPass.cs)
- [XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/AO/VPRC_MVAOPass.cs)

Likely symptoms explained:

- SSAO noise sampling degrades or fails
- MVAO noise sampling degrades or fails
- AO output can look flat, wrong, or unstable even when the pass runs

This is a real AO regression, but it looks smaller than the deferred routing and sampler-binding failures above.

### 6. MSAA light combine FBO recreation uses the generic MSAA validator

Confidence: confirmed secondary issue

The current local worktree adds a specialized `NeedsRecreateMsaaLightCombineFbo(...)` and switches the MSAA light-combine FBO cache sites to use it. Committed `HEAD` still uses the generic `NeedsRecreateMsaaFbo(...)`.

Relevant files:

- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.cs)
- [XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs)

Likely symptoms explained:

- stale MSAA light-combine resources after format or attachment changes
- MSAA-specific black or invalid resolve output

This looks like an amplifier for the MSAA regression, not the main root cause by itself.

### 7. Deferred light combine alpha handling changed, but it does not look like the primary grayscale bug

Confidence: confirmed code change, low confidence as primary regression source

The local worktree changes deferred light combine output alpha from hardcoded `1.0` to `albedoOpacity.a`.

Relevant file:

- [Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs](../../../Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs)

This looks more like a transparency / output correctness cleanup than the root cause of grayscale deferred texturing. It is worth keeping in mind, but the deferred GBuffer routing error is the stronger explanation.

## Symptom Mapping

### All AO modes no longer work

Most likely combined causes:

- deferred geometry routed into the AO path instead of the dedicated deferred GBuffer
- SSAO / MVAO noise sampler mismatch
- AO camera-resolution regression in paths that rely on rendering or last-used camera state

Important nuance:

- the rendering log shows SSAO executing, so at least some AO failures are happening after scheduling, not only before it

### MSAA makes the default render pipeline go black

Most likely combined causes:

- deferred / AO routing corruption in the default pipeline
- OpenGL sampler binding failures during scene draws
- stale or invalid MSAA light-combine FBO validation

### SMAA makes the default render pipeline go black

Most likely combined causes:

- `VPRC_SMAA` re-resolving `SourceTexture` too late
- OpenGL sampler binding failures interfering with fullscreen material state

### TSR makes the default render pipeline go black

Most likely combined causes:

- `VPRC_RenderToWindow` re-resolving `SourceTexture` too late on the final present path
- the same underlying sampler / source-texture lifetime issues affecting the fullscreen AA chain

### Deferred meshes render textures in grayscale

Most likely combined causes:

- deferred geometry targeting the AO FBO instead of the dedicated deferred GBuffer
- draw-time OpenGL sampler state errors on mesh materials

## Likely Regression Commits

Most likely timeline:

- `c2621be8` on 2026-03-17
  - introduced the default-pipeline deferred routing and AO camera-selection regressions
- `c25b410e` on 2026-03-17
  - introduced the fallback-binding path that now appears to conflict with late sampler binds
- `2a61d0a8` on 2026-03-22
  - likely introduced the current fullscreen AA / present black-frame regressions
- later probe / lighting work in the March 25 range
  - appears to have made the sampler-binding failure easier to trigger and easier to see

## Working Tree Note

The current working tree already contains uncommitted edits in the same files named above. Several of those changes look like direct fixes for the diagnosed issues rather than unrelated work:

- deferred routing now targets `DeferredGBufferFBOName`
- AO mode evaluation now uses `ResolveAmbientOcclusionSettings()`
- `GLMaterial` now delays fallback binding until the full uniform batch is done
- material textures now use compact texture-unit assignment instead of raw sparse indices
- `VPRC_SMAA` and `VPRC_RenderToWindow` now cache the resolved source texture before fullscreen draws
- SSAO and MVAO noise textures now use sampler name `AONoiseTexture`

Those local edits support the diagnosis, but they are not yet part of committed `HEAD`.

## Validation Status

No clean build or targeted unit-test run was executed for this audit note.

Reason:

- the working tree is already dirty with in-progress rendering changes in the same subsystem
- this note was written as a diagnosis and evidence capture pass, not as a clean validation handoff

Good targeted follow-up once the worktree is stabilized:

- `dotnet test .\\XREngine.UnitTests\\XREngine.UnitTests.csproj --filter "FullyQualifiedName~GLMaterialTextureBindingContractTests|FullyQualifiedName~ForwardAmbientOcclusionShaderTests|FullyQualifiedName~AlphaToCoveragePhase2Tests"`

