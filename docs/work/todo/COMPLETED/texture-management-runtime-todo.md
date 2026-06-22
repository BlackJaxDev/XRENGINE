# Texture Management Runtime TODO

Status: historical; implementation complete; validation moved

The runtime texture-management implementation has been folded into the canonical texturing roadmap:

- [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](texture-runtime-streaming-virtual-texturing-todo.md)

The remaining runtime texture-management checks now live in:

- [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)

Original scope:

- OpenGL upload validation and generation-gated cancellation.
- Budgeted texture upload scheduling.
- Residency policy, coalescing, hysteresis, and VRAM pressure behavior.
- Dedicated `log_textures.txt` telemetry.
- ImGui texture streaming diagnostics.
- Shared render-work budgeting with shadow atlas work.

This file is intentionally kept as a redirect so older links remain useful. New implementation tasks should be added to the canonical TODO, and new scene/build/test evidence should be added to the validation ledger.
