# AOT Final Game Builds

NativeAOT support is scoped to cooked final game launchers. The editor, hot-reload, runtime C# plugin loading, and authoring-time YAML workflows remain CoreCLR development surfaces.

WMI hardware inventory is unavailable in published players and returns an explicit
capability diagnostic. Desktop compression registration remains available; device
inventory must not root unsupported COM interop in the NativeAOT player.

Desktop launchers compose shared `net10.0` projects with explicit backend modules through Bootstrap; see [Runtime Project Organization](../../architecture/runtime/project-organization.md). Host generates the portable application factories, Bootstrap generates factories for its desktop inputs, and Rendering generates its built-in render-command registry through the [semantic factory generator](generated-runtime-factories.md). Moved namespaces/type names still need cooked metadata and factories in the launcher closure. Browser compilation has separate qualification and does not certify this publish path.

## Canonical Validation

Use the validation script from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Publish-RollingBall.ps1
```

The matching VS Code task is `Publish-RollingBall-NativeAOT-Package`. This
creates the validated `Samples/RollingBall/Build/Packages/RollingBall-win-x64.zip`
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

## Game Runtime Smoke

A game bootstrap can also implement `IGameLaunchRuntimeSmokeBootstrap`. With
`--aot-smoke`, the generated launcher then calls `ConfigureRuntimeSmoke()`
before `ConfigureStartup`, loads the archives, runs `Engine.Run` with the real
game state, and calls `CompleteRuntimeSmoke()` after the engine loop exits. On
success, the launcher prints `AOT runtime smoke passed.`. A bootstrap without
the interface returns after the archive smoke.

MonkeyBall VR implements this gate in `MonkeyBallRuntimeValidation`. The gate
observes the cooked runtime state and does not substitute simulation or
rendering. It sets a nonzero exit code unless all of these become valid within
at least 300 normal ticks:

- component activation, begin play, pre-physics ticks, and normal ticks
- local-player possession and keyboard, gamepad, and VR input registration
- native ball and course rigid bodies in an advancing physics scene at the
  authored fixed cadence, with ball interpolation set to `Interpolate`
- a scripted tilt that changes the native course target and the ball state, with
  the pivot at the ball position
- camera follow, upright orientation, and yaw toward velocity, in both the
  scene and the published render pose
- a standalone (non-atlased, non-cascaded) directional shadow map that renders

Opt-in diagnostics for an interactive packaged run:

| Variable | Effect |
|---|---|
| `XRE_MONKEYBALL_DIAGNOSTICS_PATH` | Enables `MonkeyBallRuntimeDiagnostics` and writes lifecycle, input, physics, camera, and shadow counters to this file. |
| `XRE_MONKEYBALL_DIAGNOSTICS_VALIDATE` | Runs the same acceptance gate without `--aot-smoke`. |

MonkeyBall rules that the gate enforces:

- `MonkeyBallWorld.asset` is the only world source. There is no hardcoded
  runtime world.
- A missing native course body is an error. There is no transform-only physics
  fallback.
- The sun is authored and cooked with `CastsShadows = true`,
  `UseShadowAtlas = false`, and `EnableCascadedShadows = false`.
- The desktop camera is not a child of the ball transform. Camera follow runs in
  late update from the presented ball pose.

Manual release sign-off uses the
[MonkeyBall VR Release Matrix](../../work/testing/xr/monkeyball-vr-release-matrix.md).

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

Published AOT launchers reject generic `BinaryV1` and `BinaryV2` cooked assets at runtime. Runtime-loadable assets must be registered with `PublishedCookedAssetRegistry` so they cook as `RuntimeBinaryV1`, and any runtime type lookup must resolve through `AotRuntimeMetadata.bin` or an explicit generated registry.

## Development Parity Diagnostics

Editor play mode runs on CoreCLR, where reflective fallbacks can resolve a type,
construct an object, or bind an animated member that NativeAOT publication
cannot. `XRE_AOT_PARITY` makes those fallbacks visible before a publish:

| Value | Behavior |
|---|---|
| `off` | Fallbacks run silently. Default for interactive editor sessions. |
| `warn` | Each distinct type and category is logged once with the call site and the missing registration. |
| `error` | Every fallback throws `AotParityViolationException`; inventory entries remain deduplicated. Default for the unit-test lane (`XRE_WORLD_MODE=UnitTesting`) and headless validation (`XR_HEADLESS_TEST`). |

Published builds ignore the variable because their reflective fallbacks are
already unavailable. Diagnostics follow explicit call-chain scopes, not a process-wide play flag; editor authoring on another call chain stays outside parity. Synchronous tick/component scopes allocate no execution context. Diagnostics fire only on the player path: published
content loads, world begin-play through end-play (including component
construction during play), and cooked content deserialization. Editor import,
inspector editing, YAML editing, and cooking stay outside player scopes. The
editor's synchronous play-transition snapshot codec is also an authoring operation:
it temporarily suspends the inherited player scope and restores it on exit,
including exceptions. Explicit player callbacks nested inside that operation
reactivate diagnostics; snapshot reference repair and game-mode lifecycle callbacks
remain checked. This boundary does not exempt packaged content loading.

Each diagnostic names the type, one of five categories (type-resolution scan,
reflective cooked deserialization, reflective factory, reflective member
binding, polymorphic YAML scan), the owning call site, and the registration or
generated contract that removes the fallback. Fix gaps by adding the
registration; do not add an allowlist.

`Tools/Run-AotParitySmoke.ps1` starts an isolated MCP editor session with
`XRE_AOT_PARITY=error`, loads the Rolling Ball world, enters play mode, exits,
and archives the session log with a summary of any parity lines.
Use `-PlayCycles 3 -PlaySeconds 15` to exercise repeated enter/exit transitions
in the same editor process. The summary records both requested and completed
cycles; incomplete transitions fail the smoke.
It waits for an active world after the MCP endpoint becomes ready, since the
server can accept requests before editor startup completes. Only logs written
during the current invocation are archived and checked; reusing a named session
does not import a previous run's diagnostics. A missing startup backend remains
a failed smoke and must be resolved or explicitly configured for the validation
profile before retrying.

## Cooked Archive And Envelope Format

Published content uses archive format version 5 and cooked envelope version 2.
Content cooked by an earlier editor build is rejected at load with a diagnostic
that names the archive or asset and says to re-cook; there is no compatibility
path. Re-run the publish script to regenerate archives.

The published runtime opens each archive once through `PublishedArchiveRegistry`,
reads entries as `CookedPayloadLease` values, and deserializes them with
`PublishedCookedAssetReader`. Game code that registers a cooked asset type uses
the span-based delegates:

```csharp
PublishedCookedAssetRegistry.Register(
    typeof(MyWorldAsset),
    static (asset, writer) => MyCodec.Serialize((MyWorldAsset)asset, writer),
    static (payload, _) => MyCodec.Deserialize(payload));
```

The serializer writes to an `IBufferWriter<byte>` and the deserializer receives a
`ReadOnlySpan<byte>` that is valid only for the duration of the call. See
[Cooked Asset Serialization](../../architecture/assets/cooked-asset-aot-and-io.md#7-archive-handles-payload-leases-and-the-envelope).
