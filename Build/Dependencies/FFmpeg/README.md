# FFmpeg Runtime Binaries (HLS Reference Build)

This folder is intentionally used for local/runtime FFmpeg binaries and is gitignored (except this file).

## Runtime path used by the engine

`UIVideoComponent` / `HlsReferenceRuntime` loads FFmpeg from:

- `Build/Dependencies/FFmpeg/HlsReference/win-x64/`

At runtime, the engine validates required DLL presence and accepts either major profile:

- FFmpeg 8 profile (`avformat-62`)
- FFmpeg 7 profile (`avformat-61`)

## Where these files come from

There are two sources in this repo workflow:

1. **Canonical local runtime (developer-provided, gitignored)**
   - Path: `Build/Dependencies/FFmpeg/HlsReference/win-x64/`
   - This is the folder actually used at runtime.

2. **Bootstrap source from pinned submodule**
   - Path: `Build/Submodules/Flyleaf/FFmpeg/`
   - Submodule commit: `510d509f716627e331813c1cf8e6d4a85408c565` (`v3.10.2`)
   - `XREngine.csproj` target `EnsureHlsReferenceFfmpeg` copies these DLLs into the canonical runtime folder only when no `avformat-62.dll` or `avformat-61.dll` exists there.

## Exact versions in this workspace (captured 2026-02-22)

### Active runtime currently in use (`HlsReference/win-x64`)

This workspace currently has the FFmpeg 7 profile loaded:

- `avcodec-61.dll` — FileVersion `61.19.101`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `avdevice-61.dll` — FileVersion `61.3.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `avfilter-10.dll` — FileVersion `10.4.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `avformat-61.dll` — FileVersion `61.7.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250415`
- `avutil-59.dll` — FileVersion `59.39.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `postproc-58.dll` — FileVersion `58.3.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `swresample-5.dll` — FileVersion `5.3.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`
- `swscale-8.dll` — FileVersion `8.3.100`, ProductVersion `n7.1.1-6-g48c0f071d4-20250414`

### Bootstrap copy source (`Build/Submodules/Flyleaf/FFmpeg`)

The pinned Flyleaf submodule currently contains FFmpeg 8 profile DLLs:

- `avcodec-62.dll` — FileVersion `62.11.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avdevice-62.dll` — FileVersion `62.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avfilter-11.dll` — FileVersion `11.4.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avformat-62.dll` — FileVersion `62.3.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avutil-60.dll` — FileVersion `60.8.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `swresample-6.dll` — FileVersion `6.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `swscale-9.dll` — FileVersion `9.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`

## How to refresh/update this record

When FFmpeg binaries change, update this README with:

1. Source path and source revision/tag (if from submodule or external package).
2. DLL list with `FileVersion` + `ProductVersion` from Windows file metadata.
3. Capture date.
