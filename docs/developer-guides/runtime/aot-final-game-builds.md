# AOT Final Game Builds

NativeAOT support is scoped to cooked final game launchers. The editor, hot-reload, runtime C# plugin loading, and authoring-time YAML workflows remain CoreCLR development surfaces.

## Canonical Validation

Use the validation script from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-AotFinalGame.ps1 -ProjectPath .\Samples\MonkeyBallVR\MonkeyBallVR.xrproj
```

The matching VS Code task is `Publish-AOT-FinalGame-Smoke`.

The script:

- builds the generated launcher with `PublishAot=true`
- defines `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`
- enables trim and AOT analyzers for the generated launcher closure
- writes publish output to `Build/Reports/aot-final-game-publish.log`
- copies the generated launcher NativeAOT log to `Build/Reports/aot-final-game-launcher-publish.log`
- writes classified IL2xxx/IL3xxx warning triage input to `Build/Reports/aot-final-game-publish-warnings.md`
- runs the published launcher with `--aot-smoke` unless `-NoSmoke` is passed

Use `-NoClean` when you intentionally want to reuse existing cooked archives during local iteration.

As of the 2026-06-16 audit, the representative MonkeyBallVR NativeAOT launcher publishes and `--aot-smoke` passes, but the analyzer gate is still closed because the generated launcher closure emits IL2xxx/IL3xxx warnings. Treat any non-empty warning report as a release blocker until the warning is fixed, explicitly suppressed with a documented reason, or excluded by narrowing the shipped closure.

## Smoke Checklist

A supported AOT final-game validation must prove:

- the final launcher publishes successfully
- `Build/<OutputSubfolder>/Binaries/<LauncherName>.exe` exists
- `Build/<OutputSubfolder>/Config/GameConfig.pak` exists
- `Build/<OutputSubfolder>/Content/GameContent.pak` exists
- `GameConfig.pak` contains `AotRuntimeMetadata.bin`
- startup, user settings, and editor preferences config assets cook as `RuntimeBinaryV1`
- the launcher can load published archives and AOT metadata with `--aot-smoke`
- IL2xxx/IL3xxx warnings are absent, or every remaining warning is explicitly classified and accepted by the release owner

For an interactive world/render smoke, run the produced executable without `--aot-smoke` after the script completes.

## Build Settings

`BuildSettings.PublishLauncherAsNativeAot` selects the NativeAOT launcher publish path.

`BuildSettings.ValidateLauncherAotCompatibility` enables analyzer validation for the generated launcher only. The repo-wide editor/dev analyzer defaults remain relaxed in `Directory.Build.props`.

Published AOT launchers reject legacy `BinaryV1` cooked assets at runtime. Runtime-loadable assets must be registered with `PublishedCookedAssetRegistry` so they cook as `RuntimeBinaryV1`, and any runtime type lookup must resolve through `AotRuntimeMetadata.bin` or an explicit generated registry.
