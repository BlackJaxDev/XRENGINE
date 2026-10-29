# Authored sky publication

Date: 2026-10-29
Status: Saved component cooking and software shipping-player checks passed.

## Gap and implementation

The earlier sky diagnostics load saved textures, but construct their sky
component in the browser. They do not establish that a saved component graph
survives the production project publisher.

RenderingParity now saves an equirectangular sky component with an embedded
2×2 RGBA16F texture. The native serializer produced the existing cooked-binary
YAML payload; no new codec was added. Explicit camera post-processing makes
the two visible neutral texels predictable. Existing meshes, maps, skeleton,
morph target, game mode and pawn/camera alias remain unchanged.

The standalone publisher initially lacked shader services for the saved
pipeline and rendering asset access for the sky's desktop material constructor.
It now installs `RuntimeCallerThreadRenderingBootstrap` with empty renderer and
physics catalogs. This reuses the shared shader/file services without starting
a renderer. The production export still loads the canonical desktop graph and
performs its normal browser projection and capability audit. The optional
`rendering-parity` driver argument leaves the original three-argument
RollingBall profile and guards intact. The compiled game registers its types
before the Editor loads the world; the driver does not preload RenderingParity.

## Evidence

- Canonical Slang cooking produced 184 shader artifacts. The production
  source-checkout Editor build path completed game compilation, world cooking,
  native WebAssembly publishing, content packaging and output activation.
  RenderingParity contains 365 assets, 181 shaders and 84 pipeline artifacts.
  Both canonical-stage and publisher-copy checks preserved all 343 input files.
- A separate native read of the final published world payload preserved the
  sky ID `5f99c9fb-395c-4243-b1bc-23f211f7da02`, texture ID
  `5f99c9fb-395c-4243-b1bc-23f211f7da01`, all 32 pixel bytes, texture mode,
  equirectangular projection, intensity, nearest filters and zero LOD clamps.
  The published world payload SHA-256 is
  `beaf219fc792658119c6b4920711a55bf5a59623f1a6b12043fde773d35040a9`.
- Chromium 154 with explicit SwiftShader passed the shipping-player check in
  two fresh contexts. All 16 sky regions (400 pixels) matched the independent
  RGB 70/157 reference exactly, including both resizes. The check permits a
  two-byte tolerance.
  The captures were viewed. Both mapped surfaces, animation, pause, reset,
  resume, focus and canvas resize also passed. Neutral sky pixels cannot
  satisfy the foreground color-spread or animation-difference assertions.
- A separate review decoded the saved Zstd texture payload and independently
  confirmed its four half-float texels and the display reference calculation.
- The unchanged three-argument RollingBall invocation also completed production
  publication with all 493 staged inputs unchanged. Its full catalog contains
  365 assets, 181 shaders and 84 pipeline artifacts. An initial staging attempt
  created an empty `Metadata` directory that the source project does not have.
  Generated metadata correctly failed the copy guard. Removing only that empty
  staging directory restored the canonical layout; no guard was weakened.
  The RollingBall shipping-player checks then passed in two fresh contexts,
  including tilt, reset, pause/resume, resize and local delivery.
- All 24 build/publication gates passed with zero compiler warnings or errors:
  four desktop targets, nineteen browser-target portable projects and native
  WebAssembly publication. The publisher driver also builds without warnings.
  Fresh fixture cooking retains 57 shaders, fourteen passes and 119 assets;
  all seven existing sky payloads remain byte-identical. The closure scan and
  bundle dependency, native WASM, Jolt pin and notice checks pass.
- Full software WebGPU regression passed 24 selected checks, including 356 sky
  cases and the strict indirect rendering checks. Three optional checks were
  skipped: diagnostic-bundle autostart, its absent game package, and the
  standalone Jolt spike. Both game packages passed separately. The nine served
  runtime/player files checked across all three outputs match repository source.

## Limits

The sky is embedded in the world graph; this does not establish a separate
texture catalog dependency. The display check covers the visible left-column
texels, not every panorama direction, HDR framebuffer alpha, automatic cycling,
lighting synchronization or resource teardown. The native read covers all four
texels. The wider projection/filtering checks remain separate.

This run invokes the compiled Editor builder from a source checkout. Packaged
Editor CLI/UI publication, physical GPUs, desktop pixel comparison and complete
shader/cooking coverage remain open. The explicit post-processing settings
change this sample's baseline; future desktop comparisons must use the same
saved world. The frozen reference runtime and separate unpublished manifest
work remain unchanged. No full backlog requirement receives completion credit.
