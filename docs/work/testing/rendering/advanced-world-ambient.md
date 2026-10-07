# Advanced World Ambient Validation

## Scope

Validate fixed world ambient in Advanced native opaque shading. Use the Math
Intersections skinned GPU chain, Vulkan, and a fixed camera exposure. Disable
dynamic GI and probes. Set the directional light intensity to zero for the
ambient isolation checks. Wait for rendered frames after each setting change.

See the [ambient contract](../../../developer-guides/components/procedural-skybox-ambient.md)
and [chain investigation](../../investigations/physics/skinned-gpu-chain-benchmark-2026-10-06.md).

## Acceptance Matrix

| Check | Expected result | Latest result |
| --- | --- | --- |
| Ambient 0.15, intensity 1, direct light zero | Mesh faces remain visible | Passed on Vulkan, 2026-10-06 |
| Ambient intensity zero, direct light zero | Non-emissive mesh becomes black | Passed |
| Change ambient color only | Mesh takes the ambient color | Passed: blue 0.4 to green 0.4, intensity 1 |
| Change ambient intensity only | Mesh brightness changes | Passed: green intensity 1 to 0.1 |
| Opposite camera positions | All face directions receive ambient | Passed: above/front and below/back |
| Restore direct light and neutral ambient | Direct shading and visible dark faces coexist | Passed |
| GPU chain animation | Mesh continues to deform | Passed across the viewed captures; physics readback submissions 0 |
| OpenGL, MSAA, stereo | Same ambient contract | Not run |
| Dynamic GI diffuse ownership | No duplicate baseline diffuse | Not run |

## Evidence

The isolated Release editor build passed with zero warnings and zero errors.
The live path used Vulkan, Advanced native shading, one admitted deformation
job, and no visible CPU fallback. The publication reported no rejection.
Exposure was fixed at 1. Dynamic GI and probes were disabled.

Evidence is under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261006-110030-skinned-chain/`.
The `reports/ambient-fixed-*.json` files identify the corresponding viewed PNGs
in `mcp-captures/`: neutral-front, zero, blue, green, green-dim, neutral-back,
restored-back, and restored-front. The final scene restores ambient
`(0.15, 0.15, 0.15)` at intensity 1 and directional intensity 1.
Screenshot readback is diagnostic. It does not change the zero-readback physics
path. The initial launch fault and the healthy retry are recorded in the
linked investigation. OpenGL, MSAA, stereo, and dynamic GI remain untested live.
