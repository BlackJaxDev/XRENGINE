# Project TODO

## Active Items
- [ ] Integrate HLS player adapter into `UIVideoComponent` for Twitch HLS playback
- [ ] Map OpenGL output to `XRMaterialFrameBuffer` textures
- [ ] Forward Twitch auth headers into streaming open options
- [ ] Bridge streaming audio frames to `AudioSourceComponent`
- [ ] Add telemetry/logging for dropped frames and reconnects

## Implementation Plan
1. Stabilize `RendererGL` by wiring it to the engine-owned Silk.NET `GL` instance, copying frame data safely (or ref-counting `AVFrame`s), and cleaning up textures when frames are released.
2. Mirror the D3D path’s pixel-format handling (HDR metadata, rotation, chroma plane sizes) so the GL renderer configures textures correctly before upload.
3. Instantiate `HlsPlayerAdapter` inside `UIVideoComponent`, passing in `OpenGLRenderer.RawGL`, the host adapter, and the GL framebuffer ID derived from `_fbo`.
4. Replace the manual FFmpeg decode loop with `PlayerGL.OpenAsync/Play`, forwarding Twitch headers via `Config.Demuxer.HttpHeaders`, `HttpUserAgent`, and `FormatOptToUnderlying`.
5. On each material render, invoke `player.GLPresent()` (render-thread only) to populate the video texture, and optionally pipe audio/telemetry into existing engine systems.

## Backlog
- [ ] Abstract video playback so other components can reuse streaming pipeline
- [ ] Expose quality selection UI for multiple Twitch renditions
- [ ] Investigate Vulkan parity for video streaming

## Done
- [x] Audit existing FFmpeg-based streaming component
- [x] Review external-reference Twitch HLS implementation details
- [x] Outline integration plan for OpenGL path
- [x] Implement safe texture uploads in `RendererGL` (needs `ActivityGL`/`AudioGL`/`SubtitlesGL`/`DataGL` class stubs to unblock builds)
