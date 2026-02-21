# Phased TODO: Native HLS Streaming Subsystem (UIVideoComponent)

## End State (Target)
- `UIVideoComponent` owns a native engine video pipeline with no external media-framework references.
- Engine performs its own HLS demuxing/decoding via FFmpeg interop.
- Video renders through engine-native OpenGL and Vulkan uploaders.
- Streaming audio is output through `AudioSourceComponent`.
- External media framework is used only as an implementation reference during migration, then fully removed.

## Non-Goals
- Reproducing external UI host behaviors (WPF/WinUI/WinForms).
- Keeping external media framework as a runtime dependency.

## Phase 0 — Lock Dependencies and Baseline
- [x] Pin exact FFmpeg binary build currently known to work for HLS live in this repo.
- [x] Copy that FFmpeg build into an engine-owned location (single canonical folder) and stop relying on submodule path lookup for runtime resolution.
- [x] Document binary provenance and version manifest (exact DLL names/versions/checksums).
- [x] Add startup validation that logs loaded FFmpeg DLL versions and fails clearly on mismatch.
- [x] Remove duplicate/competing FFmpeg copy paths from project output and prioritize one canonical path.

Progress note: canonical folder + manifests are under `Build/Dependencies/FFmpeg/HlsReference`.

## Phase 1 — New Video Subsystem API (Engine-Owned)
- [x] Create `VideoStreamingSubsystem` under engine code.
- [x] Define contracts:
  - [x] `IHlsStreamResolver`
  - [x] `IMediaStreamSession`
- [x] Define core DTOs:
  - [x] `DecodedVideoFrame`
  - [x] `DecodedAudioFrame`
  - [x] `StreamOpenOptions`
- [x] Add bounded queues and backpressure policy (`latest-frame-wins` for video; low-latency ring for audio).

## Phase 2 — `UIVideoComponent` Integration
- [x] Replace temporary adapter path with `VideoStreamingSubsystem` session object.
- [x] Keep existing `StreamUrl`/lifecycle behavior while moving internals to subsystem calls.
- [x] Route decoded PCM to `AudioSourceComponent` with dedicated streaming buffer path.
- [x] Add resync policy between audio clock and video presentation time.

## Phase 3 — HLS/Twitch Resolver Hardening
- [x] Move Twitch GQL token flow into dedicated resolver service.
- [x] Remove hardcoded test m3u8 override from `GetTwitchStreamUrl` path.
- [x] Pass resolver-provided headers/referer/user-agent into demuxer options.
- [x] Implement retry/backoff for token/playlist/segment failures.
- [x] Add telemetry: open latency, rebuffer count, retry count, stream uptime.

## Phase 4 — OpenGL Renderer Path
- [x] Implement OpenGL uploader (PBO/staging strategy, texture lifecycle, frame pacing).
- [x] Ensure decode-thread to render-thread handoff safety.
- [ ] Wire `HlsPlayerAdapter` decode callbacks into `IMediaStreamSession` frame queues so native path feeds real video/audio samples.
- [ ] Validate with Twitch live HLS, VOD HLS, and generic m3u8 endpoints.

## Phase 5 — Vulkan Renderer Path
- [ ] Implement Vulkan staging upload path (buffer copy, layout transitions, sync barriers).
- [ ] Mirror OpenGL timing and queue policies for consistent behavior.
- [ ] Add Vulkan diagnostics for upload stalls and sync hazards.

## Phase 6 — Audio Path Completion
- [ ] Implement streaming PCM producer for `AudioSourceComponent`.
- [ ] Support drift correction and underrun/overrun mitigation.
- [ ] Confirm A/V sync under packet jitter and reconnect scenarios.

## Phase 7 — External Framework Decommission
- [x] Remove direct project references to external media framework projects (boundary now uses package dependency).
- [ ] Remove adapter code that depends on external media framework APIs.
- [ ] Remove remaining framework-specific startup/config code from `UIVideoComponent`.
- [ ] Update dependency docs and submodule list once no longer needed.

## Phase 8 — Validation and Release Criteria
- [ ] Build/test matrix for OpenGL + Vulkan with live HLS streams.
- [ ] Regression tests: open/close loops, reconnects, long-run playback, scene reloads.
- [ ] Remove temporary native->legacy no-frame watchdog fallback once native frame production is complete.
- [ ] Performance and memory targets:
  - [ ] startup/open time
  - [ ] upload latency and dropped-frame budget
  - [ ] bounded queue memory and no unmanaged leaks
- [ ] Sign-off checklist before removing fallback path.
