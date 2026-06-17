# Native Dependencies

This page is the practical reference for native and external dependencies used by XRENGINE. The short version is:

- most core native dependencies are already wired into the build
- a few optional tools and SDKs need to be installed or dropped into known folders
- if you change a native dependency, validate the specific subsystem it affects

For the broadest built-in setup path, start with:

```powershell
ExecTool --bootstrap
```

That bootstrap flow covers the standard repo bootstrap path, but it should be treated as a strong baseline rather than a guarantee that every optional SDK is installed. For the full setup overview, see [Bootstrap And First-Time Setup](../../user-guide/setup/bootstrap.md).

## What is automatic vs manual

### Usually handled by the build

- CoACD build and staging
- Vulkan Memory Allocator bridge build and staging
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

### FastGltfBridge

Used for the native `.gltf` and `.glb` import path.

- Native project: `Build/Native/FastGltfBridge/FastGltfBridge.vcxproj`
- Runtime DLL: `FastGltfBridge.Native.dll`
- Managed staging location: `XREngine.Gltf/runtimes/win-x64/native`
- Vendored source snapshot:
	- `Build/Native/FastGltfBridge/vendor/fastgltf` (`fastgltf` v0.9.0, MIT)
	- `Build/Native/FastGltfBridge/vendor/simdjson` (`simdjson` v3.12.3, Apache-2.0)

The bridge is built automatically on Windows as part of `XREngine.Gltf.csproj` before managed build output is prepared. It keeps container parsing, local external-buffer loading, and coarse accessor-copy APIs native, while managed code keeps image decode, JSON `extras` retention, and engine-facing scene assembly on the existing paths.

To rebuild it directly:

```powershell
dotnet build .\XREngine.Gltf\XREngine.Gltf.csproj
```

Or build the native project itself with Visual Studio MSBuild if you are working on the bridge implementation.

Export smoke coverage lives in `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs` and should be kept green whenever the bridge ABI changes.

### Vulkan Memory Allocator

Used by the default Vulkan allocator backend.

VMA is not retrieved from upstream as a prebuilt DLL. GPUOpen VMA is a header-only C++ library, so the repository vendors a pinned `vk_mem_alloc.h` snapshot and builds our own native P/Invoke bridge DLL around it.

- Native project: `Build/Native/VulkanMemoryAllocatorBridge/VulkanMemoryAllocatorBridge.vcxproj`
- Runtime DLL: `VulkanMemoryAllocatorBridge.Native.dll`
- Managed staging location: `XREngine.Runtime.Rendering/runtimes/win-x64/native`
- Vendored source snapshot: `Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator` (VMA v3.3.0, MIT)
- Fetch script: `Tools/Dependencies/Get-VulkanMemoryAllocator.ps1`
- Direct build script: `Tools/Build-VulkanMemoryAllocatorBridge.ps1`
- Requirements: LunarG Vulkan SDK with `VULKAN_SDK` set, plus Visual Studio Build Tools with Desktop development with C++

For a fresh checkout:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-VulkanMemoryAllocator.ps1
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
```

The managed project builds the native bridge automatically on Windows before preparing build output. The native DLL is generated under `XREngine.Runtime.Rendering/runtimes/win-x64/native`, then copied beside the managed output as `VulkanMemoryAllocatorBridge.Native.dll` for P/Invoke loading.

If you are changing the native bridge and want to rebuild it directly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Build-VulkanMemoryAllocatorBridge.ps1 -RestoreVma
```

VS Code tasks are also available:

- `Install-VulkanMemoryAllocator`
- `Build-VulkanMemoryAllocatorBridge`

`ExecTool --bootstrap` runs the VMA fetch script with the other dependency installers. The normal editor/runtime build then compiles and stages the wrapper.

To intentionally update the VMA version, fetch the new tag, rebuild, and regenerate dependency docs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-VulkanMemoryAllocator.ps1 -Version vX.Y.Z -ForceDownload
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Build-VulkanMemoryAllocatorBridge.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\Generate-Dependencies.ps1
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
If that native C++ workload is missing, the script builds the managed `RiveSharp.dll`
without native project references so the engine, editor, settings generator, and docs
can still compile. In that fallback state, Rive UI components log a warning and remain
disabled at runtime until the native `rive.dll` is built and staged.

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

For DLSS through Streamline, use the repository installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-StreamlineSdk.ps1
```

The installer downloads the public NVIDIA-RTX Streamline SDK release, verifies
the archive digest, and copies the production x64 runtime files into
`ThirdParty/NVIDIA/SDK/win-x64/`. You can also run the `Install-StreamlineSdk`
VS Code task or the matching `ExecTool` dependency entry.

The staged files include:

- `sl.interposer.dll`
- `sl.common.dll`
- `sl.dlss.dll`
- `nvngx_dlss.dll`
- any accompanying `*.license.txt` files

For NVIDIA DLSS frame generation experiments, the same installer stages the
production DLSS-G runtime files from the official SDK, including:

- `sl.dlss_g.dll`
- `sl.reflex.dll`
- `sl.pcl.dll`
- `nvngx_dlssg.dll`
- Reflex/low-latency runtime DLLs included with the SDK, such as
  `NvLowLatencyVk.dll` when required by that SDK version

Rebuild the editor/app afterward so the build copies those files next to the
executable. Use NVIDIA-provided SDK packages rather than third-party DLL
download sites, and do not commit proprietary NVIDIA binaries. Manual SDK drops
are still supported; see the README files under `ThirdParty/NVIDIA/` for
folder-specific expectations.

When these runtime DLLs are missing, the ImGui settings inspector shows a red
`!` beside DLSS settings. Hover it for the exact reason those settings currently
have no effect. If DLSS upscale or frame generation is explicitly requested at
runtime and the Vulkan/Streamline path cannot run, the default render pipeline
logs a render error and stops instead of silently falling back to a standard
blit.

### OVRLipSync

Optional Meta/Oculus lip sync runtime.

- Expected location: `ThirdParty/Meta/OVRLipSync/win-x64/OVRLipSync.dll`

If present, it is copied into app outputs.

## Useful tasks and scripts

- `ExecTool --bootstrap`
- `Install-VulkanMemoryAllocator`
- `Build-VulkanMemoryAllocatorBridge`
- `Install-YtDlp`
- `Install-MsdfAtlasGen`
- `Install-NvComp`
- `Install-StreamlineSdk`
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

This includes vendored native-source changes such as fastgltf or simdjson snapshot updates inside `Build/Native/FastGltfBridge/vendor/`.
It also includes Vulkan Memory Allocator snapshot updates inside `Build/Native/VulkanMemoryAllocatorBridge/vendor/`.

Review the results for unknown or incompatible licenses before merging.

## Validation advice

Validate the narrowest affected path:

- physics changes: test the physics/unit-test world flows
- media changes: test video/audio playback paths
- XR changes: test the VR startup path you actually changed
- native UI or Rive changes: test editor UI paths directly

## Related documentation

- [Bootstrap And First-Time Setup](../../user-guide/setup/bootstrap.md)
- [Documentation Index](../README.md)
- [Unit Testing World](../testing/unit-testing-world.md)
- [CoACD Integration](../../architecture/assets/coacd.md)
- [Dependency Inventory](../../DEPENDENCIES.md)
