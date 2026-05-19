# Occlusion Regression & Sponza-Roof Investigation

## Symptoms Reported

1. **Sponza roof (and a handful of other large statics) missing in `CpuDirect` mesh-submission mode.**
2. **No occlusion culling visible in any mode** — `GpuHiZ`, `CpuQueryAsync`, `CpuSoftwareOcclusion` all leave the scene fully drawn from every angle, including in debug visualization.
3. Editor occlusion panel reports CPU-async skip reasons `modeOff` and `noCamera` even when both look false from the user's perspective.

## Findings

### Sponza roof is not a culling regression

- `RenderableMesh.cs`, `RenderInfo3D.cs`, `VisualScene3D.cs` are byte-for-byte unchanged from the last-known-good baseline `4cbf736d` (`git diff` returns 0 bytes for those files between baseline and HEAD).
- Root cause is the uber-shader Sponza variant link timeout already tracked in
  [uber-shader-sponza-link-failures-todo.md](uber-shader-sponza-link-failures-todo.md). When a variant hash hits the 30s shared-context link timeout it is marked failed, and downstream meshes referencing that hash either fall through to prepass/depth-only output or render as untextured fallback — visually identical to "missing geometry."
- Mesh culling, octree placement, and `CullingOffsetMatrix` paths are not implicated.

### "Occlusion CPU skip reasons" are correct, not a bug

- `RenderCommandCollection.RenderCPU` records `modeOff` whenever `RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode != CpuQueryAsync`. With the user's GPU mode set, every CPU pass legitimately increments `modeOff`.
- `noCamera` legitimately fires for light-probe / scene-capture / cubemap-face passes that pass `camera = null` into `RenderCPU`.
- No fix needed; the panel's existing labeling can be misread but the counts are correct.

### "No occlusion in any mode" is the real regression

Confirmed by user across the test matrix:

| Mesh Submission | Occlusion Mode | Expected | Observed |
| --- | --- | --- | --- |
| CpuDirect | CpuQueryAsync | culls | no culling |
| GpuIndirectInstrumented | GpuHiZ | culls | no culling |
| GpuIndirectInstrumented | CpuSoftwareOcclusion | culls | no culling |
| GpuIndirectZeroReadback | GpuHiZ | culls | no culling |
| CpuDirect | GpuHiZ / CpuSOC | no-op by design | no-op (correct) |

The cross-mode failure pattern strongly implies a single upstream gate short-circuits every mode.

## Diff Surface (`4cbf736d..HEAD`, 4 commits)

Relevant changed files:

- `GPURenderPassCollection.Core.cs` — `_passEnableZeroReadbackMaterialScatter = zeroReadback || instrumented`; `_passDisableCpuReadbackCount = !instrumented` (removed `IndirectDebug.DisableCpuReadbackCount` override).
- `GPURenderPassCollection.Occlusion.cs` — added `ApplyCpuSoftwareOcclusionToGpuCulledCommands` to actually rewrite the GPU culled command buffer in CpuSOC mode; `RecordActiveMode` switched from `ResolveMeshSubmissionStrategy()` to the `MeshSubmissionStrategy` property.
- `RenderCommandCollection.cs` — `RecordActiveMode` strategy field hardcoded to `EMeshSubmissionStrategy.CpuDirect` in the CPU path (cosmetic but misleading); added `PrepareCpuSoftwareOcclusion` call when `meshSubmissionStrategy == GpuIndirectInstrumented`.
- `HybridRenderingManager.cs` — 160-line refactor (new `MaterialBindingLayouts`, `MaterialBindingResolverResult`, `MaterialBindingGlslGenerator`).
- `GpuBvhTree.cs` — 170-line diff.
- Uber shader pipeline — new `MaterialBindingLayout.cs`, `UberShaderVariantBuilder.Preprocessor.cs`.

Suspicious commits:

- `e63cd398` "Improve game loop, uber shader fixes and todo doc" — adds the Sponza uber timeout doc; partial fix only.
- `e1a68971` "Working on timing, model caches, meshlets" — model-cache changes; not the Sponza-roof cause (mesh files unchanged) but a candidate for the broader culling-system regression.

## Candidate Root Causes for the Occlusion Regression

In rough order of likelihood:

1. **`Exit.ShadowOrDepthPass` early-exit firing for the main forward pass.**
   `ApplyOcclusionCulling` bails when `RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState?.UseDepthNormalMaterialVariants == true`. If the default pipeline now sets this flag during normal opaque submission, every occlusion mode short-circuits identically — matching the symptom.
2. **`GpuHiZ.Exit.NoDepthTexture` / `DepthUnsupportedView`** — `DefaultRenderPipeline.DepthViewTextureName` not being exposed or registered before the occlusion phase.
3. **`GpuHiZ.Exit.MissingShaders`** — HiZ init/gen/occlusion programs failing to link as collateral damage from the uber-shader-builder refactor (`UberShaderVariantBuilder.Preprocessor.cs`).
4. **Refine runs but pyramid is empty** — frame-ID handoff between depth-write and pyramid-build broken by the new material-scatter timing.
5. **`PrepareCpuSoftwareOcclusion` race in `GpuIndirectInstrumented`** — newly added call may run before occluders are submitted, leaving the SOC frame open but empty.

## Edits This Session

- Working tree retains only one change: `GPURenderPassCollection.Occlusion.cs`
  - `SubmitCpuOcclusionQueryBatch` made an explicit documented no-op (it was already a scaffold).
  - First-time warning log directs the user away from `GpuIndirectInstrumented + CpuQueryAsync` (an unimplemented combination) toward `GpuHiZ` or `CpuDirect + CpuQueryAsync`.
- Earlier speculative edits (`PreCullCallback` hook on `RenderInfo3D`, mesh bounds refresh in `RenderableMesh`, `passNotTestable` telemetry counter, panel surfacing) were reverted after the user confirmed they produced no behavioral change.

## Next Actions

1. Capture occlusion panel readout from a live editor run on Sponza with `GpuIndirectInstrumented + GpuHiZ`:
   - Resolved active mode
   - `candidates / visible / occluded / culled` counters
   - Any `HiZStageStats` entries beginning with `Exit.*` or `GpuHiZ.Exit.*`
   - Whether `RecordGpuPassthroughDirty` fires
2. With that readout, narrow to one of the five candidates above and bisect across the 4 regression commits if needed.
3. Address the uber-shader Sponza variant link timeout per the existing todo.

## Files of Interest

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Occlusion.cs` — `ResolveActiveOcclusionMode`, `ApplyOcclusionCulling`, `ApplyGpuHiZOcclusion`, `ApplyCpuSoftwareOcclusionToGpuCulledCommands`.
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection.Core.cs` — pass-mode policy flags.
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommandCollection.cs` — `RenderCPU`, `PrepareCpuSoftwareOcclusion`, GPU dispatch entry.
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs` — material binding pipeline.
- `docs/work/todo/rendering/uber-shader-sponza-link-failures-todo.md` — companion Sponza issue.
