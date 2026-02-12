# GPU Indirect Backend Capability Matrix

Last Updated: 2026-02-12

This matrix defines the runtime expectations for Phase 6 parity between OpenGL and Vulkan in the GPU indirect pipeline.

## Capability Matrix

| Capability | OpenGL | Vulkan | Runtime Fallback |
| --- | --- | --- | --- |
| Draw indirect buffer binding | Required | Required | Abort indirect submission if missing |
| Parameter/count buffer binding | Required for count path | Required for count path | Use non-count indirect path |
| `*IndirectCount` draw support | Extension/driver-dependent | Feature/extension-dependent | Use `MultiDrawElementsIndirect`/offset variant |
| Indexed VAO / index buffer validity | Required | Required | Abort draw and log validation warning |
| Indirect command stride (`DrawElementsIndirectCommand`) | 20 bytes | 20 bytes | Startup validation throws on mismatch |
| ViewSet bindings (`11`-`15`) | Required | Required | Disable per-view fan-out path if invalid |

## Parity Checklist (Runtime)

The runtime now evaluates a per-dispatch checklist in `HybridRenderingManager`:

- Draw indirect buffer binding readiness
- Parameter buffer binding readiness
- Backend count-draw capability
- Count-path disable override
- Indexed VAO validity
- Selected submission path (`CountDraw` vs `Fallback`)

These values are emitted through validation logging to make backend path selection explicit.

## Parity Tests

Phase 6 parity tests are covered by:

- `XREngine.UnitTests/Rendering/GpuBackendParityTests.cs`

Test coverage includes:

- Count path selection when backend supports indirect-count draw
- Fallback path selection when indirect-count draw is unavailable
- Cross-backend equivalence checks for:
  - visible command count
  - draw count
  - sampled command signatures (`mesh/material/pass`)

## Notes

- The parity comparer uses sampled command signatures by design for low-overhead diagnostics.
- Full-frame scene parity should still be validated with GPU integration tests in hardware environments for release gates.
