# Texture Streaming Cooked Cache TODO

Status: historical; implementation complete; validation moved

The cooked texture cache implementation has been folded into the canonical texturing roadmap:

- [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](texture-runtime-streaming-virtual-texturing-todo.md)

The remaining cold/warm cache validation checks now live in:

- [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)

Source analysis:

- [Texture Streaming Run Analysis - 2026-05-01 18:06](../../testing/texture-streaming-run-analysis-2026-05-01-180642.md)

Original scope:

- Prefer fresh cached `XRTexture2D` assets over raw PNG/JPG/TGA/EXR decode during runtime streaming.
- Cook runtime-streamable mip payloads with direct offsets and lengths.
- Prioritize visible preview residency before high-resolution promotion.
- Reuse prepared resident data across compatible canceled transitions.
- Bind role-aware fallback textures instead of black or null sampling paths.
- Split cache/source/upload timing in `log_textures.txt`.
- Coordinate texture finalization with shadow and render-thread work.

This file is intentionally kept as a redirect so older links remain useful. New implementation tasks should be added to the canonical TODO, and new scene/build/test evidence should be added to the validation ledger.
