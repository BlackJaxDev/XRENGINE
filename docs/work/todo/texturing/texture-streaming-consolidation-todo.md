# Texture Streaming Consolidation TODO

Status: historical; implementation complete; validation moved

The texture streaming consolidation is complete. The service-boundary design and future roadmap now live in:

- [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](texture-runtime-streaming-virtual-texturing-todo.md)

The remaining consolidation validation checks now live in:

- [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)

Original closeout summary:

- `ImportedTextureStreamingManager` became the frame-level coordinator.
- Source/cache loading moved to `TextureStreamingSourceFactory`, `AssetTextureStreamingSource`, `ThirdPartyTextureStreamingSource`, and `TextureStreamingResidentDataReuseCache`.
- Residency decisions moved to `TextureResidencyPolicy`.
- Texture records and usage snapshots moved to `TextureStreamingRegistry`.
- Pending transitions moved to `TextureTransitionQueue`.
- Upload priority, coalescing, frame budget, generation cancellation, and queue-wait telemetry moved to `TextureUploadScheduler`.
- Mutable sparse residency state moved to `TextureResidencyState`.
- OpenGL tiered and sparse residency backends moved behind `ITextureResidencyBackend`.
- Cooked texture usability became metadata-first.

This file is intentionally kept as a redirect so older links remain useful. New implementation tasks should be added to the canonical TODO, and new scene/build/test evidence should be added to the validation ledger.
