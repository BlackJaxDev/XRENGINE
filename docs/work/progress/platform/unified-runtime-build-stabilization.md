# Unified Runtime Build Stabilization

Status: the build gate passes locally. Runtime, browser, and test-execution qualification remain open.

Tracks: [unified runtime UR00](../../todo/platform/unified-desktop-browser-runtime-todo.md#ur00--stabilize-the-branch-as-a-reference-harness) and the build items of the [native subsystem integration checklist](../../todo/platform/native-subsystem-project-split-todo.md#build-dependency-and-publish-boundaries).

## Starting state

The first build attempt after the organizational refactor stopped at `XREngine.Extensions`, the lowest shared project. The portable source guard is compiled as an inline MSBuild task against netstandard2.0, and it used `string.Contains(string, StringComparison)` and `string.Replace(string, string, StringComparison)`, which that API surface lacks. Every project in the portable closure runs the guard, so no application could build. None of the source changes made since the refactor had been compiled.

## Toolchain

| Component | Version |
| --- | --- |
| .NET SDK | 10.0.401 (no `global.json` pin; decision D5) |
| `wasm-tools` workload | manifest 10.0.112/10.0.100, workload set 10.0.401.1 |
| WebAssembly SDK pack and browser runtime pack | 10.0.12 |
| Emscripten packs | 3.1.56 (matches the [Jolt browser supply pin](../../design/platform/jolt-browser-native-supply.md)) |
| PowerShell | 7.6.6 installed; Windows PowerShell 5.1 remains available |

Installing `wasm-tools` also updated the machine's other installed workloads (android, ios, maccatalyst, maui-windows) to the current manifest set, because workload installs move every installed workload together.

Run repository PowerShell tools with PowerShell 7. Under Windows PowerShell 5.1, `Tools/Reports/Generate-Dependencies.ps1` rewrote every license file with a byte-order mark and mis-decoded non-ASCII text; those rewrites were discarded.

## Results (2026-09-30)

| Check | Result |
| --- | --- |
| Shared closure, desktop (`XREngine.Runtime.Rendering.WebGPU` and its references) | Builds, 0 warnings |
| `XREngine.Editor` (clean rebuild) | Builds, 0 warnings |
| `XREngine.Server`, `XREngine.VRClient` | Build, 0 warnings |
| `XREngine.UnitTests` | Builds, 0 warnings |
| `XRENGINE.slnx` | Builds, 0 warnings |
| `Tools/Test-PortableBrowserCompile.ps1 -Configuration Release` | All 14 portable projects compile for `browser-wasm` |
| `dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -m:1` | Succeeds. `_framework` holds 200 files: 43.9 MiB uncompressed, 13.8 MiB Brotli, 17.4 MiB gzip, untrimmed interpreter |
| Dependency inventory | Regenerated with PowerShell 7; only the three moved package rows changed |

Not established by these results:

- No application, editor session, or browser page was launched.
- The Linux CI compile lane was not run in this original pass. The later focused Linux validation below normalizes Core casing; the full CI lane remains unqualified.
- Unit tests were not run as a suite. A filtered run of boundary and source-contract tests had 61 failures of 233, and every failure predates this work: source-text contracts that still name pre-refactor paths or moved types, file lookups made ambiguous by moved or duplicated names, native Vulkan tests that need a device, and boundary tests that flag existing references (Server and VRClient reference `XREngine.Runtime.ModelAssetPipeline`; Bootstrap's generator input globs it). The one contract broken by this work, `CollectibleBackends_DoNotCreateUnmanagedDelegateThunks`, was updated and passes. Triage belongs to the native subsystem integration checklist.

## Fixes

### Portability guard and policy

- The guard uses netstandard2.0 string APIs and skips the SDK-injected `Microsoft.NET.ILLink.Tasks` package that `IsTrimmable`/`IsAotCompatible` projects receive.
- Deny rules were narrowed to real API use, because deny matches cannot be allowlisted:
  - `OpenVR.NET` and `Valve.VR` instead of any `OpenVR` identifier, which matched engine enum members and the engine's own `XREngine.Input.Devices.Types.OpenVR` namespace;
  - Win32 registry roots and `RegistryKey` instead of any `Registry.`, which matched engine registry properties;
  - `Socket` type use, excluding member access and enum member declarations such as the physics-chain `Socket` element.
- Reviewed reflection allowances were added for `XRAssetGraphUtility`.
- Portable projects built for `browser-wasm` default to `PublishTrimmed=false`. The SDK otherwise defaults browser builds to trimmed publishing, which the guard rejects until trimming is reviewed.
- The Rendering render-command generator runs under Windows PowerShell on Windows, as the other desktop build steps do, and under `pwsh` elsewhere. Builds no longer require PowerShell 7 on Windows.

### Code moved out of portable projects

- Rendering held native-callable entry points (`[UnmanagedCallersOnly]` methods and unmanaged function pointers) for Streamline logging, Vulkan debug messages, the ImGui clipboard, and ImGui viewports. Rendering now keeps only the managed registrations and dispatch. The entry points live in `XREngine.Runtime.Platform.Desktop/Rendering/`, which is not collectible, and `DesktopPlatformBackend.Register` installs them through `IRendererNativeCallbackEntryPoints` and `IRendererImGuiViewportEntryPoints`. A host that creates a native renderer without installing them gets a named error.
- `Debug` names the log session from `Environment.ProcessPath` instead of `Process.GetCurrentProcess()`.
- `XROVRCameraParameters` reads the OpenVR eye projection through Rendering's `IRuntimeOpenVrStateProvider`, because Rendering may not reference `XREngine.Input`.
- `HumanoidComponent` reads the imported model's units-per-meter through the new Core holder `SceneNodeImportUnits`. The model import pipeline records it alongside its producer report, so the portable animation integration no longer needs the authoring-only pipeline.

### Refactor fallout

- Removed packages were still used through transitive references:
  - `Microsoft.DotNet.PlatformAbstractions` in the asset manager, now `AppContext.BaseDirectory`;
  - MathNet helpers in IK solvers and the mirror component, now the engine's `EqualTo` and `XRMath.NextPowerOfTwo`;
  - Silk SDL windowing and input, now direct references of `XREngine.Runtime.Platform.Desktop`;
  - `System.Security.Cryptography.ProtectedData`, now a direct reference of `XREngine.ControlPlane.Service`, at the version the editor already uses.
- Code still reached members that moved or changed:
  - the removed `XRWindow.Window` (now `WindowTitle` or `DesktopWindowBackend`);
  - datagram `SendAsync` arity;
  - the OpenXR graphics-host argument (bindings receive `GraphicsBindingHost`, not the API object);
  - OpenXR result codes that are now integers in the neutral exception;
  - primary-constructor parameters used from a nested class;
  - `PfnVoidFunction` construction.
- Imports were missing in the Rendering, Vulkan, OpenXR, Bootstrap, editor, and browser host projects.
- `WebGpuRendererHost` implements the desktop half of `IRuntimeRendererHost` and reports no indirect-count draw, no mesh shaders, and no meshlet dispatch.
- The OpenXR module exposes internals to the unit tests, and `IOpenXrRuntime` carries the smoke diagnostics that the editor's smoke controller reads.
- Nullable-flow annotations were added to OpenVR and OpenXR try-pattern methods.
- The OpenGL ImGui font texture uses the value overload of `TexParameter`.
- The server rebuilds its headless Bootstrap restore when Bootstrap's project file is newer than the isolated assets file. The stale file had compiled the server against a pre-refactor reference set.
- Tests and benchmarks follow the changed APIs, with no assertion changes other than the entry-point relocation above:
  - OpenXR swapchain generation fields;
  - neutral OpenXR policy enums;
  - `RuntimeVrDeviceInfo`.

  The pre-created shared-GL-context path takes a window backend again, with an optional owner window as on master. `SilkSharedWindowTestBackend` presents a hidden test window as that backend and is linked into the benchmarks.
- The generated browser registration file declares its nullable context. The generator emits the same directive.

## Open

- UR00.03 and UR00.04: run the published site and the editor's browser build target, and record captures.
- Run the full Linux compile lane after the Core casing normalization recorded below.
- Decision D5: pin the SDK and workload.
- Native subsystem checklist: execute and triage the unit test suite, inspect publish layouts, and run the live smokes.

## Focused Linux validation (2026-09-30)

The Core directory is now normalized in the git index to `XREngine.Runtime.Core/`: 244 tracked entries (243 C# files and the existing CoACD binary) move without content changes. The CoACD build script uses the same canonical path, so it does not recreate the differently cased directory. Existing project references and reflection allowlists already use this spelling and need no exceptions. This preserves the effective layout on case-insensitive Windows checkouts while making the same source set visible on Linux.

The first-class `XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj` compiles its complete production dependency projects on Linux with .NET SDK 10.0.401 and PowerShell 7.6.6, with zero warnings or errors. It links the existing calibration/solver tests unchanged. Six baseline/control tests pass; the explicitly selected persistence contract fails with cleared targets and duplicate calibration nodes, confirming the known calibration defect. See the [calibration rerun](../../investigations/avatar/vr-calibration-baseline-2026-09-24.md#linux-headless-rerun-2026-09-30).

This focused local result does not qualify the complete Linux CI/browser publish lane, the full desktop test suite, native rendering, or a headset. Those checks remain separate.

The subsequent six-slot calibration ownership repair promotes the persistence contract into the default headless suite. The updated focused build remains warning-free and all 16 headless tests pass. The earlier six-pass/one-known-failure result above records the pre-repair reproduction, not the current suite outcome.
