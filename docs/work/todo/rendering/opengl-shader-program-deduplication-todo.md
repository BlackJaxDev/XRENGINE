# OpenGL Shader Program Deduplication Todo

Last Updated: 2026-05-26
Status: Completed implementation pass.

User instruction for this pass: do not branch. The normal todo branch step was
intentionally skipped to honor that instruction.

## Original Evidence

Observed in the ImGui `Shader Program Links` panel while rendering the
unit-testing scene with Sponza content:

- Panel count: `Programs 8,888`, with `Linked 8,381`.
- The visible `MaterialPipeline:leaf` rows repeated heavily.
- Logs under
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-26_10-10-28_pid4224/`
  showed `8,381` shader-backend ready records but only `133` unique source
  hashes.
- `MaterialPipeline:leaf` produced `1,342` ready rows, `2` unique source
  hashes, `2` `BinaryUploadAsync` backend records, and `1,340`
  `BinaryProgramShared` backend records.

Interpretation: backend GL-handle sharing was already working, but the engine
still created many logical `XRRenderProgram` wrappers and the panel counted
wrappers rather than grouped program identity.

## Completed Work

- [x] Skipped branch creation by explicit user request.
- [x] Added `XRRenderProgramDescriptor`, a stable descriptor used before
  logical program construction.
- [x] Added descriptor metadata to `XRRenderProgram`.
- [x] Kept shader variant metadata separate from program display names.
- [x] Stopped `XRMaterial.ApplyShaderProgramMetadata()` from overwriting
  combined program names.
- [x] Added diagnostic metadata for material name, mesh renderer name, version
  kind, topology kind, and pool key.
- [x] Extended `GLRenderProgram.LinkDiagnosticsSnapshot` with effective source
  hash, binary cache key, descriptor key, shared linked program id/ref count,
  handle ownership, and handle source.
- [x] Exposed safe read-only shared linked program diagnostics.
- [x] Added logical create/destroy, shared attach/detach, peak shared refs,
  combined-program pool, GPU-driven pool, and pending destruction diagnostics.
- [x] Added descriptor and handle source fields to `[ShaderBackend]` and
  `[ShaderLink]` records.
- [x] Added compact `[ShaderProgramDedupSummary]` output at shutdown and on
  demand from the Shader Program Links panel.
- [x] Added renderer-level OpenGL combined-program pooling keyed by descriptor.
- [x] Kept per-mesh VAO/buffer binding state outside pooled shader programs.
- [x] Preserved material-specific uniform updates when programs are shared.
- [x] Made pooled combined-program ownership explicit with renderer leases and
  reference counts.
- [x] Added GPU-driven material-program descriptor keys so identical indirect
  programs can be found before creating a new `XRRenderProgram`.
- [x] Preserved the previous linked GPU-driven program while a descriptor
  replacement is still pending.
- [x] Added pending destruction counts to `XRObjectBase` diagnostics.
- [x] Added grouped Shader Program Links mode.
- [x] Grouped by descriptor, binary cache key, prepared cache key, or source
  hash fallback.
- [x] Added grouped summary counters for logical programs, groups, unique
  hashes, binary keys, shared handle refs, and pending destruction backlog.
- [x] Added `Refs` sorting and a grouped table ref-count column.
- [x] Added representative program id/shared handle details.
- [x] Added selected-group detail with sample contributing logical refs.
- [x] Preserved the row-by-row view behind the `Grouped` toggle.
- [x] Made search include grouped keys, descriptor keys, binary keys, and child
  row fields.
- [x] Added `Copy Group Summary`.
- [x] Added `Log Group Summary`.
- [x] Added tests for descriptor equality, descriptor invalidation, and
  combined-program name preservation.
- [x] Updated `docs/features/opengl-program-linking.md` with the final pooling
  model and grouped-panel UX.

## Validation Performed

- [x] Built the editor:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Result: succeeded. The build still reports pre-existing NuGet vulnerability
warnings and older unrelated C# warnings from existing files.

- [x] Ran focused unit tests:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShaderProgramDescriptorTests --no-restore
```

Result: passed, `3/3`.

## Not Run In This Non-Interactive Pass

- Sponza windowed visual inspection.
- Cold-cache and warm-cache startup comparison in the live ImGui editor.
- Scene unload/reload leak inspection with the Shader Program Links panel open.
- Profiler capture for `XRWindow.ProcessPendingUploads`.

The implementation now exposes the needed grouped panel and log counters for
those runtime checks.

## Acceptance Status

- [x] The Shader Program Links panel can answer "how many unique programs do we
  really have?" without external log parsing.
- [x] Repeated rows collapse into grouped entries with logical ref counts.
- [x] Backend deduplication remains visible through binary-cache and
  shared-program diagnostics.
- [x] Material uniforms and samplers are forced to refresh when the active
  material source changes on a shared program.
- [x] GPU-driven indirect material programs now use prehashed descriptors.
- [x] Pooled and shared program lifecycle counters are visible in logs and UI.
- [x] No new build errors.
- [x] Dedicated-branch merge step intentionally skipped by user instruction.
