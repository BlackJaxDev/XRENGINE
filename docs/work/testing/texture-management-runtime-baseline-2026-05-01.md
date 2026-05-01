# Texture Management Runtime Baseline - 2026-05-01

This note preserves the initial Sponza startup/import symptoms that motivated the texture-management runtime work.

## Baseline Logs

Session root:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_15-37-23_pid47288/`

Files to compare against after the runtime changes:

- `log_opengl.txt`
- `log_general.txt`
- `log_rendering.txt`
- `profiler-fps-drops.log`
- `profiler-render-stalls.log`

## Baseline Symptoms

- Imported-scene texture promotion stayed delayed while import scopes forced `allowPromotions=False`.
- `XRWindow.ProcessPendingUploads` appeared as a render-thread stall during startup.
- Runtime-managed progressive uploads still pushed whole mips often enough to produce multi-frame stalls.
- OpenGL emitted `GL_INVALID_VALUE` from `TexSubImage2D` during imported texture residency changes, including the Sponza bump-map repro path.
- Resident transitions repeated for textures whose target residency had not materially changed.
- Shadow atlas tile rendering and texture upload bursts contended during startup, making stall attribution hard from separate logs.

## Validation Commands

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "GLTexture2DContractTests|ImportedTextureStreamingPhaseTests|RuntimeRenderingHostServicesTests" --no-restore
```

## After-Change Checks

- Confirm `log_textures.txt` exists in the same per-session log directory.
- Compare `Texture.UploadValidationFailed`, `Texture.TransitionCoalesced`, `Texture.UploadSlow`, `Texture.VramPressure`, and `Texture.VramSummary` against `log_opengl.txt`, `profiler-fps-drops.log`, and `profiler-render-stalls.log`.
- Run a low texture-upload budget pass with `TextureUploadFrameBudgetMilliseconds` set near `1.0` to force row-chunking behavior.
- Repeat with active shadow atlas updates and inspect `Texture.DelayedByShadow` entries for shared render-work contention.
