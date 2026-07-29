# AOT Final Game Builds

NativeAOT support is scoped to cooked final game launchers. The editor, hot-reload, runtime C# plugin loading, and authoring-time YAML workflows remain CoreCLR development surfaces.

## Canonical Validation

Use the validation script from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-MonkeyBallVR.ps1
```

The matching VS Code task is `Publish-VRMonkeyBall-NativeAOT-Package`. This
creates the validated `Samples/MonkeyBallVR/Build/Packages/MonkeyBallVR-win-x64.zip`
release artifact. For another project, call `Tools/Publish-AotFinalGame.ps1`
directly with its `.xrproj`.

A published AOT game assembly must contain exactly one public, parameterless
`IGameLaunchBootstrap`. The generated launcher constructs it directly so the
game's custom startup settings, state, world, and component graph remain
statically rooted.

The script:

- builds the generated launcher with `PublishAot=true`
- defines `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`
- enables trim and AOT analyzers for the generated launcher closure
- writes publish output to `Build/Reports/aot-final-game-publish.log`
- copies the generated launcher NativeAOT log to `Build/Reports/aot-final-game-launcher-publish.log`
- writes classified IL2xxx/IL3xxx warning triage input to `Build/Reports/aot-final-game-publish-warnings.md`
- runs the published launcher with `--aot-smoke` unless `-NoSmoke` is passed
- fails when any IL2xxx/IL3xxx warning remains, unless
  `-AllowAotWarnings` is explicitly used for local diagnosis
- requires config, content, and common-assets archives and the smoke completion
  marker

Use `-NoClean` when you intentionally want to reuse existing cooked archives during local iteration.

Use `-NoEditorBuild` only after the matching editor configuration has already
been built and validated. It passes `--no-build` to `dotnet run`; clean CI and
release automation intentionally build the editor from source.

`-AllowAotWarnings` is not a release acceptance mechanism. It only permits a
diagnostic package so remaining warnings can be investigated.

## Smoke Checklist

A supported AOT final-game validation must prove:

- the final launcher publishes successfully
- `Build/<OutputSubfolder>/Binaries/<LauncherName>.exe` exists
- `Build/<OutputSubfolder>/Config/GameConfig.pak` exists
- `Build/<OutputSubfolder>/Content/GameContent.pak` exists
- `Build/<OutputSubfolder>/Content/CommonAssets.pak` exists
- `GameConfig.pak` contains `AotRuntimeMetadata.bin`
- startup, user settings, and editor preferences config assets cook as `RuntimeBinaryV1`
- the launcher can load published archives and AOT metadata with `--aot-smoke`
- the smoke output contains `AOT smoke passed:`
- IL2xxx/IL3xxx warnings are absent
- no C# source, project files, PDBs, or root launcher-only startup/state assets
  are present in `GameContent.pak`

For an interactive world/render smoke, run the produced executable without `--aot-smoke` after the script completes.

## Build Settings

`BuildSettings.PublishLauncherAsNativeAot` selects the NativeAOT launcher publish path.

`BuildSettings.ValidateLauncherAotCompatibility` enables analyzer validation for the generated launcher only. The repo-wide editor/dev analyzer defaults remain relaxed in `Directory.Build.props`.

`BuildSettings.CommonAssetsPackageMode` selects `Full` (the default) or
`RuntimeShaders`. Procedural or fully self-contained games can use
`RuntimeShaders`; the builder then packages the complete shader tree and a
manifest while excluding optional engine models, textures, fonts, audio, and
authoring assets.

The headless NativeAOT build command disables `CopyGameAssemblies` and
`CopyEngineBinaries`. The self-contained publish output is the source of truth,
and its native DLLs, license files, and subdirectories are copied beside the
renamed launcher. Analyzer logs and PDBs are excluded from release output.

Published AOT launchers reject legacy `BinaryV1` cooked assets at runtime. Runtime-loadable assets must be registered with `PublishedCookedAssetRegistry` so they cook as `RuntimeBinaryV1`, and any runtime type lookup must resolve through `AotRuntimeMetadata.bin` or an explicit generated registry.
