# FFmpeg Runtime Binaries (HLS Reference Build)

This folder is intentionally used for local/runtime FFmpeg binaries and is gitignored (except this file).

## Why this exists

`UIVideoComponent` currently needs an FFmpeg build known to work with HLS live behavior from the pinned external reference implementation.
During migration to a fully native engine media pipeline, the engine loads FFmpeg from:

- `Build/Dependencies/FFmpeg/HlsReference/win-x64/`

## Required files (current expected set)

- `avcodec-62.dll`
- `avdevice-62.dll`
- `avfilter-11.dll`
- `avformat-62.dll`
- `avutil-60.dll`
- `swresample-6.dll`
- `swscale-9.dll`

## Source of truth

Retrieve the binaries from the pinned upstream media reference build that matches this repository's expected DLL versions.
Track the exact source revision in `Build/Dependencies/FFmpeg/HlsReference/VERSION.txt`.

## Recommended local setup

1. Create folder: `Build/Dependencies/FFmpeg/HlsReference/win-x64/`
2. Copy the required DLLs listed above into that folder.
3. Run/build the engine.

At runtime, `UIVideoComponent` validates required DLL presence and expected avformat major version.
