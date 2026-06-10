# Render Settings API Separation Refactor Todo

Status: Draft.

Created: 2026-06-09.

## Objective

Organize engine, game, user, and editor preference settings so renderer-neutral policy, OpenGL-specific policy, Vulkan-specific policy, diagnostics, and editor-only workflow preferences have clear owners.

This is a settings taxonomy and resolver refactor first, not a renderer rewrite. The goal is to make backend separation explicit enough that Vulkan dynamic rendering, OpenGL shader-linking, future DX12 work, and editor diagnostics can evolve without adding more flat settings to already-large root classes.

## Current Pressure Points

- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
  - `EngineSettings` mixes common render policy, OpenGL shader-linking, Vulkan feature policy, culling, VR, shadows, diagnostics, and physics visualization settings.
  - `VulkanRobustnessSettings` already demonstrates a better nested backend-specific pattern, but neighboring Vulkan properties remain flat.
- `XRENGINE/Settings/EditorPreferences.cs`
  - `EditorDebugOptions` mixes debug visualization, profiler configuration, generic diagnostics, OpenGL diagnostics, Vulkan diagnostics, and render-pipeline isolation toggles.
- `XRENGINE/Settings/GameStartupSettings.cs`
  - Project overrides are flat, including backend-specific Vulkan/OpenGL overrides.
- `XREngine.Data/Core/UserSettings.cs`
  - `RenderLibrary` is described as a preferred library that may fall back.
- `XRENGINE/Engine/Engine.Windows.cs`
  - Vulkan window creation falls back to OpenGL whenever Vulkan initialization throws. This needs an explicit policy so requested accelerated paths are not hidden behind silent fallback behavior.
- `XREngine.Runtime.Bootstrap/UnitTestingWorldSettings.cs`
  - Unit-testing settings expose OpenGL shader-linking knobs flat. These should eventually map into the same backend settings groups as runtime settings.
- Two effective-settings entry points exist: `Engine.EffectiveSettings` (`XRENGINE`) and `RuntimeEngine.EffectiveSettings` (`XREngine.Runtime.Rendering`). Modularized runtime rendering code reads through `RuntimeEngine.EffectiveSettings`, so the resolver boundary work must cover both surfaces, not just `Engine.EffectiveSettings`.
- Serialization shape, not just names, changes when properties move into nested groups. `EngineSettings` is `[MemoryPackable(GenerateType.NoGenerate)]`, and `EditorPreferences`/`EditorDebugOptions` are `[MemoryPackable]` plus `[Serializable]`. Regrouping affects MemoryPack member layout and asset (de)serialization, so saved-asset compatibility must be evaluated whenever a property is relocated.

## Target Shape

Prefer nested settings groups with clear ownership:

```text
Engine.Rendering.Settings
  Common
  Quality
  Shaders
  Culling
  Shadows
  OpenGL
  Vulkan
  Interop
  Diagnostics
```

Suggested backend-specific groups:

```text
OpenGLRenderSettings
  Context
  ShaderLinking
  ProgramCache
  TextureUpload
  Diagnostics

VulkanRenderSettings
  Startup
  TargetMode
  GpuDriven
  Descriptors
  Synchronization
  Memory
  Robustness
  Diagnostics
```

Suggested editor preference groups:

```text
EditorPreferences
  Viewport
  Selection
  Inspector
  Mcp
  Assistant
  Theme
  Diagnostics
  Profiler

EditorDiagnosticsPreferences
  Visualization
  RenderPipeline
  OpenGL
  Vulkan
  Culling
  Exceptions
```

## Ownership Rules

- `UserSettings`: personal preferences such as preferred backend, display, audio, quality, and user overrides.
- `GameStartupSettings`: project defaults and project-required overrides.
- `Engine.Rendering.Settings`: engine default runtime policy.
- `EditorPreferences`: editor-only workflow, UI state, diagnostics, and development toggles.
- Backend settings should not leak into unrelated categories. Vulkan policy lives under Vulkan settings; OpenGL policy lives under OpenGL settings.
- Renderer code should read effective runtime policy through a resolver or immutable snapshot, not by walking editor/game/user settings directly.
- Diagnostics that change startup behavior, such as OpenGL debug context creation, must clearly state restart requirements.
- Explicit requested GPU/accelerated paths must fail visibly unless fallback was explicitly requested.

## Phase 0 - Baseline Audit

- [ ] Create a dedicated branch for this todo before starting implementation work.
- [ ] Inventory flat backend-specific settings in `Engine.Rendering.EngineSettings`.
- [ ] Inventory flat backend-specific project overrides in `GameStartupSettings`.
- [ ] Inventory backend-specific editor diagnostics in `EditorDebugOptions`.
- [ ] Inventory unit-testing world render settings that should map into backend groups.
- [ ] Identify saved asset compatibility needs for current settings names.
- [ ] Assess MemoryPack member layout and `[Serializable]` impact for `EngineSettings`, `EditorPreferences`, and `EditorDebugOptions` before moving properties, since regrouping changes serialized shape and not just names.
- [ ] Decide whether temporary flat compatibility properties are worth keeping during the refactor. Since v1 has not shipped, they may be removed before completion if migration cost is lower than shim cost.

## Phase 1 - Backend Startup And Fallback Policy

- [ ] Add an explicit backend fallback enum, for example:

  ```csharp
  public enum RenderBackendFallbackPolicy
  {
      RequireRequested,
      FallbackWithWarning,
      AutoPreferRequested,
  }
  ```

- [ ] Add the fallback policy to the appropriate startup/settings owner.
- [ ] Update `Engine.CreateWindow()` so Vulkan-to-OpenGL fallback only happens when policy permits it.
- [ ] Ensure required Vulkan mode fails visibly with a diagnostic that includes requested backend, fallback policy, and exception summary.
- [ ] Update `docs/architecture/rendering/window-creation-and-renderer-init.md` after behavior changes.
- [ ] Add targeted tests or source-contract tests for fallback policy behavior if feasible.

## Phase 2 - Extract OpenGL Runtime Settings

- [ ] Add `OpenGLRenderSettings : XRBase`.
- [ ] Move or forward OpenGL shader-linking settings into `OpenGLRenderSettings.ShaderLinking`:
  - `AllowBinaryProgramCaching`
  - `AsyncProgramBinaryUpload`
  - `AsyncProgramCompilation`
  - `OpenGLProgramCompileLinkWorkerCount`
  - `MaxAsyncShaderProgramsPerFrame`
  - `OpenGLShaderLinkStrategy`
  - `OpenGLShaderCompilerThreadCount`
  - `OpenGLParallelShaderCompileProbeEnabled`
  - `OpenGLParallelShaderCompileProbeTimeoutMs`
- [ ] Move `UseDetailPreservingComputeMipmaps` into an OpenGL texture/upload group if it remains OpenGL-only.
- [ ] Rename or relocate `AllowShaderPipelines` if it is specifically OpenGL program-pipeline policy, for example `OpenGL.AllowProgramPipelines`.
- [ ] Update `BootstrapRenderSettings.ApplyOpenGLShaderLinkSettings()` to write the nested OpenGL settings group.
- [ ] Update OpenGL renderer consumers to read through `Engine.EffectiveSettings` or a resolved OpenGL snapshot.
- [ ] Update `docs/architecture/rendering/opengl-renderer.md`.

## Phase 3 - Extract Vulkan Runtime Settings

- [ ] Add `VulkanRenderSettings : XRBase`.
- [ ] Move or forward existing Vulkan properties into nested groups:
  - `VulkanGpuDrivenProfile`
  - `VulkanQueueOverlapMode`
  - `EnableVulkanDescriptorIndexing`
  - `EnableVulkanBindlessMaterialTable`
  - `ValidateVulkanDescriptorContracts`
  - `VulkanGeometryFetchMode`
  - `VulkanRobustnessSettings`
- [ ] Move dynamic-rendering target mode policy into `VulkanRenderSettings.TargetMode` if it is not already cleanly owned.
- [ ] Ensure `VulkanRobustnessSettings` remains the sub-owner for allocator, synchronization, and descriptor-update migration policy.
- [ ] Update `VulkanFeatureProfile`, Vulkan renderer initialization, and feature fingerprint logging to consume the grouped settings.
- [ ] Update `docs/architecture/rendering/vulkan-renderer.md`.
- [ ] Cross-check against `docs/work/todo/rendering/vulkan-dynamic-rendering-migration-todo.md` so target-mode wording remains consistent.

## Phase 4 - Extract Shared Render Policy Groups

- [ ] Add or identify shared groups for renderer-neutral policy:
  - shader shape and clip-space policy
  - culling/BVH policy
  - quality/AA/upscaling policy
  - shadows
  - VR rendering policy
  - GPU memory and upload budget policy
- [ ] Keep backend-specific implementation details out of shared groups.
- [ ] Review names like `CalculateSkinningInComputeShader` and `CalculateBlendshapesInComputeShader`; keep them shared only if both active backends support them through the same engine contract.
- [ ] Move settings only when they produce clearer ownership. Avoid a large mechanical shuffle without better resolver boundaries.

## Phase 5 - Effective Settings Resolver

- [ ] Introduce immutable resolved snapshots for renderer-facing code, for example:

  ```csharp
  public readonly record struct EffectiveRenderSettingsSnapshot(
      EffectiveCommonRenderSettings Common,
      EffectiveOpenGLRenderSettings OpenGL,
      EffectiveVulkanRenderSettings Vulkan);
  ```

- [ ] Keep `Engine.EffectiveSettings` as the cascade resolver boundary.
- [ ] Cover `RuntimeEngine.EffectiveSettings` (`XREngine.Runtime.Rendering`) as the matching boundary for modularized runtime rendering code, so both effective-settings surfaces expose the grouped settings consistently.
- [ ] Make renderer code consume effective snapshots where possible.
- [ ] Keep project/user/editor cascade logic out of backend renderer classes.
- [ ] Ensure snapshot creation does not allocate in per-frame hot paths.
- [ ] Add tests for representative cascade resolution:
  - engine default only
  - project override
  - user override where supported
  - editor override where supported
  - backend-specific settings unaffected by the inactive backend

## Phase 6 - Project And User Overrides

- [ ] Add grouped override classes where useful:

  ```text
  GameRenderingOverrides
    Common
    OpenGL
    Vulkan
    Quality
    Technical

  UserRenderingOverrides
    Common
    Quality
    Performance
  ```

- [ ] Move Vulkan-specific project overrides under Vulkan override groups.
- [ ] Move OpenGL-specific project overrides under OpenGL override groups.
- [ ] Preserve full cascade semantics for settings that are intentionally user-overridable.
- [ ] Keep technical project-only settings out of user preferences unless there is a real user workflow.
- [ ] Update effective settings panel labeling so grouped settings show their source clearly.

## Phase 7 - Editor Preferences And Diagnostics

- [ ] Add `EditorViewportPreferences` for viewport presentation, scene depth preference, resize debounce, and scene-panel behavior.
- [ ] Add `EditorSelectionPreferences` for hover/selection outline and GPU mesh BVH pick preference.
- [ ] Add `EditorProfilerPreferences` for profiler collection, transport, and panel display settings.
- [ ] Add `EditorDiagnosticsPreferences`.
- [ ] Move OpenGL diagnostics into `EditorDiagnosticsPreferences.OpenGL`, including:
  - GL debug context toggle
  - GL submit trace level
  - GL-specific crash breadcrumbs if still GL-only
- [ ] Move Vulkan diagnostics into `EditorDiagnosticsPreferences.Vulkan`, including:
  - auto-uniform rewrite
  - dump shader on error
  - pipeline creation trace
  - swapchain draw trace
  - all-draw trace
  - skip UI pipeline
  - force swapchain magenta
  - skip ImGui
- [ ] Keep generic diagnostics such as first-chance exception filters and model render diagnostics under generic diagnostics groups.
- [ ] Update `EditorPreferencesOverrides` with matching grouped override classes.
- [ ] Update `EditorPreferences.CopyFrom()` and `ApplyOverrides()` to delegate to subsettings instead of mapping every backend diagnostic flatly.
- [ ] Update ImGui settings panels and effective settings panels for the new grouping.

## Phase 8 - Unit Testing World Settings

- [ ] Group unit-testing world OpenGL shader-linking settings in JSONC schema and settings model.
- [ ] Group unit-testing world render backend selection and fallback policy.
- [ ] Update `Tools/Generate-UnitTestingWorldSettings.ps1` and schema generation if settings shape changes.
- [ ] Update `Assets/UnitTestingWorldSettings.jsonc` documentation notes if needed.
- [ ] Preserve clear logging of resolved OpenGL shader-link settings during bootstrap.

## Phase 9 - Cleanup And Naming

- [ ] Remove obsolete flat properties once all consumers are moved.
- [ ] Remove temporary compatibility shims if no saved asset migration is needed before v1.
- [ ] Rename ambiguous settings:
  - `RenderLibrary` may become `PreferredRenderBackend` if fallback remains allowed.
  - `RenderAPI` in unit-testing settings may become `RenderBackend`.
  - `AllowShaderPipelines` may become backend-specific if it only affects OpenGL.
- [ ] Audit XML docs and property descriptions for backend specificity.
- [ ] Audit editor categories to avoid broad "Debug" buckets swallowing backend policy.
- [ ] Ensure all moved `XRBase` properties use `SetField(...)`.

## Phase 10 - Validation

- [ ] Build the editor:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- [ ] Run focused settings tests:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Settings
  ```

- [ ] Run focused rendering settings tests, adding them if no useful filter exists:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Rendering
  ```

- [ ] Validate editor startup with OpenGL.
- [ ] Validate editor startup with Vulkan dynamic rendering.
- [ ] Validate editor startup with Vulkan legacy render-pass mode.
- [ ] Validate required Vulkan mode fails visibly when Vulkan initialization is unavailable or forced to fail.
- [ ] Validate fallback mode logs the fallback reason when fallback is allowed.
- [ ] Validate profiler, diagnostics, and settings panels still show grouped settings clearly.
- [ ] Merge the todo branch back into `main` after completion and validation.

## Documentation Checklist

- [ ] `docs/architecture/rendering/window-creation-and-renderer-init.md`
- [ ] `docs/architecture/rendering/opengl-renderer.md`
- [ ] `docs/architecture/rendering/vulkan-renderer.md`
- [ ] `docs/architecture/rendering/default-render-pipeline-notes.md`, if shared render policy names change.
- [ ] `docs/architecture/rendering/mesh-submission-strategies.md`, if GPU-driven/Vulkan profile names or locations change.
- [ ] `.vscode/schemas/unit-testing-world-settings.schema.json`, if unit-testing settings shape changes.

## Open Questions

- Should backend fallback be allowed by default in the editor, or should the editor require explicit `AutoPreferRequested`/`FallbackWithWarning`?
- Should `VulkanRenderTargetMode` remain environment-variable driven only, or become a persisted Vulkan startup setting with env override?
- Should editor diagnostics write directly into `RenderDiagnosticsFlags`, or should there be a central diagnostics application service that owns env seed, preference seed, and live toggles?
- Should current flat asset properties be migrated through compatibility shims, or removed directly because v1 has not shipped?
- Should DX12 placeholders be introduced now as an interface/shape concern, or delayed until DX12 runtime work resumes?

## Completion Criteria

- Backend-specific settings are grouped under backend-specific owners.
- Renderer-neutral settings no longer carry backend-specific names.
- `Engine.EffectiveSettings` or equivalent snapshots are the only cascade boundary renderer code needs.
- Explicit requested Vulkan paths do not silently fall back to OpenGL.
- Editor diagnostics are separated from editor visual/debug preferences.
- OpenGL and Vulkan docs describe the new settings ownership and fallback policy.
- Targeted builds/tests pass, or unrelated failures are documented.
