# Finalized Game Builds And Asset Cooking

Last Updated: 2026-06-19
Status: Engine usage guide. NativeAOT final-game publishing exists and the representative MonkeyBallVR smoke path runs, but a production AOT release must still treat non-empty IL2xxx/IL3xxx analyzer warnings as release blockers.

## Purpose

Use this guide when you want to turn an XR project into a finalized cooked game build:

- an executable launcher under the project `Build` folder
- cooked project content in `GameContent.pak`
- cooked startup/config data in `GameConfig.pak`
- engine common assets in `CommonAssets.pak`
- either a NativeAOT launcher or an explicitly non-AOT published launcher

This is not the editor workflow. The editor remains the authoring, hot-reload, loose YAML, and dynamic plugin environment.

## Choose The Build Mode

| Mode | Launcher | Defines | Runtime behavior | Use when |
|---|---|---|---|---|
| NativeAOT finalized game | `PublishAot=true`, self-contained `win-x64` launcher | `XRE_PUBLISHED;XRE_AOT_RUNTIME` | Loads cooked archives and AOT metadata. Rejects dynamic managed plugins, authoring-time YAML asset loading, and unregistered runtime cooked asset types. | Shipping candidate once analyzer warnings are clean or explicitly accepted. |
| Non-AOT finalized game | Normal managed launcher build | `XRE_PUBLISHED` | Loads cooked archives as a published game, but still runs on CoreCLR/JIT. | Finalized local/QA builds, or release candidates that still need dynamic-code-compatible runtime paths. |
| Development/editor | Editor or loose launcher without `XRE_PUBLISHED` | none | Uses development asset and reflection paths. | Authoring, debugging, hot reload, plugin iteration. |

The important distinction is that "not AOT" does not mean "not cooked." A non-AOT finalized build should still define `XRE_PUBLISHED` so the generated launcher uses the packed config/content archives instead of development asset paths.

## What Gets Cooked

The project builder runs these steps for finalized builds:

1. Saves project build settings when `SaveSettingsBeforeBuild` is enabled.
2. Prepares `Build/<OutputSubfolder>/`.
3. Cooks every file under the project `Assets` directory into `Content/GameContent.pak`.
4. Builds managed game assemblies.
5. Generates `Config/GameConfig.pak`.
6. Copies game assemblies when requested.
7. Copies engine runtime binaries and packs common engine assets as `Content/CommonAssets.pak`.
8. Builds or publishes the generated launcher executable into `Binaries/`.

`.asset` YAML files with a `__assetType` hint are converted into `CookedAssetBlob` payloads. Asset types registered with `PublishedCookedAssetRegistry` cook as `RuntimeBinaryV1`; other supported assets use the generic `BinaryV1` cooked format.

NativeAOT published runtime builds only accept `RuntimeBinaryV1` for runtime-loaded assets. If an AOT executable tries to load a legacy `BinaryV1` runtime asset, the fix is to add an explicit published cooked serializer/registry entry for that asset type and republish content.

Non-asset files are copied into the content archive as-is.

## Output Layout

For a project rooted at `<ProjectRoot>` and `OutputSubfolder=Publish`, the finalized layout is:

```text
<ProjectRoot>\Build\Publish\
  Binaries\
    Game.exe
    Game.dll                  # non-AOT/framework-dependent launcher only
    Game.runtimeconfig.json    # non-AOT/framework-dependent launcher only
    *.dll, *.json, runtimes\   # copied runtime dependencies as needed
  Config\
    GameConfig.pak
  Content\
    GameContent.pak
    CommonAssets.pak
```

A NativeAOT config archive also contains:

```text
AotRuntimeMetadata.bin
```

The generated launcher resolves archives relative to `AppContext.BaseDirectory` first, then from sibling `Config` and `Content` folders beside `Binaries`.

## NativeAOT Finalized Build

Use the validation script for the canonical AOT path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-AotFinalGame.ps1 -ProjectPath .\Samples\MonkeyBallVR\MonkeyBallVR.xrproj
```

Useful script options:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-AotFinalGame.ps1 `
  -ProjectPath .\Samples\MonkeyBallVR\MonkeyBallVR.xrproj `
  -BuildConfiguration Release `
  -BuildPlatform Windows64 `
  -OutputSubfolder Publish `
  -LauncherName Game.exe `
  -SmokeTimeoutSeconds 30
```

`-NoClean` keeps existing generated build artifacts for faster local iteration. Do not use it for release validation unless you intentionally want to validate an incremental archive update.

`-NoSmoke` skips the generated launcher smoke test. Use it only when you are debugging publish failures before runtime validation.

The script does all of the following:

- runs the editor build command headlessly
- sets `--publish-native-aot true`
- sets `--validate-aot true`
- publishes the generated launcher with `PublishAot=true`, `SelfContained=true`, and `RuntimeIdentifier=win-x64`
- automatically adds `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`
- writes `GameConfig.pak`, `GameContent.pak`, and `CommonAssets.pak`
- copies the final launcher to `Build/<OutputSubfolder>/Binaries/<LauncherName>`
- runs `<LauncherName> --aot-smoke` unless `-NoSmoke` is passed

Validation outputs are written under `Build/Reports/`:

```text
Build/Reports/aot-final-game-publish.log
Build/Reports/aot-final-game-launcher-publish.log
Build/Reports/aot-final-game-publish-warnings.md
Build/Reports/aot-final-game-smoke.log
```

Treat `Build/Reports/aot-final-game-publish-warnings.md` as a release gate. A production AOT release must have no IL2xxx/IL3xxx warnings, or each remaining warning must be explicitly classified, justified, and accepted by the release owner.

## Direct AOT CLI

For custom projects or local automation, the script maps to this editor CLI shape:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- `
  --build-project .\Path\To\Game.xrproj `
  --build-configuration Release `
  --build-platform Windows64 `
  --output-subfolder Publish `
  --launcher-name Game.exe `
  --publish-native-aot true `
  --validate-aot true
```

Then run the generated smoke check:

```powershell
.\Path\To\Project\Build\Publish\Binaries\Game.exe --aot-smoke
```

The `--aot-smoke` path verifies that the published config archive exists, `AotRuntimeMetadata.bin` is loadable, key config assets are runtime-binary cooked, and content/common archives can be opened.

## Non-AOT Finalized Build

Use this when you want a cooked published game, but you specifically do not want NativeAOT:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- `
  --build-project .\Path\To\Game.xrproj `
  --build-configuration Release `
  --build-platform Windows64 `
  --output-subfolder PublishJit `
  --launcher-name Game.exe `
  --publish-native-aot false `
  --validate-aot false `
  --define-constants XRE_PUBLISHED
```

The `XRE_PUBLISHED` define is required for a finalized non-AOT launcher. Without it, the generated program configures itself as a development build and does not use the published archive-loading path.

Do not use `--aot-smoke` for the non-AOT build unless you have intentionally added AOT metadata to its config archive. Run the produced executable normally:

```powershell
.\Path\To\Project\Build\PublishJit\Binaries\Game.exe
```

Use a different output subfolder, such as `PublishJit`, when keeping AOT and non-AOT builds side by side.

## Build Settings Asset

Projects store build settings in `Config/build_settings.asset`. In the editor, use the ImGui Build Settings panel or edit the asset directly.

Recommended NativeAOT finalized settings:

```yaml
Configuration: Release
Platform: Windows64
OutputSubfolder: Publish
CleanOutputDirectory: true
CookContent: true
BuildManagedAssemblies: true
CopyGameAssemblies: true
CopyEngineBinaries: true
BuildLauncherExecutable: true
PublishLauncherAsNativeAot: true
ValidateLauncherAotCompatibility: true
GenerateConfigArchive: true
ContentArchiveName: GameContent.pak
ConfigArchiveName: GameConfig.pak
ContentOutputFolder: Content
ConfigOutputFolder: Config
BinariesOutputFolder: Binaries
LauncherExecutableName: Game.exe
```

Recommended non-AOT finalized differences:

```yaml
OutputSubfolder: PublishJit
PublishLauncherAsNativeAot: false
ValidateLauncherAotCompatibility: false
LauncherDefineConstants: XRE_PUBLISHED
```

When `PublishLauncherAsNativeAot` is true, the builder forces content cooking and config archive generation even if those settings were disabled. AOT launchers also automatically receive `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`.

## Sample Project MSBuild Targets

`Samples/MonkeyBallVR/MonkeyBallVR.csproj` exposes a `CookGameExe` target for sample automation.

NativeAOT published sample build:

```powershell
dotnet msbuild .\Samples\MonkeyBallVR\MonkeyBallVR.csproj /t:CookGameExe /p:Configuration="Published Release"
```

Explicitly non-AOT published sample build:

```powershell
dotnet msbuild .\Samples\MonkeyBallVR\MonkeyBallVR.csproj /t:CookGameExe `
  /p:Configuration="Published Release" `
  /p:GamePublishNativeAot=false `
  /p:GameOutputSubfolder=PublishJit `
  /p:GameDefineConstants=XRE_PUBLISHED
```

The script-based AOT path is still preferred for release validation because it captures publish logs, warning classification, and smoke-test output in `Build/Reports/`.

## Release Validation Checklist

For any finalized build:

- `Build/<OutputSubfolder>/Binaries/<LauncherName>` exists.
- `Build/<OutputSubfolder>/Config/GameConfig.pak` exists.
- `Build/<OutputSubfolder>/Content/GameContent.pak` exists.
- `Build/<OutputSubfolder>/Content/CommonAssets.pak` exists when engine common assets are required.
- The launcher starts from the `Binaries` directory.
- The launcher loads startup settings from the config archive.
- Representative project content loads from the content archive.
- Rendering/input/world startup is smoke-tested in the target mode.

Additional NativeAOT checks:

- `GameConfig.pak` contains `AotRuntimeMetadata.bin`.
- `--aot-smoke` succeeds.
- `Build/Reports/aot-final-game-publish-warnings.md` has no unaccepted IL2xxx/IL3xxx warnings.
- Runtime-loaded asset types are registered with `PublishedCookedAssetRegistry`.
- Runtime-created types use explicit factories/registries or metadata-backed lookup.
- Runtime C# plugin loading, hot reload, and authoring-time YAML asset loading are not part of the shipped path.

## Current NativeAOT Boundaries

Supported first-class AOT target:

- generated cooked final game launcher
- Windows `win-x64`
- self-contained NativeAOT publish
- packed config/content/common asset archives
- metadata-backed runtime type resolution

Not currently AOT targets:

- `XREngine.Editor`
- editor/dev reflection tooling
- runtime C# plugin loading and hot reload
- `XREngine.Server`
- `XREngine.VRClient`
- optional runtime integrations unless a final launcher statically includes and validates them

## Troubleshooting

`Config archive '<path>' not found.`

The launcher is compiled as `XRE_PUBLISHED`, but the expected `Config/GameConfig.pak` is missing or the executable is being run from an unexpected layout. Rebuild with a clean output folder and run from `Build/<OutputSubfolder>/Binaries/`.

`Published AOT runtime metadata is missing.`

The executable is running as `XRE_AOT_RUNTIME`, but `AotRuntimeMetadata.bin` is not present in `GameConfig.pak`. Rebuild with `--publish-native-aot true`, clean stale archives, and verify the config archive was regenerated.

`Cooked asset type ... was published with legacy 'BinaryV1'.`

The asset is being loaded by a published AOT runtime without an explicit runtime serializer. Register the type with `PublishedCookedAssetRegistry`, make sure it cooks as `RuntimeBinaryV1`, and republish content.

`No published cooked asset serializer is registered for ...`

The cooked blob says `RuntimeBinaryV1`, but the final runtime did not register a matching serializer. Ensure the registration assembly is referenced by the final launcher closure and that the registration runs before asset load.

The non-AOT build starts but does not load cooked archives.

Confirm the generated launcher was built with `XRE_PUBLISHED`. For the CLI, pass `--define-constants XRE_PUBLISHED`. For build settings, set `LauncherDefineConstants: XRE_PUBLISHED`.

AOT publish succeeds but warning report is non-empty.

Open `Build/Reports/aot-final-game-publish-warnings.md`. First-party runtime warnings should be fixed or removed from the shipped closure. Editor/dev authoring warnings should be excluded by narrowing the launcher closure. Third-party warnings require explicit owner acceptance before release.

## Useful Files

- `Tools/Publish-AotFinalGame.ps1`
- `XREngine.Data/Core/BuildSettings.cs`
- `XREngine.Editor/Program.cs`
- `XREngine.Editor/ProjectBuilder.cs`
- `XREngine.Editor/CodeManager.cs`
- `XREngine.Runtime.Core/XRRuntimeEnvironment.cs`
- `XREngine.Runtime.Core/AotRuntimeMetadata.cs`
- `XREngine.Runtime.Core/AotRuntimeMetadataStore.cs`
- `XRENGINE/Core/Files/CookedAssetBlob.cs`
- `XRENGINE/Core/Files/CookedAssetTypeReference.cs`
- `XREngine.Runtime.Core/Files/PublishedCookedAssetRegistry.cs`
- `XRENGINE/Core/Files/PublishedCookedAssetRegistryRegistration.cs`
- `XREngine.Runtime.Rendering/Core/Files/PublishedCookedAssetRegistryRegistration.cs`
- `docs/developer-guides/runtime/aot-final-game-builds.md`
- `docs/architecture/assets/cooked-asset-aot-and-io.md`
