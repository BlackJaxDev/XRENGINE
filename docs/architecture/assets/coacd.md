# CoACD Integration

[← Docs index](../README.md)

CoACD is an editor/cook-time collider authoring dependency. The native call and convex-hull disk cache live in `XREngine.Runtime.Physics.Authoring`; the authored parameter/result contract remains in `XREngine.Data.Tools.CoACD`, and runtime physics components call the neutral `IPhysicsColliderAuthoringService` in `XREngine.Runtime.Core`. The editor installs the authoring service and PhysX installs `IPhysicsConvexHullInstaller` for cooked shapes. Server and game distributions should use cooked collider geometry; an unregistered authoring service reports a named missing-service diagnostic.

CoACD does not currently publish a NuGet package. The existing native supply path remains `XREngine.Runtime.Core/runtimes/<rid>/native` so the build and wheel scripts keep working; the Authoring project links the binary from that location into its own runtime output. A fallback script extracts the vendor-provided wheel.

## Building from source (default path)

`XREngine.Runtime.Physics.Authoring/XREngine.Runtime.Physics.Authoring.csproj` invokes `Tools/Dependencies/Build-CoACD.ps1` before its managed build whenever the selected binary is missing (or when you pass `/p:ForceCoACDBuild=true`). The script:

- clones or updates `https://github.com/SarahWeiii/CoACD.git` under `Build/Submodules/CoACD`
- configures CMake with the same flags as the official wheels (`/MT`, OpenVDB static, `_coacd` target)
- builds the selected RID
- copies `lib_coacd.*` into `XREngine.Runtime.Core/runtimes/<rid>/native`
- records the commit hash in `Build/Dependencies/CoACD/build-info-<rid>.json`

Manual invocation mirrors what the MSBuild target runs:

```powershell
pwsh Tools/Dependencies/Build-CoACD.ps1 -Rid win-x64 -Ref 1.0.7 -Configuration Release
```

- Use `-ForceBuild` to rebuild even if the current binary matches the requested ref/commit.
- Use `/p:CoACDRef=<tag-or-branch>` or `/p:CoACDRid=<rid>` when calling `dotnet build` to override the defaults.

## Downloading the Python wheel (fallback)

If you cannot build the native project locally, use the helper to extract the prebuilt binary that ships with each wheel:

```powershell
pwsh Tools/Dependencies/Get-CoACD.ps1 -Version 1.0.7 -Rid win-x64
```

Supported `-Rid` values:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each invocation downloads `coacd-<version>-cp39-abi3-<suffix>.whl` directly from the [1.0.7 release](https://github.com/SarahWeiii/CoACD/releases/tag/1.0.7), expands it under `Build/Dependencies/CoACD`, and copies the `lib_coacd.*` file into `XREngine.Runtime.Core/runtimes/<rid>/native`.

## Build output

The Authoring project links `lib_coacd.dll` as a native asset for desktop authoring output. Building the lower Core project alone does not install or distribute CoACD. Repeat native preparation for other RIDs when an authoring host targets them.

## Runtime concurrency

Managed CoACD requests are globally throttled before entering the native library. The default is one native CoACD run at a time because the CoACD binary uses OpenMP internally, and each run may already spread work across CPU cores. This prevents bulk collider generation from starving unrelated .NET thread-pool work such as render debug-shape population.

For local experiments, override the limit before launching the process:

```powershell
$env:XRE_COACD_MAX_CONCURRENT_RUNS = "2"
```

The value is clamped to `1..Environment.ProcessorCount` and is read once when the managed CoACD wrapper is first used.

## Updating versions

1. Update the default `CoACDRef` property in `XREngine.Runtime.Physics.Authoring/XREngine.Runtime.Physics.Authoring.csproj` (or pass `/p:CoACDRef=<new-tag>` when building).
2. Re-run the build script (or the wheel extractor) for each RID you ship.
3. If the upstream project renames the native binary, update `XREngine.Runtime.Physics.Authoring/CoAcdNativeBackend.cs`, its project content item, `Build-CoACD.ps1`, and `Get-CoACD.ps1` to match.
