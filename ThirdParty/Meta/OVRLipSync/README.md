# OVR LipSync drop folder

This repository does **not** ship Meta/Oculus OVR LipSync SDK binaries as open-source artifacts.

To enable OVR LipSync locally:

1. Obtain OVR LipSync from Meta under the Oculus SDK terms.
2. Copy the native Windows x64 DLL into:

   `ThirdParty/Meta/OVRLipSync/win-x64/OVRLipSync.dll`

Expected file:
- `OVRLipSync.dll`

Notes:
- This binary is proprietary and optional.
- The engine will copy it to build output only when present.
- Do not commit SDK binaries unless your distribution policy explicitly allows it.
