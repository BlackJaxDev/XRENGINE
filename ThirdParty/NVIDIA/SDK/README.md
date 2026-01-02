# NVIDIA SDK drop folder

This repository does **not** redistribute NVIDIA proprietary SDK binaries (e.g., DLSS/NGX, Reflex, Streamline).

To enable NVIDIA features locally:

1. Obtain the required NVIDIA SDK(s) from NVIDIA under their terms.
2. Copy the required DLLs and any accompanying license files into:

   `ThirdParty/NVIDIA/SDK/win-x64/`

Expected files (may vary by the SDK/version you use):
- `nvngx_*.dll` (NGX/DLSS)
- `NvLowLatencyVk.dll` (Reflex)
- `sl.*.dll` (Streamline)
- Any `*.license.txt` files that ship with the SDK

Do not commit these SDK files to git.
