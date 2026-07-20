# NVIDIA SDK drop folder

This repository does **not** redistribute NVIDIA proprietary SDK binaries (e.g., DLSS/NGX, Reflex, Streamline).

To enable NVIDIA features locally:

1. Prefer the repository installer for the public Streamline GitHub release:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-StreamlineSdk.ps1
   ```

   The installer downloads the official NVIDIA-RTX Streamline SDK release,
   pinned by default to version 2.12.0 and its published SHA-256 digest, and
   stages the production x64 runtime files in this folder.

   Manual install is still supported. Download the official NVIDIA
   Streamline/DLSS SDK from NVIDIA:
   - Streamline releases: <https://github.com/NVIDIA-RTX/Streamline/releases>
   - NVIDIA DLSS developer page: <https://developer.nvidia.com/rtx/dlss>
2. Extract the SDK and copy the required production x64 DLLs and any accompanying
   license files into:

   `ThirdParty/NVIDIA/SDK/win-x64/`

3. Rebuild the editor/app. The repo build copies these files next to the
   executable so the runtime can find them on the DLL search path.

Expected files for DLSS through Streamline (may vary by SDK/version):
- `sl.interposer.dll`
- `sl.common.dll`
- `sl.dlss.dll`
- `nvngx_dlss.dll`

Expected files for DLSS frame generation experiments (may vary by SDK/version):
- `sl.dlss_g.dll`
- `sl.reflex.dll`
- `sl.pcl.dll`
- `nvngx_dlssg.dll`
- Reflex/low-latency runtime DLLs included with the SDK, such as
  `NvLowLatencyVk.dll` when required by that SDK version

Other NVIDIA SDK files this folder may contain:
- `nvngx_*.dll` (NGX/DLSS)
- `NvLowLatencyVk.dll` (Reflex)
- `sl.*.dll` (Streamline)
- Any `*.license.txt` files that ship with the SDK

Runtime policy: when the engine explicitly requests NVIDIA DLSS upscale or
frame generation and the Vulkan/Streamline path cannot run, the renderer logs a
hard error instead of silently falling back to a normal blit. The DLLs in this
folder are necessary but not sufficient for DLSS-G unless Streamline reports
feature support, Reflex/PCL initialize, and the Vulkan swapchain is created
through the Streamline acquire/present proxy functions.

Use NVIDIA-provided SDK packages, not third-party DLL download sites. Do not
commit these SDK files to git.
