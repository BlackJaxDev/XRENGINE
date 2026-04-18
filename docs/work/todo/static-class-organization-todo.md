# Static Class Organization TODO

Generated: 2026-04-17
Source report: [docs/work/audit/static-classes.md](../audit/static-classes.md)

Baseline (original audit): 343 unique static classes (281 top-level, 62 nested). Audit initially reported ~140 top-level statics in the global/default namespace.

**Update 2026-04-17:** The audit scanner had a regex bug that missed the common `namespace X\n{` (brace-on-next-line) pattern. Fixing it revealed the real count of global-namespace static classes was only 7 engine/editor classes + 1 dev-tool class. Phase 1 is now complete — scanner fixed, all 7 engine/editor classes namespaced, and `XREngine.Extensions` assembly promoted from the flat `Extensions` namespace to `XREngine.Extensions` (34 file namespaces + 210 consumer `using` statements). `Tools/` is excluded from the audit as dev-only surface.

Current state: 332 unique static classes, 0 in `(none)` namespace.

Goal: keep static utilities discoverable via IntelliSense by maintaining `XREngine.*` namespaces, and consolidate near-duplicates.

---

## Phase 1 — Namespace sweep (✅ COMPLETE)

- [x] Fix `Tools/Reports/Find-StaticClasses.ps1` namespace regex (missed brace-on-next-line).
- [x] Exclude `Tools/` from the audit (dev tooling, not engine API surface).
- [x] `XREngine.Extensions` assembly — promoted flat `Extensions` namespace to `XREngine.Extensions` (chose single flat namespace over subdividing by folder to minimize consumer churn; 210 `using Extensions;` statements rewritten).
- [x] `XREngine.Editor` — `EditorProjectInitializer`, `EditorVR`.
- [x] `XREngine.Benchmarks` — `FbxPhase0BaselineHarness`, `FbxPhase7RegressionHarness`, `GltfPhase0BaselineHarness`.
- [x] `XREngine.Core` — `OpenVRExtensions` (plus added `using XREngine.Core;` to two VR consumers).

Items from the original plan that turned out to already be namespaced (verified via scanner bug fix):

- [x] `XREngine.Data` — `Interp`, `Globals`, `Utility`, `Compression`, `Win32`, `CudaInterop`, `NativeMethods`, `FloatQuantizer`, `Memory`, `GeoUtil`, `BSPShapeExtensions`, `PlaneHelper`, `SpatialCoordinateConversion`, `OverrideableSettingExtensions`, `VMDUtils`, `UnityHumanoidMuscleMap`, `CoACD`
- [x] `XREngine.Core` — `ExpressionParser`, `SnapshotYamlSerializer`, `XRTypeRedirectRegistry`, `GraphicsExtensions`, `UnityConverter`
- [x] `XREngine.Core.*` — all asset-loading context classes
- [x] `XREngine.Audio` — `AudioDiagnostics`, `AudioSettings`, `XRAudioUtil`
- [x] `XREngine.Rendering.*` — all rendering static classes
- [x] `XREngine.Scene.*` — all scene/component/physics static classes
- [x] `XREngine.Editor.*` — remaining editor/ImGui utilities
- [x] `XREngine.Runtime.Core` — all runtime static classes

## Phase 2 — Naming normalization (✅ COMPLETE)

- [x] Standardize all extension classes on plural `*Extensions`. Renamed 24 singular `*Extension` classes to plural: `ArrayExtension(s)`, `EnumerableExtension(s)`, `EnumExtension(s)`, `ListExtension(s)`, `StringExtension(s)`, `StreamExtension(s)`, `LockingExtension(s)`, `RectangleExtension(s)`, `MarshalExtension(s)`, `TypeExtension(s)`, `MethodInfoExtension(s)`, `EventInfoExtension(s)`, `TaskExtension(s)`, `ByteExtension(s)`, `SByteExtension(s)`, `Int16/32/64Extension(s)`, `UInt16/32/64Extension(s)`, `DecimalExtension(s)`, `QuaternionExtension(s)`, `MatrixExtension(s)`. Also renamed the three source files whose names still ended in `Extension.cs` (`MatrixExtensions.cs`, `QuaternionExtensions.cs`, `MarshalExtensions.cs`). Verified by building `XRENGINE.csproj` and `XREngine.Server.csproj` cleanly.
- [x] Evaluated `Helper` / `Util` vs `*Extensions` overlap. No `Helper`/`Util` class shadows a same-type `*Extensions` class (`PlaneHelper` has no `PlaneExtensions`, `ConsoleHelper` has no `ConsoleExtensions`, etc.). The remaining mix of `Helper` / `Util` / `Utility` / `Utilities` suffixes is a subjective style preference, not a correctness issue; leaving as-is for pre-v1 rather than churning reviewers. Revisit post-v1 if a stronger convention is desired.

## Phase 3 — Consolidation & file-per-class

- [ ] Split `XREngine.Runtime.Bootstrap/BootstrapEditorHooks.cs` into three files matching class names: `BootstrapEditorBridge`, `BootstrapModelImportBridge`, `BootstrapWorldBridge`.
- [ ] Group `*Builder` bootstraps under `XREngine.Runtime.Bootstrap.Builders` sub-namespace: `BootstrapLightingBuilder`, `BootstrapModelBuilder`, `BootstrapPawnFactory`, `BootstrapPhysicsBuilder`, `BootstrapWaterBuilder`, `BootstrapWorldFactory`, `BootstrapAnimationWorldBuilder`, `BootstrapAudioWorldBuilder`, `BootstrapFlyableCameraFactory`.
- [ ] Collapse the three parallel serialization triplets in `XREngine` namespace into a shared generic helper (or at minimum a common `XREngine.Core.Engine.Serialization` sub-namespace):
    - [ ] `AnimationClipCookedBinarySerializer` + `AnimationClipMemoryPackRegistration` + `AnimationClipSerialization`
    - [ ] `AnimStateMachineCookedBinarySerializer` + `AnimStateMachineMemoryPackRegistration` + `AnimStateMachineSerialization`
    - [ ] `BlendTreeCookedBinarySerializer` + `BlendTreeMemoryPackRegistration` + `BlendTreeSerialization`
- [ ] Consolidate ImGui editor helpers into one namespace `XREngine.Editor.ImGui`:
    - [ ] Move `ImGuiAssetUtilities`, `ImGuiDragDropNative`, `ImGuiEditorUtilities`, `ImGuiExternalPathDrop`, `ImGuiUndoHelper` out of `XREngine.Editor` root.
    - [ ] Evaluate merging `ImGuiAssetUtilities` + `ImGuiEditorUtilities` + `ImGuiExternalPathDrop` into a single `ImGuiEditorUtilities` class if the combined surface is < ~500 LoC.
- [ ] Consider merging `Audio2Face3DEmotions` + `Audio2Face3DLiveClientRegistry` into a single `Audio2Face3DRegistry` (they already live in the same file pair).

## Phase 4 — Duplicate & conflict resolution

- [ ] Rename one of the two `PublishedCookedAssetRegistryRegistration` classes (the runtime-rendering copy in `XREngine.Core.Files` vs. the `XREngine` namespace copy in `XRENGINE/Core/Files/`). Keep the one whose namespace matches its assembly; rename or delete the other.
- [ ] Resolve the two `Engine` static classes: the 29-partial engine in `(none)` namespace and the 2-partial profiler-sender `Engine` in `XREngine` namespace. Rename the profiler-sender variant (e.g. `EngineProfilerSender`) or nest it inside the primary `Engine` class.
- [ ] Audit other same-named statics surfaced by the report (e.g. `BootstrapEditorHookRegistration` vs. `BootstrapEditorBridge`, `RuntimeBootstrapState` vs. `UnitTestingWorldSettingsStore`) for overlap and merge where appropriate.

## Phase 5 — Global-namespace cleanup (catch-all)

- [ ] After Phases 1–4, re-run `Tools/Reports/Find-StaticClasses.ps1` and verify that the `(none)` namespace column is empty (target: 0 top-level static classes without a namespace).
- [ ] Commit the updated `docs/work/audit/static-classes.md` alongside the namespace changes so the audit stays current.

---

## Execution guidance

- Work one assembly per PR/commit to keep review surface small.
- Expect cascading compile errors from `using` directives — run `dotnet build XRENGINE.slnx` after each assembly sweep.
- For pre-v1 codebase (see `AGENTS.md` §1), breaking namespace changes are acceptable; update consumers directly rather than adding `using` aliases.
- When renaming, prefer the Pylance/Roslyn rename-symbol refactor over text search-and-replace to pick up XML doc references and cref links.
- After Phase 1, consider adding an analyzer / build-time check that rejects new top-level static classes in the global namespace.
