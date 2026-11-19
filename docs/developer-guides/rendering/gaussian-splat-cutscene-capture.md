# Gaussian Splat Cutscene Capture

This guide explains how to show a Unity avatar as live Uber-shaded Gaussian splats in a saved scene, and how to record
any saved cutscene to a video. Use it when you make a splat video, or when a cook or a recording is slow.

The engine has no built-in video content. A saved world holds the avatar, the cameras, the overlays, and a
`CutsceneDirectorComponent` with its `CutsceneAsset`. The recorder plays that cutscene and writes the frames. A helper
script can build the world through the editor MCP tools. `Tools/Cutscenes/New-SplatShowcaseCutscene.ps1` is a sample
that builds a five-shot avatar showcase.

## Cook A Unity Prefab

`--cook-unity-prefab` imports a Unity prefab, binds an optional humanoid clip, and writes a `.pak` archive.

```powershell
dotnet run --no-build -c Release --project XREngine.Editor/XREngine.Editor.csproj -- `
  --cook-unity-prefab `
  --unity-project "<unity-project>" `
  --prefab "<unity-project>/Assets/<path>/Avatar.prefab" `
  --anim "<unity-project>/Assets/<path>/Walk.anim" `
  --output-pak "<output>/avatar.pak"
```

Without `--asset`, the archive entry is `Avatars/<scene-root-name>.asset` with `--scene-root`, or `Avatars/<prefab-file-name>.asset` without it. Add `--asset "Avatars/<name>.asset"` to
choose another entry.

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

## Show An Avatar As Splats

Add a `GaussianAvatarSplatComponent` to the root node of the avatar. When the component activates, it samples
`TargetSplatCount` splats over the active triangle meshes under its node. When `HideSourceMeshes` is true (the default),
it stops those meshes from drawing and casting shadows. Deactivating the component draws them again.

| Property | Default | Meaning |
|---|---|---|
| `TargetSplatCount` | 2,400,000 | The number of splats over all sampled meshes. |
| `SampleOnActivate` | true | Samples in an app-thread task after the activation, so the model components under the node have built their runtime meshes first. Call `SampleOwnHierarchy` to sample again. |
| `HideSourceMeshes` | true | Hides the sampled triangle meshes. |

The splats follow the skinned pose of the meshes in each frame. An animation clip track or a physics chain that moves the
bones also moves the splats.

A baked cache plays with `GaussianCacheComponent`. Set `CacheAssetPath` to the root asset of the cache, and the
component loads it when it activates. Drive `PlaybackTimeSeconds` with a property track.

## Record A Cutscene

`--record-cutscene` loads a saved world, opens the cutscene of one director, and records it in lockstep. Each video
frame is exact for its cutscene time, so the recording does not run in real time.

```powershell
dotnet run --no-build -c Release --project XREngine.Editor/XREngine.Editor.csproj -- `
  --record-cutscene `
  --project "<project>/<name>.xrproj" `
  --world "<project>/Assets/Worlds/<world>.asset" `
  --director "<root-node>/<director-node>" `
  --backend vulkan --video-encoder nvenc --ffmpeg "<ffmpeg-path>" --output-dir "<output>"
```

| Option | Default | Meaning |
|---|---|---|
| `--project` | none | The authoring project that owns the assets of the world. |
| `--world` | required | The world asset file. |
| `--director` | the only director | The node path (from a root node, with `/`) or the node name of the director. |
| `--output-dir` | required | The folder for the output files. |
| `--backend` | `vulkan` | `vulkan` or `opengl`. |
| `--width`, `--height` | 1920, 1080 | The frame size. |
| `--fps` | 60 | The frame rate. Frame `n` shows the cutscene at `n / fps` seconds. |
| `--duration` | asset duration | The recorded length in seconds. |
| `--poster-time` | last frame | The cutscene time of the poster frame in seconds. |
| `--video-encoder` | `nvenc` | `nvenc` uses `h264_nvenc`. `x264` uses `libx264`. The command fails when NVENC is not available; it does not change to the CPU encoder. |
| `--optimized-max-bytes` | 0 | The size limit of the optimized file size copy. 0 writes no copy. |
| `--contact-sheet` | `true` | `false` writes no contact sheet. |
| `--physics-substeps` | 2 | The number of steps between two frames. Each step advances the cutscene and ticks the physics chains once. |
| `--ffmpeg` | `ffmpeg` | The ffmpeg executable. |
| `--max-render-fps` | 30 | The highest number of render frames per second. 0 removes the limit. |
| `--max-gpu-busy` | 0.5 | The highest fraction of time that the recorder keeps the GPU busy, from 0.05 to 1. 1 removes the limit. |
| `--gpu-priority` | `below-normal` | The Windows GPU scheduling class: `below-normal` or `normal`. |

`CutsceneRecordSettings` holds the same values, and also the file names, the warm-up frame counts, the settle steps, and
the capture render overrides.

The recorder writes these files to `--output-dir`:

| File | Content |
|---|---|
| `cutscene.mp4` | The master video. The captured frames go as raw pixels to the standard input of ffmpeg, so no PNG frame files are written. |
| `cutscene-optimized.mp4` | Only with `--optimized-max-bytes`: a copy below that size. The recorder lowers the bit rate and tries again when a copy is too large. |
| `poster.png` | The poster frame, taken from the read-back pixels without video compression. |
| `contact-sheet.png` | Evenly spaced thumbnails from the master video. |
| `manifest-segment-NN.json` | The capture manifest of each capture segment. |

### What The Recorder Changes

The recorder changes only capture settings. It does not add content.

- It limits its GPU load, so the PC stays responsive. The recorder does not run in real time, so without limits it
  would keep the GPU busy all the time. It caps the render rate at `--max-render-fps`, runs at the BelowNormal GPU
  scheduling class, and waits for the GPU at the end of each render. The engine timer then measures the GPU time of
  each frame and waits after it, so the GPU is busy for at most `--max-gpu-busy` of the time. These limits do not
  make one long frame shorter: a frame that takes 200 ms of GPU time still blocks the desktop compositor for that
  time.
- It pauses the world. Between two frames, it advances the cutscene in `--physics-substeps` equal steps and ticks the
  physics chains after each step.
- At a jump (the first frame) and at each camera cut, it resets the physics chains to the current pose and runs
  `SettleSubstepsAfterDiscontinuity` (default 30) steps, so the chains hang under gravity before the frame shows.
- It runs each physics chain on the CPU in discrete fixed steps with no sleep and no distance culling, so two recordings
  give the same chain motion.
- It renders every camera with the offscreen capture pipeline, no anti-aliasing, no bloom, and a fixed exposure of 1.
- It gives the first screen-space `UICanvasComponent` of the world to the viewport and to every camera.
- It requests every material texture at full resolution and keeps it resident for the whole recording.
- Before the capture, it renders a frame inside each camera cut clip and waits until the world is ready, so the
  programs and textures of each shot load before the first captured frame.

A component that can be incomplete for some frames implements `IRenderCaptureReadiness`. The recorder captures a frame
only when each such component in the world is ready. `GaussianAvatarSplatComponent` and `GaussianCacheComponent`
implement it.

### Read The Timing Output

The recorder prints the time of each phase:

```text
[CutsceneTiming] warmup=..s, capture+masterEncode=..s, optimizedEncode=..s, contactSheet=..s, total=..s.
```

The capture summary also prints `lockstep(...)`. Each `waitPerFrame` value is the average number of render frames
that a gate rejected for one captured frame:

| Gate | Waits for |
|---|---|
| `apply` | The update thread to apply the next cutscene time and the physics chain steps. |
| `visibility` | A render frame whose visible-object package comes from a collect pass that started after the apply. |
| `gpuData` | Textures, programs, and each `IRenderCaptureReadiness` component. |
| `readback` | A free render pipeline readback scope. |

`applyMsAvg` and `applyMsMax` show the update-thread time of one apply. A large `apply` wait with a large
`applyMsAvg` means that the cutscene and physics evaluation limit the capture speed.

### Speed

- Use a Release build. The recorder prints `build=Debug` or `build=Release` at startup.
- The `visibility` gate uses the visibility-collect generations of the engine timer instead of a fixed number of
  settle frames. A host without collect generations uses two settle frames, and the summary prints
  `fence=settle-frames`.

## Build A Cutscene With A Script

The editor MCP server has tools that build a cutscene asset from a script:

| Tool | Work |
|---|---|
| `cutscene_create_asset` | Creates an empty cutscene asset and assigns it to the director of a node. |
| `cutscene_add_track` | Adds a track with its clips and curves: transform, camera cut, camera property, property, animation clip, or simulation control. |
| `cutscene_save_asset` | Saves the cutscene asset of a director. |
| `setup_humanoid_avatar` | Adds a `HumanoidComponent` to an imported avatar, maps its bones, and returns the node that holds it. Animation clip tracks and humanoid bounds use that node. |
| `get_node_bounds` | Returns the world bounds of the static meshes under a node, or of its humanoid skeleton with `source = humanoid`, so a script can place cameras relative to an object. |

The scene tools (`create_scene_node`, `add_component_to_node`, `set_component_properties`, `set_node_transform`, and
`save_world`) build the rest of the world. `Tools/Cutscenes/New-SplatShowcaseCutscene.ps1` uses these tools in an
isolated editor session. It computes orbit camera paths around the avatar bounds, writes the camera, animation clip, and
overlay tracks, saves the world, and then calls `--record-cutscene`. All values of the video are parameters or data in
the script.

`import_third_party_asset` imports the avatar. For an avatar that exists only as a prefab instance in a `.unity` scene,
pass the scene file and `scene_root` with the name of the top-level avatar object; the script has `-SceneRoot` for this.
Select the plain avatar object, not a variant that adds locomotion, face tracking, or other sample systems. Add
`allow_partial_import` (script: `-AllowPartialImport`) when the Unity project is missing some textures or materials.

The Unity import creates the bones of an avatar, but no humanoid. Call `setup_humanoid_avatar` after
`instantiate_prefab`. It finds the source FBX file in the import record of the prefab, reads the humanoid bone names
from the Unity settings of that file (`.fbx.meta`), and stores the file hash to identify the skeleton. The tool fails
when it finds no FBX file; pass `source_model_path` (script: `-SourceModel`) in that case. The cook command does the
same setup in its `attach-humanoid-clip` stage.
