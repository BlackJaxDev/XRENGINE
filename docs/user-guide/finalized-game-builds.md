# Finalized Game Builds And Asset Cooking

Last Updated: 2026-07-28
Status: Engine usage guide. NativeAOT publishing now enforces strict cooked assets, a statically rooted game bootstrap, complete archives, zero IL2xxx/IL3xxx warnings, and a positive smoke completion marker.

## Purpose

Use this guide when you want to turn an XR project into a finalized cooked game build:

- an executable launcher under the project `Build` folder
- cooked project content in `GameContent.pak`
- cooked startup/config data in `GameConfig.pak`
- engine common assets in `CommonAssets.pak`
- a NativeAOT launcher (production published builds are always NativeAOT)

This is not the editor workflow. The editor remains the authoring, hot-reload, loose YAML, and dynamic plugin environment.

## Choose The Build Mode

| Mode | Launcher | Defines | Runtime behavior | Use when |
|---|---|---|---|---|
| NativeAOT finalized game | `PublishAot=true`, self-contained `win-x64` launcher | `XRE_PUBLISHED;XRE_AOT_RUNTIME` | Loads cooked archives and AOT metadata. Rejects dynamic managed plugins, authoring-time YAML asset loading, and unregistered runtime cooked asset types. | Shipping candidate once analyzer warnings are clean or explicitly accepted. |
| Development launcher that reads archives | Normal managed launcher build, `Debug` or `Development` configuration | `XRE_DEVELOPMENT_ARCHIVES` | Loads the config, content, and common-assets archives (also the `lod/` entries), but stays a development build with reflection paths. | Development and testing of cooked content without NativeAOT. Never a production build. |
| Development/editor | Editor or loose launcher without `XRE_PUBLISHED` | none | Uses development asset and reflection paths. | Authoring, debugging, hot reload, plugin iteration. |

Production published builds must use NativeAOT. A non-AOT launcher that defines `XRE_PUBLISHED` or `XRE_AOT_RUNTIME` fails the build in every configuration. To test cooked archives without NativeAOT, use the [development launcher that reads archives](#development-launcher-that-reads-archives).

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-RollingBall.ps1
```

Useful script options:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-AotFinalGame.ps1 `
  -ProjectPath .\Samples\RollingBall\RollingBall.xrproj `
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

## Development Launcher That Reads Archives

Use this mode to test cooked content, such as split cooked models with separate `lod/` entries, in a non-AOT launcher. It is for development and testing only. Production builds must use NativeAOT.

Set `DevelopmentLauncherReadsArchives: true` in the build settings (or pass `--development-archives true` to the headless `--build-project` command), keep `PublishLauncherAsNativeAot: false`, and use the `Debug` or `Development` configuration. The builder then:

- cooks the content archive and writes the config archive, as for a NativeAOT build;
- adds the `XRE_DEVELOPMENT_ARCHIVES` define constant to the generated launcher;
- does not add `XRE_PUBLISHED` or `XRE_AOT_RUNTIME`.

The generated launcher configures the `EXRRuntimeBuildKind.DevelopmentArchives` build kind. It is not `Published` or `PublishedAot`, so `XRRuntimeEnvironment.IsPublishedBuild` is false and reflective loading stays available. At startup, the launcher writes one line that says it is a development build that reads the archives. A missing config, content, or common-assets archive stops the launcher. Asset loads read the archives through the authoring cooked reader, which also reads the reflective formats of a non-AOT cook. Separate `lod/` entries resolve through `PublishedArchiveRegistry`.

This mode never uses a loose file in place of an archive entry. When an archive is configured for a request, an entry that does not read, an entry with a stale contract version, or a missing entry fails the load with an error that names the asset and the archive. Re-cook the content, or cook the missing asset into the archives.

A build with this option and the `Release` configuration fails with an error, because the project marks production builds with the `Release` configuration. The same rule applies when `LauncherDefineConstants` lists `XRE_DEVELOPMENT_ARCHIVES` by hand. A NativeAOT launcher ignores the option and never takes the define constant. Do not add `XRE_PUBLISHED` to this mode: a non-AOT launcher with that define fails the build.

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

Settings for a development launcher that reads the archives (development and testing only):

```yaml
Configuration: Development
OutputSubfolder: DevArchives
PublishLauncherAsNativeAot: false
ValidateLauncherAotCompatibility: false
DevelopmentLauncherReadsArchives: true
LauncherDefineConstants: ""
```

When `PublishLauncherAsNativeAot` is true, the builder forces content cooking and config archive generation even if those settings were disabled. AOT launchers also automatically receive `XRE_PUBLISHED` and `XRE_AOT_RUNTIME`. A non-AOT launcher with `DevelopmentLauncherReadsArchives: true` also forces content cooking and config archive generation, and receives `XRE_DEVELOPMENT_ARCHIVES`.

The headless NativeAOT build path also disables `CopyGameAssemblies` and
`CopyEngineBinaries`; `dotnet publish` provides the self-contained executable
and required native dependencies. `CommonAssetsPackageMode: Full` packages the
complete engine asset library. Use `CommonAssetsPackageMode: RuntimeShaders`
for a procedural game that creates every other runtime asset itself; the
complete shader tree is retained so the render pipeline and shader includes
remain functional. Both modes create a non-empty `CommonAssets.pak`, and a
missing archive remains a hard startup error.

A publish removes absolute machine paths (for example a path in a user profile
folder, or an authoring path in an FBX or EXR file) from the files that it ships:
cooked assets, files that the content cook copies as they are, and every file of
`CommonAssets.pak`. It changes only the copy in the build output, never a source
file. It replaces the folder part of each path with underscores and keeps the
separators and the file name, so a model can still find its textures by file
name. It writes one warning that lists each file and each removed path. The same
list goes to `<project>/Intermediate/Build/Logs/publish-report.txt`, which is not
part of the build output. The publish fails when a path cannot be removed safely
or cannot be checked, for example a path inside a compressed PNG text chunk or a
ZIP or GZIP file, or a 7z file that the publish cannot open. The error names the
file. See [Cooked Assets, AOT, and IO](../architecture/assets/cooked-asset-aot-and-io.md).

Limits of the path removal:

- In a text file, a comma, a semicolon, or an equals sign in a folder name stops
  the removal early. The rest of the path stays, without its drive.
- In a binary file (images, audio, video, models), a drive path with fewer than
  two folder parts (for example `D:\Art\tex.png`) is not found. Paths in a user
  profile folder and paths under the asset folders are always found.
- In a binary file, a drive path that mixes forward slashes and backslashes (for
  example `C:\proj\assets/textures/wood.png`) is not found. A path in a user
  profile folder is still found.
- A ZIP file whose entries use LZMA, BZip2, or Zstandard compression, or
  encryption, cannot be checked, so the publish fails. Store the files unpacked,
  or use a ZIP file with normal (deflate) compression.
- The publish checks at most 2 GiB of inflated data for each compressed item and
  4 GiB for each file, and spends at most 2 minutes on an item and 10 minutes on
  a file. A file that goes past a limit fails the publish, and the error names
  the file and the limit.

The compiled game assembly must contain exactly one public, concrete `IGameLaunchBootstrap` with a public parameterless constructor. The generated launcher constructs it directly, uses it to configure startup, and obtains the initial `GameState`; this is the supported way to root custom worlds and components under NativeAOT.

## Sample Project MSBuild Targets

`Samples/RollingBall/RollingBall.csproj` exposes a `CookGameExe` target for sample automation.

NativeAOT published sample build:

```powershell
dotnet msbuild .\Samples\RollingBall\RollingBall.csproj /t:CookGameExe /p:Configuration="Published Release"
```

The `Published Release` configuration always uses NativeAOT. Do not override it with `/p:GamePublishNativeAot=false`: that build defines `XRE_PUBLISHED` without NativeAOT and fails. For a non-AOT sample build that reads the archives, use the [development launcher that reads archives](#development-launcher-that-reads-archives).

For Rolling Ball, prefer `Tools/Publish-RollingBall.ps1`; it runs the strict script path and creates the distributable ZIP. The direct MSBuild target remains useful for development diagnostics.

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

`Cooked asset type ... was published with generic 'BinaryV1'` (or `BinaryV2`).

The asset is being loaded by a published AOT runtime without an explicit runtime serializer. Register the type with `PublishedCookedAssetRegistry`, make sure it cooks as `RuntimeBinaryV1`, and republish content.

`No published cooked asset serializer is registered for ...`

The cooked blob says `RuntimeBinaryV1`, but the final runtime did not register a matching serializer. Ensure the registration assembly is referenced by the final launcher closure and that the registration runs before asset load.

`The launcher defines XRE_PUBLISHED, but PublishLauncherAsNativeAot is off.`

A non-AOT launcher cannot be a published build. Enable `PublishLauncherAsNativeAot` for a production build. For non-AOT testing of the archives, enable `DevelopmentLauncherReadsArchives` and remove `XRE_PUBLISHED` and `XRE_AOT_RUNTIME` from `LauncherDefineConstants` or `--define-constants`.

`Development archive build: asset '<path>' ...`

A development launcher that reads the archives could not load an asset from the archives, and it does not use the loose file instead. The message names the asset and the archive. Re-cook the content, or cook the missing asset into the archives.

AOT publish stops because the warning report is non-empty.

Open `Build/Reports/aot-final-game-publish-warnings.md`. Fix first-party runtime warnings, remove editor/dev authoring surfaces from the launcher closure, or replace the warning-producing dependency. Use `-AllowAotWarnings` only to create a local diagnostic artifact while doing that work.

## Useful Files

- `Tools/Publish-AotFinalGame.ps1`
- `XREngine.Data/Core/BuildSettings.cs`
- `XREngine.Editor/Program.cs`
- `XREngine.Editor/ProjectBuilder.cs`
- `XREngine.Editor/CodeManager.cs`
- `XREngine.Data/Runtime/XRRuntimeEnvironment.cs`
- `XREngine.Data/Serialization/AotRuntimeMetadata.cs`
- `XREngine.Data/Serialization/AotRuntimeMetadataStore.cs`
- `XREngine.Data/Core/Files/CookedAssetBlob.cs`
- `XREngine.Data/Core/Files/CookedAssetTypeReference.cs`
- `XREngine.Data/Core/Files/PublishedCookedAssetRegistry.cs`
- `XREngine.Runtime.Rendering/Core/Files/PublishedCookedAssetRegistryRegistration.cs`
- `docs/developer-guides/runtime/aot-final-game-builds.md`
- `docs/architecture/assets/cooked-asset-aot-and-io.md`
