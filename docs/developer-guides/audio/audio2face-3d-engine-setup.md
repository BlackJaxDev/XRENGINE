# Audio2Face-3D Engine Setup

This guide is the canonical setup path for using NVIDIA Audio2Face-3D live inference inside XRENGINE.

The goal is to end with all of the following working together:

- CUDA installed on the machine.
- TensorRT extracted locally and exposed through `TENSORRT_ROOT_DIR`.
- The upstream NVIDIA Audio2Face-3D SDK fully built under `Build/Dependencies/Audio2Face-3D-SDK/_build/release`.
- The XREngine native bridge built from `Build/Native/Audio2XBridge/Audio2XBridge.vcxproj`.
- `Audio2Face3DNativeBridgeComponent` able to load `Audio2XBridge.Native.dll` at runtime.

## What The Prereq Task Does

The VS Code task `Install-Audio2Face3D-Prereqs` is a convenience wrapper only. It runs these existing tasks in sequence:

- `Install-CUDA`
- `Install-TensorRT`
- `Install-Audio2Face3D-SDK`

It is not a zero-config installer.

Two of the prerequisites still require user-supplied installers or authenticated downloads:

- CUDA: the script needs either a local installer path or a direct NVIDIA installer URL.
- TensorRT: the script can try a pinned direct URL for `10.13.3.9`, but NVIDIA commonly gates that download behind an authenticated developer session.

That means the prereq task works best after you have already staged the CUDA installer and the TensorRT zip, or when you can supply direct URLs yourself.

## Supported Versions

Use versions that match the upstream NVIDIA SDK requirements and the XREngine bridge defaults:

- Visual Studio 2022 with C++ desktop build tools
- CUDA `12.9.x` recommended
- TensorRT `10.13.x`, currently pinned in XREngine docs/scripts to `10.13.3.9` on Windows CUDA `12.9`
- Python `3.8` through `3.10.x`
- `git`
- `git-lfs`

## Step 1: Install Base Tooling

Install these first if they are not already on the machine:

1. Visual Studio 2022 or Build Tools 2022 with the MSVC C++ toolchain.
2. Git.
3. Git LFS.
4. Python 3.10.x.

Recommended checks:

```powershell
git --version
git lfs version
python --version
```

## Step 2: Install CUDA Correctly

Get the Windows CUDA Toolkit installer from NVIDIA's CUDA Toolkit download page.

You have two supported flows.

### Option A: Reuse A Local CUDA Installer

Download the installer manually, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-CudaToolkit.ps1 \
  -InstallerPath "C:\Downloads\cuda_12.9.1_windows.exe" \
  -Install
```

### Option B: Use A Direct Download URL

If you already know the exact NVIDIA installer URL, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-CudaToolkit.ps1 \
  -DownloadUrl "https://developer.download.nvidia.com/.../cuda_installer.exe" \
  -Version "12.9.1" \
  -Install
```

### Option C: Stage The Installer For The VS Code Task

Place the installer in:

```text
Build\Dependencies\CUDA\downloads
```

with a filename matching `cuda-*.exe` or `cuda_*.exe`, then rerun `Install-CUDA` or `Install-Audio2Face3D-Prereqs`.

### Verify CUDA

After install, open a new shell and verify:

```powershell
$env:CUDA_PATH
Test-Path "$env:CUDA_PATH\bin\nvcc.exe"
```

If `CUDA_PATH` is still blank, either reopen the shell or set it manually before building the NVIDIA SDK.

## Step 3: Install TensorRT Correctly

Get the Windows TensorRT SDK zip from NVIDIA's TensorRT download portal.

For the currently documented XREngine path, use:

- TensorRT `10.13.3.9`
- Windows package for CUDA `12.9`

The expected archive name is:

```text
TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip
```

### Important

The script `Get-TensorRT.ps1` has a built-in mapping for that exact version and will try this URL automatically:

```text
https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/10.13.3.9/TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip
```

On many machines this still fails because NVIDIA requires an authenticated browser session for the asset. When that happens, download the zip manually from the TensorRT portal and use `-ArchivePath`.

### Recommended Flow: Manual Download + Scripted Extract

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-TensorRT.ps1 \
  -ArchivePath "C:\Downloads\TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip" \
  -SetUserEnvironment
```

### Alternate Flow: Let The Script Try The Built-In URL

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-TensorRT.ps1 \
  -SetUserEnvironment
```

If NVIDIA blocks the download, the script now tells you to download the archive manually and rerun with `-ArchivePath`.

### Option C: Stage The Archive For The VS Code Task

Place the zip in:

```text
Build\Dependencies\TensorRT\downloads
```

then rerun `Install-TensorRT` or `Install-Audio2Face3D-Prereqs`.

### Verify TensorRT

The extracted TensorRT root must contain at least:

```text
include\NvInfer.h
lib\nvinfer.lib
```

Verify with:

```powershell
$env:TENSORRT_ROOT_DIR
Test-Path "$env:TENSORRT_ROOT_DIR\include\NvInfer.h"
Test-Path "$env:TENSORRT_ROOT_DIR\lib\nvinfer.lib"
```

## Step 4: Acquire The NVIDIA Audio2Face-3D SDK Source

Once CUDA and TensorRT are ready, fetch or update the upstream NVIDIA SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-Audio2Face3DSdk.ps1
```

This script does the following:

- clones or updates `Build\Dependencies\Audio2Face-3D-SDK`
- runs `git lfs pull`
- runs `fetch_deps.bat release`

At this point the source tree is ready, but the SDK is not built yet.

## Step 5: Build The NVIDIA Audio2Face-3D SDK Fully

Open a new PowerShell window so the latest environment variables are visible, then verify:

```powershell
$env:CUDA_PATH
$env:TENSORRT_ROOT_DIR
```

Now build the upstream SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-Audio2Face3DSdk.ps1 -Build
```

This runs the upstream `build.bat all release` flow.

Successful output should produce:

```text
Build\Dependencies\Audio2Face-3D-SDK\_build\release\audio2x-sdk\bin\audio2x.dll
Build\Dependencies\Audio2Face-3D-SDK\_build\release\audio2x-sdk\lib\audio2x.lib
```

Verify with:

```powershell
Test-Path .\Build\Dependencies\Audio2Face-3D-SDK\_build\release\audio2x-sdk\bin\audio2x.dll
Test-Path .\Build\Dependencies\Audio2Face-3D-SDK\_build\release\audio2x-sdk\lib\audio2x.lib
```

## Step 6: Optional Models And Test Data

If you want to run NVIDIA samples or generate TensorRT test assets, use the same script after authenticating with Hugging Face:

```powershell
hf auth login
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-Audio2Face3DSdk.ps1 -DownloadModels -GenerateTestData
```

This is optional for the XREngine native bridge itself, but it is useful for validating the upstream SDK build and sample pipelines.

## Step 7: Validate The Upstream SDK Build

The upstream SDK expects CUDA and TensorRT DLLs to be on `PATH` when running binaries. Use NVIDIA's wrapper script:

```powershell
Push-Location .\Build\Dependencies\Audio2Face-3D-SDK
.\run_sample.bat .\_build\release\audio2face-sdk\bin\audio2face-unit-tests.exe
Pop-Location
```

If you downloaded models and generated test data, you can also try:

```powershell
Push-Location .\Build\Dependencies\Audio2Face-3D-SDK
.\run_sample.bat .\_build\release\audio2face-sdk\bin\sample-a2f-executor.exe
Pop-Location
```

## Step 8: Build The XREngine Native Bridge

Do not use `dotnet build` for the native bridge project. It is a `.vcxproj` and must be built with Visual Studio or `MSBuild.exe`.

Build:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" \
  .\Build\Native\Audio2XBridge\Audio2XBridge.vcxproj \
  /p:Configuration=Debug \
  /p:Platform=x64
```

When the upstream SDK outputs exist, the bridge project automatically:

- enables the real backend compile flag
- links `audio2x.lib`
- copies the required `audio2x` runtime DLLs into the bridge output directory

Expected output:

```text
Build\Tools\Debug\Audio2XBridge\Audio2XBridge.Native.dll
```

## Step 9: Make The Bridge Available To The Editor

The managed loader looks for `Audio2XBridge.Native.dll` by name and expects it to be loadable by the editor process.

The simplest setup is:

1. Build the bridge.
2. Copy `Audio2XBridge.Native.dll` and any copied native dependencies from the bridge output folder beside the editor executable.

The editor-side loader error text is explicit when this is missing.

## Step 10: Configure The Engine

For live inference in XREngine:

1. Add `Audio2Face3DComponent`, `Audio2Face3DNativeBridgeComponent`, and `MicrophoneComponent` to the same scene node.
2. Set `Audio2Face3DComponent.SourceMode` to `LiveStream`.
3. Point the bridge component at your Audio2Face and Audio2Emotion model paths.
4. Keep microphone capture at `16 kHz` PCM16, or leave `AutoConfigureMicrophoneFormat` enabled.
5. Connect the bridge from the editor inspector.

## Common Failure Cases

### `Install-CUDA` says it needs `-InstallerPath` or `-DownloadUrl`

That is expected when no CUDA installer has been staged yet. The script cannot guess NVIDIA's installer URL. Download the installer manually or provide the exact URL.

### `Install-TensorRT` says the built-in URL failed

That usually means NVIDIA is gating the asset behind an authenticated browser session. Download the zip manually and rerun `Get-TensorRT.ps1 -ArchivePath ...`.

### `Get-Audio2Face3DSdk.ps1` finishes but says build was skipped

That means source acquisition succeeded, but the upstream SDK was not compiled. Set `TENSORRT_ROOT_DIR`, verify `CUDA_PATH`, and rerun with `-Build`.

### `Audio2XBridge.Native` says the backend is not enabled

That means the native bridge project compiled in fallback mode because the upstream SDK outputs were not present yet. Build the NVIDIA SDK first, then rebuild the bridge.

### `dotnet build` fails on `Audio2XBridge.vcxproj`

That is expected. Use `MSBuild.exe` or Visual Studio for the native bridge project.

## Recommended End-To-End Command Sequence

If you already downloaded the CUDA installer and TensorRT zip manually, the full path is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-CudaToolkit.ps1 \
  -InstallerPath "C:\Downloads\cuda_12.9.1_windows.exe" \
  -Install

powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-TensorRT.ps1 \
  -ArchivePath "C:\Downloads\TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip" \
  -SetUserEnvironment

powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-Audio2Face3DSdk.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-Audio2Face3DSdk.ps1 -Build
```

After that, build the bridge project with `MSBuild.exe`, copy the resulting native bridge DLL beside the editor executable, and use the live bridge component in the editor.