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

2. **Bootstrap source from local dependency seed folder**
   - Path: `Build/Dependencies/FFmpeg/Seed/win-x64/`
   - `XREngine.csproj` target `EnsureHlsReferenceFfmpeg` copies these DLLs into the canonical runtime folder only when no `avformat-62.dll` or `avformat-61.dll` exists there.

## Retrieve seed DLLs from Flyleaf GitHub

To pull FFmpeg DLLs from Flyleaf without restoring the submodule:

```powershell
pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1
```

Default behavior:
- Downloads a Flyleaf repository archive (`SuRGeoNix/Flyleaf`, `master`).
- Detects the best x64 FFmpeg DLL folder in the archive.
- Copies detected FFmpeg DLLs into `Build/Dependencies/FFmpeg/Seed/win-x64/`.

Useful options:

```powershell
# Use a tag or branch
pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1 -Ref v3.8.2

# Also copy directly into the canonical runtime folder
pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1 -CopyToRuntime

# Force re-download and overwrite staged DLLs
pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1 -Force
```

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

### Bootstrap copy source (`Build/Dependencies/FFmpeg/Seed/win-x64`)

This workspace currently stages FFmpeg 8 profile DLLs in the seed folder:

- `avcodec-62.dll` — FileVersion `62.11.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avdevice-62.dll` — FileVersion `62.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avfilter-11.dll` — FileVersion `11.4.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avformat-62.dll` — FileVersion `62.3.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `avutil-60.dll` — FileVersion `60.8.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `swresample-6.dll` — FileVersion `6.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`
- `swscale-9.dll` — FileVersion `9.1.100`, ProductVersion `n8.0-16-gd8605a6b55-20250925`

## How to refresh/update this record

When FFmpeg binaries change, update this README with:

1. Source path and source revision/tag (if from external package).
2. DLL list with `FileVersion` + `ProductVersion` from Windows file metadata.
3. Capture date.
