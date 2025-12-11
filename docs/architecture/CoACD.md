# CoACD Integration

[← Docs index](../README.md)

CoACD does not currently publish a NuGet package. The engine now pulls the upstream repository, builds `_coacd` from source, and drops the produced native binary into `XRENGINE/runtimes/<rid>/native`. A fallback script is still available when you simply want to extract the vendor-provided wheel.

## Building from source (default path)

`XRENGINE/XREngine.csproj` invokes `Tools/Dependencies/Build-CoACD.ps1` before the managed build whenever the Windows binary is missing (or when you pass `/p:ForceCoACDBuild=true`). The script:

- clones or updates `https://github.com/SarahWeiii/CoACD.git` under `Build/Submodules/CoACD`
- configures CMake with the same flags as the official wheels (`/MT`, OpenVDB static, `_coacd` target)
- builds the selected RID
- copies `lib_coacd.*` into `XRENGINE/runtimes/<rid>/native`
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

Each invocation downloads `coacd-<version>-cp39-abi3-<suffix>.whl` directly from the [1.0.7 release](https://github.com/SarahWeiii/CoACD/releases/tag/1.0.7), expands it under `Build/Dependencies/CoACD`, and copies the `lib_coacd.*` file into `XRENGINE/runtimes/<rid>/native`.

## Build output

`XRENGINE/XREngine.csproj` treats `lib_coacd.dll` as a native asset, so the engine binaries automatically include the dependency after either script runs. Repeat the step for other RIDs if you ship non-Windows builds.

## Updating versions

1. Update the default `CoACDRef` property in `XRENGINE/XREngine.csproj` (or pass `/p:CoACDRef=<new-tag>` when building).
2. Re-run the build script (or the wheel extractor) for each RID you ship.
3. If the upstream project renames the native binary, update `XREngine.Data/Tools/CoACD.cs`, `Build-CoACD.ps1`, and `Get-CoACD.ps1` to match.
