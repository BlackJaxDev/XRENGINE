# Finalized Game Builds And Asset Cooking

Last Updated: 2026-07-28
Status: Engine usage guide. NativeAOT publishing now enforces strict cooked assets, a statically rooted game bootstrap, complete archives, zero IL2xxx/IL3xxx warnings, and a positive smoke completion marker.

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
3. Builds the managed game assembly so custom types are available to the cooker and launcher generator.
4. Resolves the single public `IGameLaunchBootstrap` required by NativeAOT builds.
5. Cooks eligible project assets into `Content/GameContent.pak`.
6. Generates `Config/GameConfig.pak`.
7. Copies requested runtime binaries and packs either the full engine assets or
   the complete runtime shader tree as `Content/CommonAssets.pak`.
8. Builds or publishes the generated launcher executable into `Binaries/`.

`.asset` YAML files with either a `__assetType` or legacy `__type` hint are converted into `CookedAssetBlob` payloads. NativeAOT publishing requires every shipped asset to use `RuntimeBinaryV1`; an unknown, uncookable, or unregistered asset fails the cook instead of falling back to authoring YAML.

Eligible non-asset files are copied as-is. C# source, project/solution files, user files, PDBs, `.editorconfig`, and root `startup.asset` / `state.asset` launcher inputs are excluded from `GameContent.pak`.

## Output Layout

For a project rooted at `<ProjectRoot>` and `OutputSubfolder=Publish`, the finalized layout is:

```text
<ProjectRoot>\Build\Publish\
  Binaries\
    Game.exe
    Game.dll                  # non-AOT/framework-dependent launcher only
    Game.runtimeconfig.json    # non-AOT/framework-dependent launcher only
    *.dll, lib\                # NativeAOT publish-produced native dependencies
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
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-MonkeyBallVR.ps1
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

`-NoEditorBuild` uses the already-built editor configuration. Use it only for
local iteration after that editor binary has passed its own build/tests; clean
release automation does not use this option.

`-NoSmoke` skips the generated launcher smoke test. Use it only when you are debugging publish failures before runtime validation.

`-AllowAotWarnings` permits a local diagnostic package, but it is never valid for a release. The default publisher fails on every IL2xxx/IL3xxx warning.

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

`Build/Reports/aot-final-game-publish-warnings.md` is a hard release gate. The default script fails when it contains any IL2xxx/IL3xxx warning.

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
CopyGameAssemblies: false
CopyEngineBinaries: false
CommonAssetsPackageMode: Full
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

The headless NativeAOT build path also disables `CopyGameAssemblies` and
`CopyEngineBinaries`; `dotnet publish` provides the self-contained executable
and required native dependencies. `CommonAssetsPackageMode: Full` packages the
complete engine asset library. Use `CommonAssetsPackageMode: RuntimeShaders`
for a procedural game that creates every other runtime asset itself; the
complete shader tree is retained so the render pipeline and shader includes
remain functional. Both modes create a non-empty `CommonAssets.pak`, and a
missing archive remains a hard startup error.

The compiled game assembly must contain exactly one public, concrete `IGameLaunchBootstrap` with a public parameterless constructor. The generated launcher constructs it directly, uses it to configure startup, and obtains the initial `GameState`; this is the supported way to root custom worlds and components under NativeAOT.

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

For MonkeyBall VR, prefer `Tools/Publish-MonkeyBallVR.ps1`; it runs the strict script path and creates the distributable ZIP. The direct MSBuild target remains useful for development diagnostics.

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
- `Build/Reports/aot-final-game-publish-warnings.md` has no IL2xxx/IL3xxx warnings.
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

AOT publish stops because the warning report is non-empty.

Open `Build/Reports/aot-final-game-publish-warnings.md`. Fix first-party runtime warnings, remove editor/dev authoring surfaces from the launcher closure, or replace the warning-producing dependency. Use `-AllowAotWarnings` only to create a local diagnostic artifact while doing that work.

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
