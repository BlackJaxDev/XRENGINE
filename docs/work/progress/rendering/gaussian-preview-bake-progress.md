# Gaussian Preview Bake Progress

Updated: 2026-10-27. Branch: `xrengine-web-rendering`.

This note records rollout steps 1 and 2 of the gaussian preview bake: the hidden-window capture spike and the engine
core. The design is in a separate repository at
`docs/work/design/rendering/xrengine-4d-gaussian-previews-design.md`. The command is
`XREngine.Editor --bake-splat-preview`.

## Status

- Step 1 (hidden-window capture spike) is complete.
- Step 2 (engine core) is complete. The bake command writes `cache/`, four loop videos, two posters, and `report.json`
  for the avatar, item, and world fixtures. Final visual sign-off of the loops is open (see "Open Items").
- Step 4 (browser package, crossfade, LOD auto-raise) did not start.

## Environment

| Item | Value |
|---|---|
| GPU | NVIDIA GeForce RTX 4070 Laptop GPU |
| GPU driver | 610.91 |
| Render backend of the bake | OpenGL, one hidden 320 x 180 window (`Bake window visible: False`) |
| ffmpeg | `C:\ffmpeg\bin\ffmpeg.exe`, version N-119918-gee1f79b0fa-20250618 |
| ffmpeg license | GPL build (`--enable-gpl --enable-version3`). It also has `libx264`, but the bake never selects it. A production worker must use an LGPL build if GPL obligations apply. |
| Encoders used | `libvpx-vp9` (WebM), `libopenh264` (MP4), `libwebp` (posters) |
| meshoptimizer | v1.3, commit 9e1f07b159d3cb777f1c67ed31fc11fd117986f4 |

Task 1 copied nothing from the main checkout. The worktree build needed no dependency folder from it.

## Step 1 Result

The spike rendered 32 orbit views of the avatar fixture in the hidden window. 31 of 32 views show the avatar. View 0
is empty because its direction is in the plane of the flat avatar quad, so the quad is edge-on. A volumetric avatar
would show in all views.

## Step 2 Acceptance Bakes

Each bake used the default stage and the default options. The output folder is `Build/GaussianBakeOut/<kind>`.

| Value | Avatar | Item | World |
|---|---|---|---|
| Input | `avatar.pak` (skinned glTF quad, clip "Hero Action", 1 s) | `item.pak` (Corvette) | `world.pak` (Corvette, avatar, two lights) |
| Exit code | 0 | 0 | 0 |
| Splats per level | 782 / 6,250 / 50,000 | 782 / 6,250 / 50,000 | 4,688 / 37,500 / 300,000 |
| Static splats | 0 | 50,000 | 300,000 |
| Driver vertices per level | 4 / 4 / 4 | none | none |
| Frames | 32 at 32 fps, PingPong | 1, Once | 1, Once (see "Open Items") |
| Chunks | 8 | 3 | 3 |
| Chunk 0 | 12,850 B | 16,870 B | 92,161 B |
| Total | 277,682 B | 673,851 B | 3,461,511 B |
| Loop videos | 62 frames (forward and back) | 240 frames (8 s orbit) | 300 frames (10 s orbit) |
| Bake time (process) | 22 s | 4 min 14 s | 5 min 9 s |
| Bake time (steps in report) | 15.6 s | 248.8 s | 303.7 s |

Notes:

- The item load step takes 167 s. Most of this time is the read of the 88 MB archive.
- The world surface has 237 meshes, 8,729,360 vertices, and 4,487,882 triangles. The frame passes of a world sample
  all 304 frames of the full world surface, so the animate step is long (110 s). The color step of the world takes
  121 s for 64 views.
- The command loads the input before the engine starts. A cooked world that loads while the engine runs does not
  render in the bake world, and its splat colors were black.
- Consecutive decoded loop frames have a PSNR of about 29-34 dB. The step from the last frame to frame 0 has the same
  PSNR as a normal step, so the loops start again with no jump.

## Determinism

Two avatar bakes in two separate processes gave byte-identical files (SHA-256) for all eight cache files, all four
videos, and both posters. Only `report.json` is different, because it holds timings. The content hash of both bakes is
a8fdf16bbb17b8279f63262c9814ac0bf2273eba868e8d930b1c304d140b6c0c.

Two changes keep the files identical:

- The cut pass writes each splat at the slot of its node index, so the draw order of equal depths is fixed.
- The cache writer replaces the random asset ID in each cooked payload with an ID made from the content hash.

## Fixture Changes

- The item and world fixtures are regenerated with real engine materials. The Corvette has 58 textures of up to 8K.
  Their full pixels gave a cooked payload of 27.8 GB, more than the 2 GB limit. The fixture import now makes each
  texture 256 px or smaller on its longer side.
- The world fixture has the two directional lights and the ambient light of the default stage. A world bake keeps the
  lighting of the world, and with no light the loop was black.

## Open Items

- The avatar inside the world fixture does not animate in the bake. The bake reports `clip-static` and makes a still
  world cache. The clip data in the cooked world is correct, but its evaluation does not move the joints. The skinned
  mesh bones in the cooked world are detached copies that the engine rebinds when the world attaches, and the engine
  itself renders the avatar of the cooked world sheared. This is a defect of cooked worlds with animated, skinned
  prefab instances. It is not a defect of the bake. The avatar and item fixtures are not affected.
- The splat coverage leaves small dark gaps on flat surfaces at 50,000 splats, and the lit projection colors of the
  Corvette are noisy. Tune the splat size and the color projection in a later step.
- Do the final visual check of the loops and the posters in a video player.
- Do the interactive checks: no taskbar entry during a bake, and the gaussian cache in the editor viewport.
