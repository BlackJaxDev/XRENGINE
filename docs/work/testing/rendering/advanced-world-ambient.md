# Advanced World Ambient Validation

Scope: Validate fixed world ambient in Advanced native opaque shading.

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Procedural Skybox Ambient](../../../developer-guides/components/procedural-skybox-ambient.md)  Code todos: none.

## Setup

Use the Math Intersections skinned GPU chain, Vulkan, and a fixed camera exposure. Disable dynamic GI and probes. Set directional light intensity to zero for ambient isolation checks. Wait for rendered frames after each setting change.

## Checks

### Fixed Ambient
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Neutral ambient | Set ambient color to `(0.15, 0.15, 0.15)`, intensity to `1`, and direct light to zero. | Mesh faces remain visible. | Passed | 2026-10-06 |
| Zero ambient | Set ambient intensity to zero and direct light to zero. | Non-emissive mesh becomes black. | Passed | 2026-10-06 |
| Ambient color | Change ambient color only. | Mesh takes the ambient color. | Passed | 2026-10-06 |
| Ambient intensity | Change ambient intensity only. | Mesh brightness changes. | Passed | 2026-10-06 |
| Opposite cameras | Capture above-front and below-back camera positions. | All face directions receive ambient. | Passed | 2026-10-06 |
| Direct light restore | Restore direct light and neutral ambient. | Direct shading and visible dark faces coexist. | Passed | 2026-10-06 |
| GPU chain animation | Run the skinned GPU chain during ambient changes. | Mesh continues to deform with no visible CPU fallback. | Passed | 2026-10-06 |
| OpenGL | Repeat the checks on OpenGL. | Ambient contract matches Vulkan. | Open | none |
| MSAA | Repeat the checks with MSAA. | Ambient contract matches non-MSAA output. | Open | none |
| Stereo | Repeat the checks in stereo. | Ambient contract matches mono output. | Open | none |
| Dynamic GI ownership | Enable dynamic GI diffuse ownership checks. | No duplicate baseline diffuse appears. | Open | none |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
