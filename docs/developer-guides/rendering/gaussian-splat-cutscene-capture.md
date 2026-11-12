# Gaussian Splat Cutscene Capture

This guide explains the editor commands that cook a Unity avatar prefab and record a cutscene video of it as
Uber-shaded Gaussian splats or as triangles. Use it when you record a cutscene, or when a cook or a recording is slow.

## Cook A Unity Prefab

`--cook-unity-prefab` imports a Unity prefab, binds an optional humanoid clip, and writes a `.pak` archive.

```powershell
dotnet run --no-build -c Release --project XREngine.Editor/XREngine.Editor.csproj -- `
  --cook-unity-prefab `
  --unity-project "<unity-project>" `
  --prefab "<unity-project>/Assets/<path>/Avatar.prefab" `
  --anim "<unity-project>/Assets/<path>/Walk.anim" `
  --asset "Avatars/avatar.asset" `
  --output-pak "<output>/avatar.pak"
```

The cook stops when the Unity project is missing a required material or texture. Add `--allow-partial-import` to keep
the available content, use fallback materials, and print each missing asset. A partial cook does not match the Unity
look for the parts that use the missing assets.

When the avatar is a prefab instance inside a `.unity` scene, pass the `.unity` path to `--prefab` and add
`--scene-root "<top-level-node-name>"` to select that root node and move its root translation to the origin.

The cook does these stages. At the end, also after a failure, it prints the wall time of each stage:

| Stage | Work |
|---|---|
| `import-prefab` | Reads the Unity YAML and the FBX files, converts the materials, and cooks meshlets. |
| `prune-inactive-models` | Removes disabled `ModelComponent` instances. |
| `hydrate-textures` | Decodes the source texture files and resizes them to the maximum texture size. |
| `init-uber-materials` | Initializes the authored Uber state of each converted material. |
| `rebuild-meshes` | Rebuilds the runtime meshes of each active model. |
| `attach-humanoid-clip` | Maps the humanoid bones and attaches the `.anim` clip. |
| `serialize-prefab` | Serializes the prefab into the cooked binary format. |
| `pack-archive` | Writes the staged asset and packs the `.pak` file. |
| `verify-load-pak` | Loads the written `.pak` again to verify the material semantics. |
| `verify-bounds` | Computes the content bounds of the loaded prefab. |
| `verify-motion` | Selects the animation clip of the loaded prefab. |
| `verify-deformation` | Skins the mesh on the CPU at two clip times to verify that it moves. |

The `unmeasured` line shows the time outside every stage, for example process startup and logging.

## Record A Cutscene

`--record-splat-cutscene` loads the `.pak` file, builds a five-shot cutscene, and records it in lockstep. Each
video frame is exact for its cutscene time, so the recording does not run in real time.

```powershell
dotnet run --no-build -c Release --project XREngine.Editor/XREngine.Editor.csproj -- `
  --record-splat-cutscene `
  --pak "<output>/avatar.pak" --asset "Avatars/avatar.asset" --clip "Walk" `
  --splats 2400000 --duration 8.8 --fps 60 --backend vulkan `
  --video-encoder nvenc --ffmpeg "<ffmpeg-path>" --output-dir "<output>/cutscene"
```

Add `--triangles-only` to record the triangle reference instead of splats.

| Option | Default | Meaning |
|---|---|---|
| `--video-encoder` | `nvenc` | `nvenc` uses `h264_nvenc`. `x264` uses `libx264`. The command fails when NVENC is not available; it does not change to the CPU encoder. |

The recorder writes these files to `--output-dir`:

| File | Content |
|---|---|
| `cutscene-1080.mp4` | The master video. The captured frames go as raw pixels to the standard input of ffmpeg, so no PNG frame files are written. |
| `cutscene-1080-remote.mp4` | A copy below 2,097,152 bytes for Remote Control transfer. The recorder lowers the bit rate and tries again when a copy is too large. |
| `hero-poster-1080.png` | The last frame, taken from the read-back pixels without video compression. |
| `contact-sheet.png` | Evenly spaced thumbnails from the master video. |
| `manifest-segment-NN.json` | The capture manifest of each capture segment. |

### Read The Timing Output

The recorder prints the time of each phase:

```text
[CutsceneTiming] startup=..s, capture+masterEncode=..s, remoteEncode=..s, contactSheet=..s, total=..s.
```

The capture summary also prints `lockstep(...)`. Each `waitPerFrame` value is the average number of render frames
that a gate rejected for one captured frame:

| Gate | Waits for |
|---|---|
| `apply` | The update thread to apply the next cutscene time, the animation, and the physics chain steps. |
| `visibility` | A render frame whose visible-object package comes from a collect pass that started after the apply. |
| `gpuData` | GPU resources such as splat buffers, textures, and pipelines. |
| `readback` | A free render pipeline readback scope. |

`applyMsAvg` and `applyMsMax` show the update-thread time of one apply. A large `apply` wait with a large
`applyMsAvg` means that the animation and physics evaluation limit the capture speed.

### Speed

- Use a Release build. The recorder prints `build=Debug` or `build=Release` at startup.
- The `visibility` gate uses the visibility-collect generations of the engine timer instead of a fixed number of
  settle frames. A host without collect generations uses two settle frames, and the summary prints
  `fence=settle-frames`.
