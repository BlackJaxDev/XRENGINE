# Portable Browser Engine Boot Qualification

[Work index](../../README.md) · [Active runtime checklist](../../todo/platform/unified-desktop-browser-runtime-todo.md) · [Portable host ownership](portable-engine-host-ownership.md)

Status: preparation and partial interpreter qualification only. Implementation paused at the owner's request on 2026-09-30. The browser application still uses the frozen reference scene host; real `RuntimeWorld` startup is not implemented or qualified.

## Compiled ownership

Browser references the whole portable Host, Core, Rendering, WebGPU and Animation assemblies. Host supplies the transitive Audio/Input contracts and animation/audio/input integration adapters. The evaluated Browser closure has 13 projects; the reviewed portable compile manifest has 15, including Modeling and ModelingIntegration outside that application closure. The final Host gate compiles all 15 for `browser-wasm` and publishes the untrimmed interpreter with zero warnings/errors.

Host's generated module initializer supplies the shared factories and Rendering owns render-command registration. Complete browser component/serializer/module registration and an audit of real engine startup are still required. `Program.Main` currently initializes `BrowserStaticRegistrations` and `BrowserRendererComposition`, rather than real world/host composition.

## Live asset qualification

A disposable WebAssembly application references the full portable Host/Rendering closure, installs Data/Core/Rendering serialization contributions, and fetches the existing desktop-authored `fixture-world.asset`. It runs in Development interpreter mode. Its explicit inline-only asset service names unsupported external references; no dummy asset or alternate browser world format replaces them.

The source is under `Build/_AgentValidation/20260930-105523-unified-browser-runtime/scratch/BrowserAssetQualification/`; the refreshed publish is `temp-build/browser-asset-qualification-publish/wwwroot/`. Its second publish passes with zero warnings/errors, recorded in `logs/portable-browser-asset-qualification-publish-2.log`. The local browser page fetched 6,481 characters and reported:

| Check | Live result |
| --- | --- |
| Authored `XRWorld` YAML load | Failed on external shader `Shaders/Common/UnlitColoredForward.fs`; named unresolved TextFile diagnostic with expected `XRShader` type and source mark 38:25 |
| `XRWorld` YAML round-trip | Not qualified because world load failed |
| `XRPrefabSource` and camera component YAML round-trip | Passed; concrete camera type, component/node IDs and owning node preserved |
| Standalone camera `SceneNode` YAML round-trip | Passed within the prefab/component check |
| `XRWorld` cooked binary and MemoryPack envelope | Not qualified because world load failed |
| `XRPrefabSource` cooked binary and MemoryPack envelope | Passed; restored camera retains its owning node |

The binary route is the existing `CookedBinarySerializer` payload inside a MemoryPack `CookedAssetBlob`, loaded through `CookedAssetReader`. This is not a claim that `XRWorld` has a generated MemoryPack formatter or that Published mode works. Published mode rejects YAML and requires metadata before world/transform static initialization. External asset resolution, cooked published metadata, real startup/game-mode/pawn lifecycle, frame stepping and renderer execution remain open.

## Native prerequisite and next boundary

The approved Jolt archives build, but the managed static-link signature conflict blocks the browser physics prerequisite. The [native supply record](../../design/platform/jolt-browser-native-supply.md#managed-linkage-findings) distinguishes the fixed module-name admission from the unresolved binding conflict. No browser stepping/callback/teardown proof or default promotion is claimed.

Resume with the managed binding supply decision and native spike qualification, then implement the real browser composition/asset reference owner and complete representative world/prefab/component round-trips. Boot a fetched cooked `XRWorld` through the shared world host only after those prerequisites pass. Frame/scheduling and asset work precede the engine WebGPU backend.
