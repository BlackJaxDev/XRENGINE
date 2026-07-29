# Advanced Render Pipeline Identity Slice - 2026-07-28

Status: Complete
Parent TODO:
[01 - Pipeline Identity And Frame Contract](../../todo/rendering/architectural-refactor/01-pipeline-identity-and-frame-contract-todo.md)

Subsequent routing contract:
[Output-Purpose And Feature-Contract Slice](advanced-render-pipeline-output-purpose-and-feature-contract-slice-2026-07-28.md)
Starting Commit: `e4326aa43df2eac7b8880434491718ff9c230cd4`

> Selection details in this historical slice were superseded by the
> [Capability Selection Slice](advanced-render-pipeline-capability-selection-slice-2026-07-28.md).
> Current builds use `XRE_ADVANCED_RENDER_PIPELINE_MODE`.

## Scope

Establish the new pipeline identity without changing render output or pass
ordering. This slice intentionally preserves the former migration pipeline's
frame graph so the subsequent architectural work begins from a compiling,
selectable, consistently named boundary.

## Completed

- Moved the source folder from `Types/Default2/` to `Types/Advanced/`.
- Renamed all pipeline partial files and the runtime type to
  `AdvancedRenderPipeline`.
- Removed the `DefaultRenderPipeline2` runtime type without an alias, subclass,
  or forwarding facade.
- Renamed the local opt-in selector to
  `XRE_USE_ADVANCED_RENDER_PIPELINE=1` and the corresponding environment
  constant/property.
- Updated the central pipeline factory while preserving selection behavior:
  `DefaultRenderPipeline` remains the default and the advanced pipeline remains
  opt-in.
- Updated editor inspectors, OpenXR cloning, stereo detection, GI/light
  feature commands, probe synchronization, motion/depth/overdraw feature
  commands, Vulkan upscale integration, diagnostic labels, and source-contract
  paths.
- Updated current architecture/developer documentation and active todos that
  referenced the live type or source path.
- Added `AdvancedRenderPipelineIdentityTests`.

## Behavior

- Rendering behavior is intentionally unchanged in this slice.
- `XRE_USE_ADVANCED_RENDER_PIPELINE=1` selects
  `AdvancedRenderPipeline`.
- The former `XRE_USE_PIPELINE_V2` name is no longer recognized.
- Leaving the advanced selector unset continues to create
  `DefaultRenderPipeline`.
- At the time of this identity-only slice, RVC and debug-opaque retained global
  precedence. The subsequent output-purpose slice supersedes that behavior:
  RVC owns OpenXR eyes and debug-opaque is desktop-only.

## Validation

### Build

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Final incremental result: passed with 0 errors. The 10 reported warnings were
the existing `Magick.NET` NuGet advisory; this slice introduced no compiler
warning. An earlier full build also reported the existing `OscCore-NET9`
warnings.

### Identity Tests

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~AdvancedRenderPipelineIdentityTests"
```

Result: 2 passed.

### Affected Rendering Contract Tests

The changed rendering test classes were run in three bounded groups:

- material/AO/atmosphere/depth/probe/editor contracts: 72 passed;
- mesh/OpenXR/capture/resource/secondary-pass/stereo/post contracts:
  322 passed;
- Vulkan command/deferred-probe/P0/P1/upscale contracts:
  252 passed, 1 expected skip.

Total focused result: 648 passed, 1 skipped, 0 failed.

An initial attempt to run the entire rendering namespace exceeded five minutes.
Its owned test process was terminated and no test host was left running. The
focused groups above cover every rendering test class changed by this slice.

## Worktree Exclusions

The following pre-existing workspace state was not modified as part of this
slice:

- `Build/Submodules/OscCore-NET9`
- `Build/Dependencies/vcpkg/`
- `Build/Submodules/Flyleaf/`
- `Build/Submodules/MagicPhysX/`

## Remaining Document 01 Work

- Replace the temporary binary environment selector with the final explicit
  pipeline-kind setting.
- Introduce `AdvancedRenderPipelineCapabilities` and structured rejection
  reasons.
- Replace concrete editor/GI feature checks with focused provider interfaces.
- Disconnect the inherited deferred/opaque-forward frame graph and establish
  the advanced stage skeleton.
- Add command-tree, resource-layout, and required-mode failure tests for that
  new skeleton.
