# VR Development

[Back to user guide](README.md)

Use this page when configuring and testing VR projects. For code-facing VR APIs and components, see [VR Developer Guide](../developer-guides/vr/vr-development.md). For renderer internals, see [OpenVR Rendering](../architecture/rendering/openvr-rendering.md) and [OpenXR VR Rendering](../architecture/rendering/openxr-vr-rendering.md).

## Runtime Paths

XRENGINE has OpenXR and SteamVR/OpenVR paths. OpenVR is currently the tested path. OpenXR exists and is actively documented, but should be validated for the specific runtime and headset before relying on it.

VR can run in local, client, or server-oriented modes depending on whether tracking, rendering, and networking live in one process or are split across processes.

## Setup Checklist

- Choose the target runtime and renderer.
- Provide the action manifest and VR manifest expected by the runtime path.
- Start with local mode while validating tracking and input.
- Add a humanoid or VR player rig only after headset/controller poses are visible.
- Watch frame timing closely; VR work should target stable low-latency rendering.

## Testing

Always test on real hardware before trusting comfort, performance, tracking, or input behavior. Desktop preview can verify scene setup but cannot validate motion-to-photon latency or headset runtime behavior.

## Deeper Docs

- [VR Developer Guide](../developer-guides/vr/vr-development.md)
- [OpenXR Runtime](../developer-guides/vr/openxr-runtime.md)
- [OpenVR Rendering](../architecture/rendering/openvr-rendering.md)
- [OpenXR VR Rendering](../architecture/rendering/openxr-vr-rendering.md)
