# Native Dependencies

This page is the practical reference for native and external dependencies used by XRENGINE. The short version is:

- most core native dependencies are already wired into the build
- a few optional tools and SDKs need to be installed or dropped into known folders
- if you change a native dependency, validate the specific subsystem it affects

For the broadest built-in setup path, start with:

```powershell
ExecTool --bootstrap
```

That bootstrap flow covers the standard repo bootstrap path, but it should be treated as a strong baseline rather than a guarantee that every optional SDK is installed. For the full setup overview, see `bootstrap.md`.

## What is automatic vs manual

### Usually handled by the build

- CoACD build and staging
- MagicPhysX runtime copy to output
- FFmpeg runtime copy to output when present
- `yt-dlp.exe` copy to executable output when installed in the repo-standard location
- `msdf-atlas-gen.exe` detection for font import workflows

### Usually manual

- installing optional helper tools
- rebuilding submodule-native binaries
- supplying proprietary NVIDIA SDK binaries locally
- placing optional vendor DLLs in their expected `ThirdParty/` locations

## Dependency reference

### CoACD

Used for convex decomposition workflows.

- Build path: triggered by `dotnet build` when needed
- Script: `Tools/Dependencies/Build-CoACD.ps1`
- Requirements: `git`, `cmake`, and Visual Studio tooling on Windows
- Output location: `XRENGINE/runtimes/<rid>/native`

If you need to force a rebuild, pass:

```powershell
dotnet build XRENGINE.slnx /p:ForceCoACDBuild=true
```

There is also a legacy fetch script if you want prebuilt vendor binaries instead:

```powershell
pwsh Tools/Dependencies/Get-CoACD.ps1
```

### MagicPhysX

Used for the current PhysX integration.

- Runtime DLL: `libmagicphysx.dll`
- Expected location: `XRENGINE/runtimes/win-x64/native`

If you update the MagicPhysX submodule or its native output, rebuild and revalidate physics-focused flows afterward.

### Rive

Used for Rive-based UI rendering.

- Source submodule: `Build/Submodules/rive-sharp`
- Runtime DLL: `rive.dll`
- Expected location: `XRENGINE/runtimes/win-x64/native`

To rebuild submodule binaries:

```powershell
./Tools/Build-Submodules.bat Debug x64
```

Or:

```powershell
./Tools/Build-Submodules.bat Release x64
```

This requires Visual Studio or Build Tools with the Desktop development with C++ workload.

### FFmpeg

Used for video/media decode, streaming, and audio extraction.

- Runtime DLLs live under `XRENGINE/runtimes/win-x64/native`
- Optional seed/reference files live under `Build/Dependencies/FFmpeg/`

To retrieve the repo's FFmpeg seed binaries:

```powershell
pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1
```

### yt-dlp

Optional. Used for resolving YouTube URLs to directly playable media URLs.

Install it into the repo-standard location with:

```powershell
pwsh Tools/Dependencies/Get-YtDlp.ps1
```

That places `yt-dlp.exe` under `Build/Dependencies/YoutubeDL/`, and the build copies it into executable output folders.

### msdf-atlas-gen

Optional. Used for MSDF font atlas generation.

Install it with:

```powershell
pwsh Tools/Dependencies/Get-MsdfAtlasGen.ps1
```

The importer looks for it under `Build/Dependencies/MsdfAtlasGen/`.

### NVIDIA SDK binaries

Optional. Not redistributed in the repository.

This includes local enablement for features such as:

- DLSS
- Reflex
- Streamline
- ReSTIR GI bridge binaries

Place the required files under the appropriate `ThirdParty/NVIDIA/` folders. See the README files under that tree for exact expectations.

### OVRLipSync

Optional Meta/Oculus lip sync runtime.

- Expected location: `ThirdParty/Meta/OVRLipSync/win-x64/OVRLipSync.dll`

If present, it is copied into app outputs.

## Useful tasks and scripts

- `ExecTool --bootstrap`
- `Install-YtDlp`
- `Install-MsdfAtlasGen`
- `Install-NvComp`
- `Install-Phonon`
- `Install-Audio2Face3D-SDK`
- `Install-TensorRT`
- `Install-CUDA`
- `ExecTool.bat`

For first-time setup across the repo, `ExecTool --bootstrap` is the broadest built-in setup path.

What it currently does:

1. initializes all git submodules
2. runs every dependency installer listed in the `Deps` group in `ExecTool.bat`
3. builds submodules
4. builds the DocFX site
5. launches the docs server
6. launches the editor

What it does not guarantee:

- proprietary SDK setup
- every optional task-defined dependency outside the `Deps` group
- workstation-specific prerequisites such as missing Visual C++ build workloads

## If you change dependencies

After changing NuGet packages or submodules, refresh the dependency inventory:

```powershell
pwsh Tools/Reports/Generate-Dependencies.ps1
```

That updates:

- `docs/DEPENDENCIES.md`
- `docs/licenses/`

Review the results for unknown or incompatible licenses before merging.

## Validation advice

Validate the narrowest affected path:

- physics changes: test the physics/unit-test world flows
- media changes: test video/audio playback paths
- XR changes: test the VR startup path you actually changed
- native UI or Rive changes: test editor UI paths directly

## Related documentation

- [Bootstrap And First-Time Setup](bootstrap.md)
- [Documentation Index](../README.md)
- [Unit Testing World](unit-testing-world.md)
- [CoACD Integration](../architecture/CoACD.md)
- [Dependency Inventory](../DEPENDENCIES.md)