# Build and publish a browser game

Updated: 2026-10-09

Use this guide to publish a project as a static BrowserWebGPU site. The Editor builds the project code, checks the saved startup world, cooks its required assets, and writes a complete WebAssembly site.

The current browser profile uses untrimmed, interpreted .NET 10 WebAssembly with native Jolt and WebGPU. It does not use WebAssembly threads. See [Browser static hosting](../developer-guides/runtime/browser-static-hosting.md) for the deployment requirements for this profile.

## Before you publish

Use an XRENGINE source checkout. The standalone Editor package does not yet include its normal `Build/CommonAssets` tree, so it cannot publish a project by itself. The repository pins the .NET SDK to 10.0.401 in `global.json`. Browser restore and publish also need the pinned `wasm-tools` workload and Jolt source and archive inputs. Use the `Prepare-JoltBrowserManaged.ps1` and `Build-JoltBrowser.ps1` steps and exact pins in the [browser publishing record](../work/progress/rendering/browser-project-publishing.md); do not substitute tool or native-library versions. That record's final `dotnet publish` command prepares an Editor package. It is not the project build command below.

Save one startup world as a `.asset` file inside the project's `Assets` folder. Select that same world for each startup window in the saved `Assets/startup.asset` settings. If the saved settings have no startup windows, set the project `StartupScenePath` to the saved asset. Browser publishing does not support `RunWithoutWindows`, multiple different startup worlds, or a startup world outside `Assets`.

The Editor and CLI use the same browser builder, but they prepare its settings differently. Both paths require `CleanOutputDirectory=true`, `BuildManagedAssemblies=true`, `RendererBackendPackage=All`, and the saved startup world described above.

In the Editor, open `Settings > Game Project > Build Settings`. The `File > Build Project` command uses the current Build Settings values. Set `Platform=BrowserWebGPU`, `Configuration=Release`, and `OutputSubfolder=Browser` or choose your own values. Also set `CookContent=true`, `BuildLauncherExecutable=true`, `LauncherDefineConstants` to empty, and both `PublishLauncherAsNativeAot` and `ValidateLauncherAotCompatibility` to false. Choose `Save Build Settings` to save the values to `Config/build_settings.asset`. The default configuration is `Development`, and the default output folder is `Game`. This menu route is present in source but has not been validated in a live Editor session.

The CLI command below sets the platform, configuration, and output folder. It also sets `CookContent=true`, `BuildLauncherExecutable=true`, and an empty `LauncherDefineConstants`. Its BrowserWebGPU platform override clears inherited NativeAOT and AOT-validation flags by default. If you pass either AOT option explicitly, a true value is rejected for this target. The override also sets `SaveSettingsBeforeBuild` to false, so the temporary platform, configuration, and output-folder values are not saved to the authored project settings. The saved `CleanOutputDirectory`, `BuildManagedAssemblies`, and `RendererBackendPackage` values must still meet the browser build checks.

## Publish from the source checkout

Run this command from the XRENGINE source root. Replace the example project file with your `.xrproj` file:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- `
  --build-project ".\Path\To\Game.xrproj" `
  --build-platform BrowserWebGPU `
  --build-configuration Release `
  --output-subfolder Browser
```

The Editor prints `Browser static bundle ready: ...` when the build finishes and the output contains `index.html`. With the standard project layout, the output is `<project-root>/Build/Browser`. The browser CLI requires `--build-project`; the managed-only `--build-project-code` command does not publish the browser application.

The output is a complete static site. It includes the player page and JavaScript, WebAssembly runtime files, `browser-publish.json`, `content/manifest.json`, content payloads, and any required font notices. Keep the directory layout intact.

The publisher generates a small `XREngine.BrowserSite` executable in the project's `Intermediate/BrowserPublishing/Launcher` directory. `XREngine.Browser` is a reusable library. The launcher calls `BrowserRuntime.Initialize` once and supplies typed game bootstrap and registration delegates. Game registration still runs after engine asset services are ready. A second initialization fails; after a failed initialization, reload the page.

A binary browser SDK supplies `BrowserPublishing/browser-host.json`, verified engine DLLs in `lib`, shared host targets, assets, and the portable/native build policy. With this SDK, publishing compiles the game and generated launcher without compiling engine C# sources. Native WebAssembly linking and project asset cooking still run. Source-checkout publishing references the browser library project instead. JavaScript loads exports from `XREngine.Browser` and runs the generated executable's entry point.

For standalone diagnostics, publish `XREngine.Browser.Standalone/XREngine.Browser.Standalone.csproj` with the same browser workload and native inputs. Do not publish the browser library as an executable.

The publisher builds the site in a sibling staging directory. It checks the entry page, launch descriptor, manifest, and required notices before it activates the new site. It then replaces the whole output directory. If the build fails before activation, the previous site stays in place. If activation fails after it moves the previous site, the publisher tries to restore it. Keep files that you need outside `Build/Browser`; the next build replaces that complete directory.

## Test and host the site

Serve the complete output over HTTPS, or use HTTP on `localhost` for local development. Do not open `index.html` as a `file://` URL. The site needs its original relative paths, correct MIME types, and the cache and security headers described in [Browser static hosting](../developer-guides/runtime/browser-static-hosting.md). That guide covers the full deployment checklist. It does not certify a hosting provider or browser/device combination.

The [browser UI font guide](../developer-guides/runtime/browser-ui-fonts.md) covers font source and notice requirements. Its cooking support does not mean that every authored screen or UI interaction has passed browser runtime checks.

## Current browser qualification

This is a dated summary of bounded evidence. It is not a general browser or device support promise.

| Area | Evidence as of 2026-10-07 | Limit |
| --- | --- | --- |
| CI run | The completed [run 37546153259](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37546153259) on [commit 37db74dd](https://github.com/BlackJaxDev/XRENGINE/commit/37db74ddb1524b31f4536b0d95556919d88fa689) had seven passing checks and two failures. | These results do not establish full gameplay or desktop parity. |
| Shadow comparison | Three small shadow-off captures passed. | The first shadow-on/no-decals startup reached its 45-second limit while native `constref` shader creation had been pending for 37.30 seconds. The off-only captures do not qualify directional or point shadows. The full-size shadow run was not performed. |
| Hardware and browsers | The cited results are from automated CI. | There is no fresh physical-PC matrix for this revision. Do not infer Safari, RTX, broad hardware, numeric MSAA/deformation parity, or performance support. |

This run does not establish a performance result or full desktop parity. Check the [active browser runtime record](../work/todo/platform/unified-desktop-browser-runtime-todo.md) and [shadow investigation](../work/investigations/platform/browser-shadow-capture-timeout-2026-10-06.md) for later evidence and open work.

A successful publish means that the authored startup world passed the publisher's current checks. It does not promise that every world, shader, material, component, effect, or target device will run. The publisher rejects unsupported startup requirements instead of silently dropping them. Read the named error, correct the asset or setting that it identifies, then publish again.

## Troubleshooting

- If the build says `Every browser startup window must select a saved target world`, save the startup settings and assign every startup window the same saved world.
- If it says `Select and save a startup window target world or project StartupScenePath before browser publishing.`, choose one saved `.asset` file under `Assets` and save the startup settings or project descriptor.
- If a `BrowserCook...` diagnostic names an asset, setting, or shader, fix that named requirement. The publisher does not convert arbitrary desktop shaders or ignore unsupported assets.
- If the page loads without the game, check the browser console and network panel. Confirm that the host serves the complete output, returns `application/wasm` for `.wasm`, and returns a missing-file error for missing JavaScript, manifests, and content payloads. See the [hosting guide](../developer-guides/runtime/browser-static-hosting.md).

For desktop cooked builds, use [Finalized game builds and asset cooking](finalized-game-builds.md).
