# Browser tool host startup

Date: 2026-10-29
Status: Startup fix and source-checkout browser publication validated.

## Failure

The Release `BrowserRuntimeMetadataCooker` aborts when it cooks
`Tools/BrowserSmoke/Content/EngineSmokeWorld.asset` in a fresh process.
`RuntimeAssetBootstrap.InstallEngineAssetServices` initializes `Engine.Assets`.
The `AssetManager` constructor requests its host metadata file provider before
one is installed. The exception is `Asset file discovery is not installed`.
The process exits with code 134 before it writes a cooked world.

The publisher driver installs `LocalAssetFileSystem` after the same bootstrap
call. Both entry points must install host capabilities before engine services.
This is a tool composition failure. The shared runtime must retain its explicit
provider requirement. Adding a hidden desktop fallback would hide the error.

## Scope

The two tools now share the existing portable native file provider through
`Tools/BrowserToolHost/BrowserToolHost.props`. No Windows-targeted desktop
assembly enters their project graph or the browser runtime closure. Both
install host providers before asset registration. Metadata-cooker host state
uses a sibling directory, outside the fixture or site being checked. See
[native tool host services](../../../../Tools/BrowserToolHost/README.md).

## Runtime results

- Release and fresh Debug builds complete with zero warnings and errors.
- Fresh cook and verify processes pass in both configurations. The fixture
  contains the real binary `XRWorld` and its AOT metadata. Verification leaves
  both input files unchanged.
- Fresh closure scans pass in both configurations: 17 engine assemblies,
  8,473 types, 42 transforms, 15 redirects, and 12 registered asset types.
  Hashes for all 981 site files remain unchanged after the two scans.
- The native publisher driver invokes the compiled Editor's real synchronous
  build path from the source checkout. It completes portable game compilation,
  authored world cooking, WebAssembly publishing, content packaging, launch
  configuration, and output activation. The result has 83 assets, 40 shaders,
  and 9 pipeline passes. Its source and copied-input guards confirm that all
  187 canonical RollingBall input files remain unchanged.
- The existing software browser harness passes all 13 selected engine checks.
  Its separate game-only run passes both published-game and local-delivery
  checks. Two fresh shipping-player contexts start RollingBall with Jolt
  physics. Tilt, reset, pause/resume HUD changes, and resize checks pass.
  The rendered game canvas was viewed. See the [platform validation
  record](../../testing/platform/platform-validation.md#native-tool-and-software-browser-follow-up--2026-10-29)
  for fixture coverage and software-device limits.

The original failure was a provider-order error, not a missing file or corrupt
world. No assertion was disabled. No new unit tests were added during live
regression diagnosis.

## Limits

The source-checkout publisher result does not qualify the packaged Editor CLI
or the Editor UI. The frozen browser reference runtime is unchanged. The other
executor's manifest-validator work is separate. Sky projection pixels,
procedural sky controls, and hardware GPU acceptance remain open.
