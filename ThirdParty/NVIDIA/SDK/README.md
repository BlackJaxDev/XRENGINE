# NVIDIA SDK drop folder

This repository does **not** redistribute NVIDIA proprietary SDK binaries (e.g., DLSS/NGX, Reflex, Streamline).

To enable NVIDIA features locally:

1. Download the official NVIDIA Streamline/DLSS SDK from NVIDIA:
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

Other NVIDIA SDK files this folder may contain:
- `nvngx_*.dll` (NGX/DLSS)
- `NvLowLatencyVk.dll` (Reflex)
- `sl.*.dll` (Streamline)
- Any `*.license.txt` files that ship with the SDK

Use NVIDIA-provided SDK packages, not third-party DLL download sites. Do not
commit these SDK files to git.
