# NVIDIA RTXGI drop folder

This repository does **not** redistribute NVIDIA RTXGI proprietary SDK binaries.

To enable optional RTXGI native interop locally:

1. Obtain RTXGI SDK artifacts from NVIDIA under their terms.
2. Place the native bridge DLL in:

   `ThirdParty/NVIDIA/RTXGI/win-x64/RestirGI.Native.dll`

Expected file:
- `RestirGI.Native.dll`

Notes:
- This binary is optional.
- The managed project copies it to output only when present.
- If you build `Build/RestirGI/RestirGINative.sln`, the build target now stages the DLL into this folder.
