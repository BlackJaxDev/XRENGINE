# Portable Browser Engine Boot Qualification

[Work index](../../README.md) · [Active runtime checklist](../../todo/platform/unified-desktop-browser-runtime-todo.md) · [Portable host ownership](portable-engine-host-ownership.md)

Status (2026-10-01): the browser source now has a shared-engine composition path, but its live qualification remains incomplete. `BrowserEngineExports.StartAsync` loads a hash-verified content catalog, deserializes a real `XRWorld`, and starts it through `BrowserEngineSession`, `RuntimeWorld`, and the shared play host; the session exposes caller-thread frame stepping and input/audio services. The production player explicitly reports that rendering is unavailable, and no live browser-world start/stop result is recorded. See the [unified browser checkpoint](unified-browser-checkpoint-2026-10-01.md) and [active TODO](../../todo/platform/unified-desktop-browser-runtime-todo.md).

## Compiled ownership

`XREngine.Browser` references portable Host, Core, Rendering, WebGPU, Animation, WebAudio, Jolt and WebSocket projects. The reviewed portable compile manifest has 18 projects, including projects outside the application closure. Browser static registrations, asset services, game bootstrap, audio, and Jolt factory are installed by the browser composition. On 2026-10-01, the supported in-process MSBuild task-host override compiled all 18 portable-project rows and a fresh current-tree browser interpreter/Jolt publish exited 0. There were no compiler warnings/errors; the compile sweep retained only NU1900 cached-audit warnings from the read-only home. These are current compile/publish results, not live browser execution or game-render evidence. The fresh untrimmed static bundle is about 89 MiB and is not a network-download or performance measurement.

Host's generated module initializer supplies shared factories and Rendering owns render-command registration. The browser page installs generated registrations and a typed game bootstrap before loading the startup world. Remaining gaps include full browser-reachable startup/metadata qualification, complete world/prefab/component asset acceptance, and integration of the renderer with the production player. `Program.Main` installs static registrations, WebGPU composition, the Jolt scene factory, and WebAudio; the page-facing engine exports perform the later world composition.

The production session sets `AllowsRendererBackends: false` and the player reports `Rendering is unavailable in this build`. The separate engine-mesh diagnostic uses engine objects but does not qualify the production render pipeline. Browser surface-generation/resize behavior is not connected to the production world host.

## Historical live asset qualification (2026-09-30)

The checks below describe the earlier disposable asset-qualification page and remain useful boundary evidence. They are not a live test of the resumed `BrowserEngineExports` path.

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

The binary route is the existing `CookedBinarySerializer` payload inside a MemoryPack `CookedAssetBlob`, loaded through `CookedAssetReader`. This is not a claim that `XRWorld` has a generated MemoryPack formatter or that Published mode works. Published mode rejects YAML and requires metadata before world/transform static initialization. The resumed source adds a catalog-backed asset path and real `RuntimeWorld` composition, but those paths have not yet passed a live browser startup, full world/prefab/component round-trip, or rendered-world check.

## Native Jolt status and next boundary

Decision D14 approved the browser-only managed Jolt source supply that corrects the conflicting `JPH_ContactListener_SetProcs` overload; the source/license review and exact native/managed spike publish passed on 2026-10-01. The browser Jolt leaf, pinned archive build, and native-asset portability allowance are implemented. The remaining gate is live browser initialization, stepping/callbacks, teardown, and tolerance-based parity. Desktop physics defaults remain unchanged until parity is established. See the [native supply record](../../design/platform/jolt-browser-native-supply.md#managed-linkage-findings) and [approved decisions](../../todo/platform/unified-desktop-browser-runtime-todo.md#owner-decisions).

Next: complete the current browser build/publish gate, run the production page through actual engine-world start/stop and caller-thread frames, then integrate the engine surface and WebGPU renderer. Local Playwright/Chromium execution is blocked by the executor's Unix-socket policy; the approved CI workflow is the fallback, but no current CI smoke result is recorded. The renderer's current WebGPU default-pipeline guard, shader/material limitations, audio/input gaps, and all live boundaries are summarized in the [2026-10-01 checkpoint](unified-browser-checkpoint-2026-10-01.md).
