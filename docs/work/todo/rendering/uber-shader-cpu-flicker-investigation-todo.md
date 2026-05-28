# Uber Shader CPU Flicker Investigation - TODO

Tracks the current suspicion trail for the CPU direct draw path where Sponza
Uber materials randomly flicker out, stay unrendered, or show the pending
fallback even after programs appear to have linked.

Observed setup:

- `Assets/UnitTestingWorldSettings.jsonc`
  - `GPURenderDispatch=false`
  - `AllowShaderPipelines=false`
  - `OpenGLProgramCompileLinkWorkerCount=4`
  - Sponza imported with `MaterialMode=Uber`
- Occlusion culling off does not fix it.
- Flicker can happen while the camera is still, so the primary trigger is likely
  visible draw volume / program state churn rather than camera angle.
- The issue appeared after recent Uber/linking work, especially combined-program
  sharing, pending fallback, and async compile/link changes.

## Strongest suspicion - combined program reported ready before usable

Status: first fix implemented 2026-05-27.

Latest log evidence from `xrengine_2026-05-27_10-57-36_pid16100`:

- Sponza-like combined programs (`Combined:arch`, `Combined:column_c`,
  `Combined:chain`, `Combined:Material__57`) timed out in the shared-context
  source-link lane.
- After timeout, those hashes were marked failed and then skipped repeatedly via
  `SOURCE_FAILED_SKIPPED`, which can leave individual Sponza materials on
  fallback/not-rendered paths while neighboring meshes continue drawing.
- Follow-up fix: non-hazard graphics programs now retry the existing
  driver-parallel source lane after a shared-context source-link timeout instead
  of immediately becoming failed hashes.

`GLMeshRenderer.GetCombinedProgram(...)` currently does:

1. `EnsureCombinedProgramForMaterial(material)`
2. `vertexProgram.Link(nonBlocking: true)`
3. `Api.BindProgramPipeline(0)`
4. `vertexProgram.Use()`
5. returns `true`

The return value from `vertexProgram.Use()` is ignored.

`GLRenderProgram.Use()` can return `false` when the logical program has a
pending async/shared build, has not adopted its linked handle yet, or otherwise
is not safe to bind. If `GetCombinedProgram` returns `true` anyway, the draw
continues with either:

- no valid current combined program,
- a stale program from a previous draw,
- or a program-pipeline/combined-program state mismatch.

That would explain:

- random-looking flicker that depends on draw order / number of visible meshes,
- meshes disappearing while selection outlines still render,
- fallback meshes appearing inconsistently,
- and the problem surviving occlusion-culling changes.

### First fix to try

- [x] In `GLMeshRenderer.GetCombinedProgram(...)`, require `vertexProgram.Use()`
  to succeed before returning `true`.
- [x] If `Use()` fails, set both out parameters to `null`, log a throttled
  "combined program not ready for use" diagnostic, and return `false`.
- [ ] Confirm the pending Uber fallback path handles this as "material still
  loading" instead of drawing with stale GL state.
- [x] Add a source/contract test in
  `XREngine.UnitTests/Rendering/GLMeshRendererLifecycleContractTests.cs` so the
  combined path cannot ignore `Use()` again.

Validation target:

- [ ] Sponza CPU direct draw path remains stable with a still camera after all
  visible Uber programs finish linking.
- [ ] No black/transparent flicker when rotating or when many Sponza meshes are
  visible.
- [ ] Logs show pending programs taking fallback until usable, then switching to
  the real combined program.

## Second suspicion - pooled combined program lifetime/reuse

Recent work introduced combined program pooling:

- `OpenGLRenderer.ProgramPool.cs`
- `XRRenderProgramDescriptor.cs`
- `GLMeshRenderer.CreateCombinedProgram(...)`

The descriptor intentionally groups by generated shader/program identity rather
than by material instance. That should be valid if all per-material state is
uploaded every draw, but it makes any cached-per-program material state more
dangerous.

### Checks

- [ ] Verify `GLMaterial.SetUniforms(...)` always forces uniform updates when
  the material source changes on a shared combined program.
- [ ] Verify texture bindings are refreshed per draw, not only per program.
- [ ] Verify pooled program release cannot destroy a shared program while another
  `GLMeshRenderer` still has a lease.
- [ ] Add temporary diagnostics for combined program descriptor, pool ref count,
  logical program id, GL handle id, and material name when a renderer switches
  from fallback to real program.

Expected result if this is the culprit:

- Multiple materials sharing one combined program will intermittently render
  with another material's stale uniforms/textures, or render blank after one
  renderer releases the shared program.

## Third suspicion - mutable render command state shared across views

The CPU path reuses `RenderCommandMesh3D` objects and mutates fields such as
`RenderDistance`, `SortOrderKey`, render pass, mesh, and culling matrices while
collecting. The same command object can be collected for multiple cameras or
passes in one frame.

This is older than the most recent shader work, so it is less likely to be the
new regression by itself. It can still amplify the symptom if more programs now
stay pending/fallback and more draws are skipped or sorted differently.

### Checks

- [x] Log per-camera CPU collection counts with command id, mesh id, material
  name, pass name, and accepted/rejected reason for a still-camera frame range.
- [ ] Verify commands inside sorted CPU pass sets are not mutated after insertion
  in a way that changes their comparer result.
- [ ] Verify multi-camera shadow/capture passes cannot overwrite a command
  snapshot needed by the main camera draw.

Added 2026-05-27:

- `[ModelRenderDiag] CommandCollect` now includes command identity, stable query
  key, source submesh, command render distance, and sort key.
- `[ModelDrawDiag.GL]` now matches Sponza source submesh names and logs the
  source submesh at GL draw phases.
- `[SponzaFlickerDiag.CPU]` logs capped CPU collect/draw/skip breadcrumbs for
  Sponza render commands, including frame, pass, command id, stable key, sort
  key, distance, camera, source submesh, material, and skip reason.

Expected result if this is the culprit:

- CPU collection counts or sorted pass membership will change for a still camera
  even when program readiness is stable.

## Fourth suspicion - culling basis / render transform mismatch

Some culling paths use world matrices while rendering uses render-thread
matrices. This can cause angle-dependent disappearances, especially after tighter
submesh bounds. The latest observation says the issue happens with a still
camera and scales more with mesh count, so this is not the leading suspicion.

### Checks

- [ ] Compare the accepted CPU culling matrix, render matrix, and final draw
  transform for flickering Sponza meshes.
- [ ] Keep this separate from the shader/program-state fix so the regression is
  not hidden by a broader culling change.

## Useful log probes

Add temporary, throttled diagnostics around:

- `GLMeshRenderer.GetCombinedProgram(...)`
  - material name
  - combined program name/hash
  - `Link(nonBlocking:true)` result
  - `Use()` result
  - `IsLinked`
  - `IsAsyncBuildPending`
  - `BindingId`
- fallback draw decision in `GLMeshRenderer.Rendering.cs`
- combined program pool acquire/release ref counts
- CPU direct draw skipped reason per material when `GetPrograms(...)` returns
  false

## Narrow validation commands

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "GLMeshRendererLifecycleContractTests|ShaderProgramDescriptorTests" --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Manual validation:

- Run the Unit Testing World with CPU direct draw and shader pipelines disabled.
- Keep the camera still after Sponza loads and watch for material flicker after
  the shader program link panel reports no pending combined Uber programs.
- Repeat once with `OpenGLProgramCompileLinkWorkerCount=1` and once with `4`.
  The bug should not change correctness with worker count; only the time spent
  in fallback should change.
