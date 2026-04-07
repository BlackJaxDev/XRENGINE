# Rendering Regression Fixes

Review date: 2026-04-06.

This todo captures fixes for four outstanding rendering issues discovered through
deep static analysis and verification of the render pipeline, shader code, and
model importer. Two issues (MSAA black screen, TSR black screen) are regressions
from previously working behavior. Two issues (volumetric fog noise, incorrect
TexturedNormalDeferred normals) are long-standing quality gaps.

---

## Working Rules

- Fix the high-confidence issues (normals, fog) first as quick wins.
- For the medium-confidence issues (MSAA, TSR), instrument first, reproduce, then fix.
- Apply pipeline-level fixes to both `DefaultRenderPipeline` and `DefaultRenderPipeline2`.
- Keep render-pass order and visible output unchanged unless a step explicitly fixes incorrect behavior.
- Run the editor after each fix to validate visually.

---

## Non-Goals

- No new AA algorithms, temporal denoising passes, or GI changes.
- No shader-language migration.
- No editor UX changes beyond validation.

---

## Issue 1: TexturedNormalDeferred Incorrect Normals

**Confidence: High** — root cause verified in code with documented regression history.

### Root Cause

`ImportedHeightMapScale` at [ModelImporter.cs line 565](../../../XRENGINE/Core/ModelImporter.cs#L565)
is `2.0f`. This was previously fixed to `1.0f` (matching the shader default at
[SurfaceDetailNormalMapping.glsl line 13](../../../Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl#L13))
but has regressed. A scale of 2.0 on the Sobel 3×3 kernel amplifies slopes so
much that reconstructed normals tilt 45-60° from the surface on high-contrast
bump maps (e.g. Sponza lion relief), driving `N·L ≈ 0` across most fragments.

### Secondary Issue

The filename heuristic in `InferTextureTypeFromFilePath` at
[ModelImporter.cs lines 587-589](../../../XRENGINE/Core/ModelImporter.cs#L587-L589)
only matches the substring `"normal"`. Common normal-map naming conventions are
not detected:

| Pattern | Example Filenames | Currently Detected? |
|---------|-------------------|---------------------|
| `normal` | `wall_normal.png` | Yes |
| `norm` | `wall_norm.png` | No |
| `nrm` | `brick_nrm.tga` | No |
| `_nm` | `floor_nm.png` | No |
| `_n.` | `metal_n.png` | No |

Unrecognized files fall through to Assimp's type classification, which may
expose OBJ `map_bump` entries as `TextureType.Height` — causing real RGB normal
maps to be processed through the Sobel height-to-normal path and producing
entirely wrong normals.

### Verified Non-Issues

- **TBN matrix construction**: [SurfaceDetailNormalMapping.glsl lines 40-92](../../../Build/CommonAssets/Shaders/Snippets/SurfaceDetailNormalMapping.glsl#L40-L92)
  has extensive guards (degenerate-vector detection, Gram-Schmidt re-orthogonalization,
  NaN guards, Z-clamping). Not a root cause.
- **GBuffer normal encoding**: Octahedral encoding in [NormalEncoding.glsl](../../../Build/CommonAssets/Shaders/Snippets/NormalEncoding.glsl)
  is correct and `TexturedNormalDeferred.fs` calls it properly.
- **Importer priority logic**: `ResolveSurfaceDetailTextureIndex` at
  [ModelImporter.cs lines 626-639](../../../XRENGINE/Core/ModelImporter.cs#L626-L639)
  correctly checks `TextureType.Normals` / `TextureType.NormalCamera` before
  falling back to `TextureType.Height`.

### Fix Steps

- [ ] **1a. Restore HeightMapScale to 1.0**
  - File: [ModelImporter.cs line 565](../../../XRENGINE/Core/ModelImporter.cs#L565)
  - Change: `private const float ImportedHeightMapScale = 2.0f;` → `1.0f`
  - This matches the shader default and the previously validated value.

- [ ] **1b. Expand filename heuristic for normal maps**
  - File: [ModelImporter.cs line 587](../../../XRENGINE/Core/ModelImporter.cs#L587)
  - Expand the `"normal"` match to also catch `"norm"`, `"nrm"`, and
    common suffix patterns like `_nm`, `_n` (with word-boundary awareness to
    avoid false positives on words like "ornament").
  - The bump/height match on line 588 (`"bump"` / `"height"`) should also be
    extended if there are additional patterns, but the normal-map detection is
    highest priority since false negatives there cause the worst visual artifacts.

- [ ] **1c. Validate with Sponza and other test scenes**
  - Re-import a model with known bump maps (Sponza lion) and verify normals look
    correct in the deferred debug view (`DeferredDebugView = Normals`).

---

## Issue 2: Volumetric Fog Appears As Noise

**Confidence: High** — all claims verified in shader code and settings.

### Root Cause

The volumetric fog is a single-pass screen-space raymarcher in
[PostProcess.fs lines 369-440](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs#L369-L440)
with no temporal reprojection, denoising, or bilateral blur. The jitter seed at
line 400 is `rand(sourceUv + rawDepth)` — purely spatial with no frame counter.
The noise pattern is **static per pixel**, so even TAA cannot average it out
across frames.

Combined with a runtime default `JitterStrength = 1.0` (from the pipeline
schema), the noise is maximally visible.

### Secondary Issue: Default Mismatches

The C# field defaults in [VolumetricFogSettings.cs lines 22-25](../../../XRENGINE/Rendering/Camera/VolumetricFogSettings.cs#L22-L25)
and the pipeline schema defaults in [DefaultRenderPipeline2.PostProcessing.cs lines 1375-1400](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs#L1375-L1400)
disagree on three parameters:

| Parameter | C# Field Default | Pipeline Schema Default |
|-----------|------------------|-------------------------|
| `MaxDistance` | 120.0 | 150.0 |
| `StepSize` | 2.0 | 4.0 |
| `JitterStrength` | 0.25 | 1.0 |

The pipeline schema wins at runtime, so the effective default uses maximum
jitter and coarser stepping than the field initializer suggests. This also
means the unit test `Defaults_MatchPipelineSchemaDefaults` (which asserts the
schema values) fails against the actual field initializers.

### Fix Steps

- [ ] **2a. Add temporal variation to jitter seed**
  - File: [PostProcess.fs ~line 400](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs#L400)
  - Change the jitter seed from `rand(sourceUv + rawDepth)` to
    `rand(sourceUv + rawDepth + fract(RenderTime * 7.0))` (or similar).
  - This lets TAA/temporal accumulation average out the noise across frames,
    dramatically reducing visible noise without a dedicated denoiser pass.

- [ ] **2b. Align C# field defaults with pipeline schema defaults**
  - File: [VolumetricFogSettings.cs lines 23-25](../../../XRENGINE/Rendering/Camera/VolumetricFogSettings.cs#L23-L25)
  - Change field initializers to match the pipeline schema so behavior is
    identical regardless of which initialization path wins:
    - `_maxDistance = 150.0f;`
    - `_stepSize = 4.0f;`
    - `_jitterStrength = 1.0f;`
  - Alternatively, if the C# field defaults are preferred, update the pipeline
    schema values. The key requirement is **consistency** — pick one source of
    truth and align the other.

- [ ] **2c. Reduce default JitterStrength for better out-of-box quality**
  - After step 2a (temporal jitter), re-evaluate whether 1.0 or a lower value
    (0.5-0.75) provides the best quality/noise tradeoff. With temporal variation,
    higher jitter is more tolerable since it averages out over frames.
  - If temporal jitter is NOT implemented (skipped for scope), reduce the default
    to 0.25-0.5 to limit visible noise.

- [ ] **2d. Validate fog appearance**
  - Place a directional light and a volumetric fog volume in the unit testing
    world and confirm the fog looks like smooth 3D raymarched fog rather than
    per-pixel noise.

---

## Issue 3: MSAA Results In Black Screen (Regression)

**Confidence: Medium** — the full command chain is structurally correct in static
analysis. The regression is likely in resource lifecycle, shader compilation,
or a specific code change. Runtime debugging is required.

### What Was Verified (Structurally Correct)

The full MSAA data flow was traced and found to be **correct in steady state**:

1. `VPRC_BindFBOByName` binds `ForwardPassMsaaFBO` with color+depth+stencil clear
   — [DefaultRenderPipeline.cs line 1325](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1325)
2. Depth preload blits MSAA depth from `MsaaGBufferFBO` → `ForwardPassMsaaFBO`
   — [line 1347](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1347)
3. `MsaaLightCombineFBO` (quad shader, no render targets) renders to the currently
   bound `ForwardPassMsaaFBO`
   — [line 1386](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1386)
4. Forward meshes render to `ForwardPassMsaaFBO`
5. `VPRC_ResolveMsaaGBuffer` blits color from `ForwardPassMsaaFBO` → `ForwardPassFBO`
   (which targets `HDRSceneTexture`)
   — [line 1426](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1426)
6. MSAA mode makes `upscaleChoice` false → PostProcess is NOT rendered to
   `PostProcessOutputFBO` — it goes directly through `CreateFinalBlitCommands`
7. `VPRC_VendorUpscale` receives `PostProcessFBOName`; since PostProcessFBO is a
   quad shader with no color attachment, it enters the QuadShader path
   — [VPRC_VendorUpscale.cs line 126](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs#L126)
8. `base.Execute()` renders PostProcess.fs directly to backbuffer

### What Was Debunked

- **0×0 FBO claim**: `InternalWidth` / `InternalHeight` always go through
  `Math.Max(1, ...)` at [XRRenderPipeline.cs lines 250-257](../../../XRENGINE/Rendering/Pipelines/XRRenderPipeline.cs#L250-L257).
  Zero-sized FBOs cannot exist.
- **HDR format churn claim**: `ResolveOutputHDR()` uses per-frame latching via
  `EffectiveOutputHDRThisFrame` at [XRRenderPipelineInstance.cs line 322](../../../XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs#L322).
  Within a single frame, all resources see the same HDR state.

### Likely Regression Candidates

1. **`#define DEFERRED_MSAA` shader variant compilation failure**: If the MSAA
   variant of DeferredLightCombine fails to compile at runtime, the
   `MsaaLightCombineFBO` quad renders nothing, leaving `ForwardPassMsaaFBO`
   with only cleared color (black). The resolve then blits black into
   `HDRSceneTexture`, and PostProcess reads all-zero.

2. **Null texture references after AA invalidation**: When AA settings change,
   `InvalidateAntiAliasingResources` at [line 878](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L878) destroys
   all AA-related FBOs and textures. `NeedsRecreateMsaaLightCombineFbo` at
   [line 314](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L314) verifies all 7 texture references via `ReferenceEquals`.
   If a dependent texture (e.g., `MsaaLightingTexture`) has not been recreated
   at the time the check runs, the FBO is recreated with null material textures.

3. **MSAA render buffer format mismatch**: `ForwardPassMsaaFBO` uses an
   `XRRenderBuffer` whose format comes from `ResolveOutputRenderBufferStorage()`
   (Rgba16f or Rgba8). If this changes between create and resolve time (unlikely
   due to latching, but possible across invalidation cycles), `glBlitFramebuffer`
   may silently fail.

4. **A non-obvious code change** in a helper method, FBO creation, shader, or
   binding order that broke the chain. Git bisect between the known-good and
   current state would isolate this efficiently.

### Fix Steps

- [ ] **3a. Instrument the MSAA chain for runtime diagnosis**
  - Run the editor with env vars `XRE_DIAG_VENDOR_UPSCALE=1` and
    `XRE_DIAG_QUAD_BLIT=1` set, then switch AA to MSAA.
  - Observe the diagnostic log output to identify where in the chain the data
    goes black.
  - Also uncomment the commented-out `Debug.RenderingEvery` blocks in:
    - [VPRC_VendorUpscale.cs lines 129-136](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs#L129-L136) (QuadShader path)
    - [VPRC_VendorUpscale.cs lines 147-155](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs#L147-L155) (ResolvedColor path)

- [ ] **3b. Verify MSAA shader variant compilation**
  - Check if `ShaderHelper.CreateDefinedShaderVariant(baseShader, MsaaDeferredDefine)`
    at [DefaultRenderPipeline.FBOs.cs line 1083](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs#L1083)
    is returning a valid shader. Add a temporary log if it returns the fallback
    `baseShader`.
  - Review the DeferredLightCombine shader for any `#ifdef DEFERRED_MSAA` code
    paths that might have syntax errors or reference missing uniforms/samplers.

- [ ] **3c. Verify MSAA resolve output data**
  - Use `XRE_OUTPUT_SOURCE_FBO=ForwardPassFBO` env var to present ForwardPassFBO
    directly to screen, bypassing PostProcess.
  - If the output is black with MSAA on but correct with MSAA off, the issue is
    in the MSAA resolve blit from `ForwardPassMsaaFBO` → `ForwardPassFBO`.
  - If the output is correct, the issue is downstream in PostProcess or the
    VendorUpscale present path.

- [ ] **3d. Check resource creation ordering**
  - In the command chain, `MsaaLightCombineFBO` is created at
    [line 1221](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1221) AFTER the MSAA GBuffer and lighting
    textures are created (lines 1066-1075). Verify that after an invalidation,
    the CacheOrCreate commands recreate textures before FBOs that reference them.

- [ ] **3e. Git bisect for the regression**
  - If steps 3a-3d don't isolate the bug, bisect between the last known-good
    commit for MSAA and current HEAD. The MSAA chain is structurally correct,
    so the regression is a specific code delta.

- [ ] **3f. Apply fix once root cause is identified**
  - The fix will depend on which candidate (or new finding) is confirmed.

---

## Issue 4: TSR Results In Black Screen (Regression)

**Confidence: Medium** — same as MSAA; structurally correct, needs runtime
debugging to isolate the regression.

### What Was Verified (Structurally Correct)

The full TSR data flow was traced:

1. PostProcess quad renders at internal resolution to `PostProcessOutputFBO`
   (which targets `PostProcessOutputTexture`)
   — [DefaultRenderPipeline.cs line 1652](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1652)
2. `TsrUpscaleFBO` (TemporalSuperResolution.fs) reads `PostProcessOutputTexture`,
   velocity, depth, history; writes to `FxaaOutputTexture` via explicit render
   targets
   — [DefaultRenderPipeline.cs line 1661](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1661)
3. Blit `FxaaOutputTexture` → `TsrHistoryColorFBO` for next-frame history
   — [line 1662](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1662)
4. `VPRC_VendorUpscale` receives `TsrUpscaleFBOName`; TsrUpscaleFBO HAS a color
   texture attachment (`FxaaOutputTexture`), so VendorUpscale enters the
   FallbackBlit path (passthrough shader) presenting that texture to backbuffer.

The TSR shader properly handles uninitialized history:

```glsl
bool canUseHistory = HistoryReady && IsValidUV(historyUV);
...
if (!canUseHistory) historyWeight = 0.0f;  // falls back to current frame
```

### Likely Regression Candidates

1. **`PostProcessOutputTexture` not populated**: If POST ProcessFBO → `PostProcessOutputFBO`
   rendering fails (e.g., PostProcessOutputFBO was invalidated and not recreated
   before TSR reads it), the TSR shader reads all-zero as input.

2. **Internal resolution viewport mismatch**: PostProcess renders at internal
   resolution (line 1651 pushes `UseInternalResolution = true`), TSR renders at
   full resolution (line 1657 pushes `UseInternalResolution = false`). If the
   viewport render area push/pop is unbalanced or the internal resolution is
   not correctly applied, texture coordinates could sample outside the valid
   region.

3. **FxaaOutputTexture format/size mismatch**: `TsrUpscaleFBO` targets
   `FxaaOutputTexture`, which is created at full resolution. If a size or format
   mismatch occurs between the target texture and the FBO's expectations, the
   output could be undefined.

4. **HistoryReady incorrectly stuck `false`**: If  the temporal accumulation
   commit pass never marks history as ready (e.g., a code change broke the
   commit phase), the shader always outputs current-frame content at internal
   resolution stretched to full resolution, which could appear as a heavily
   aliased or incorrect image.

### Fix Steps

- [ ] **4a. Instrument the TSR chain**
  - Set `XRE_DIAG_QUAD_BLIT=1` and `XRE_DIAG_VENDOR_UPSCALE=1`, switch AA to TSR.
  - Observe which quad blit steps succeed and which produce unexpected output.

- [ ] **4b. Test PostProcessOutputFBO bypass**
  - Use `XRE_OUTPUT_SOURCE_FBO=PostProcessOutputFBO` to present the PostProcess
    output directly, bypassing TSR and VendorUpscale.
  - If this is black: the issue is in PostProcess → PostProcessOutputFBO rendering.
  - If this is correct: the issue is in the TSR shader or the final present path.

- [ ] **4c. Test TsrUpscaleFBO bypass**
  - Use `XRE_OUTPUT_SOURCE_FBO=TsrUpscaleFBO` to present TSR output directly.
  - If this is black but 4b was correct: the TSR shader itself is the problem.
  - If this is correct: the VendorUpscale fallback blit is the problem.

- [ ] **4d. Log HistoryReady state**
  - Uncomment the diagnostic log in [DefaultRenderPipeline.PostProcessing.cs
    ~line 1589](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs#L1589)
    to confirm `HistoryReady` transitions from false → true.

- [ ] **4e. Check temporal accumulation commit phase**
  - Verify [VPRC_TemporalAccumulationPass](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs)
    `Phase.Commit` at [line 1697](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs#L1697)
    is executing and correctly updating temporal state.

- [ ] **4f. Git bisect for the regression**
  - Same approach as MSAA: if instrumentation doesn't isolate the cause, bisect
    between the last known-good TSR commit and current HEAD.

- [ ] **4g. Apply fix once root cause is identified**

---

## Suggested Fix Order

| Priority | Issue | Effort | Reason |
|----------|-------|--------|--------|
| 1 | TexturedNormalDeferred (1a) | Trivial | One constant change, high visual impact |
| 2 | TexturedNormalDeferred (1b) | Small | Filename heuristic expansion |
| 3 | Volumetric Fog jitter (2a) | Small | One shader line change |
| 4 | Volumetric Fog defaults (2b) | Trivial | Align three field initializers |
| 5 | MSAA instrumentation (3a-3d) | Medium | Diagnostic first, then targeted fix |
| 6 | TSR instrumentation (4a-4e) | Medium | Diagnostic first, then targeted fix |
| 7 | MSAA/TSR git bisect (3e, 4f) | Variable | Only if instrumentation doesn't isolate |

---

## Diagnostic Environment Variables Reference

| Variable | Purpose |
|----------|---------|
| `XRE_DIAG_VENDOR_UPSCALE=1` | Logs VendorUpscale path selection and source/target info |
| `XRE_DIAG_QUAD_BLIT=1` | Logs every `VPRC_RenderQuadToFBO` source/dest with target details |
| `XRE_OUTPUT_SOURCE_FBO=<name>` | Overrides final present to use the named FBO, bypassing AA chain |
| `XRE_BYPASS_VENDOR_UPSCALE=1` | Uses `VPRC_RenderToWindow` instead of VendorUpscale for final present |

---

## Expected Files To Change

| File | Issues |
|------|--------|
| [ModelImporter.cs](../../../XRENGINE/Core/ModelImporter.cs) | 1a, 1b |
| [PostProcess.fs](../../../Build/CommonAssets/Shaders/Scene3D/PostProcess.fs) | 2a |
| [VolumetricFogSettings.cs](../../../XRENGINE/Rendering/Camera/VolumetricFogSettings.cs) | 2b |
| [DefaultRenderPipeline.PostProcessing.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs) | Possibly 2b (if schema defaults change) |
| [DefaultRenderPipeline2.PostProcessing.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline2.PostProcessing.cs) | Possibly 2b (if schema defaults change) |
| [VPRC_VendorUpscale.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_VendorUpscale.cs) | 3a (uncomment diagnostics) |
| [VPRC_TemporalAccumulationPass.cs](../../../XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs) | 4e |
| [DefaultRenderPipeline.FBOs.cs](../../../XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs) | 3b (verify shader variant) |
| TBD files depending on MSAA/TSR root cause | 3f, 4g |
