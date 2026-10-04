# Browser UI Parity

This saved engine world exercises shared controls, bitmap glyphs, solid/image
quads, inherited clipping, possessed-player input and one owned offscreen world
canvas. Both desktop and browser launchers load `BrowserUiParityWorld.asset`
through `Engine.Assets` into the ordinary `RuntimeWorld`. There is no substitute
DOM UI, scene DTO, renderer, diagnostic input route or game-specific JS export.

## Authored and runtime content

The world saves the Default pipeline, a fixed orthographic camera, its pawn
alias, both canvases and their `UICanvasInputComponent.OwningPawn` aliases, all
control/text nodes, geometry bounds, source text and game-mode marker
`shared-ui-offscreen-input-v1`. The game mode verifies those aliases, reconnects
normal input subscriptions after hydration, possesses player one, binds the
screen canvas to the camera and installs ordinary button/text callbacks.

Panel colors and property bindings are normal game initialization. The shared
`UIToggleComponent` edits `ClippingEnabled` through its public property. Both
image nodes have a sample component that constructs a real `XRTexture2D(2, 2,
rgba)` and calls `UIMaterialComponent.CreateImageMaterial`. That image is runtime
construction, not authored image-payload serialization coverage. Its explicit
RGBA8/sRGB-role, nearest/clamp setup does not modify a cooked texture format.
The image/material are detached and released when play ends. No new image
codec, font dependency, custom cooked payload or serialization codec is added.

The offscreen canvas uses the shared `PreRender` producer, owned framebuffer
and transparent scene quad. Its target is transparent; empty regions show the
world clear color. The world-space placement is explicit, not automatic camera
placement. The camera uses a 1280 x 720, bottom-left orthographic extent at
(0, 0, 1000), looking along -Z. Exposure is fixed at 1, gamma is 1, Mobius
transition is 0.6, and AO, bloom, vignette and depth of field are disabled.
This deliberately uses the currently admitted Mobius output route.

## Build and stage from a source checkout

Use the repository's pinned SDK in `global.json`, its `wasm-tools` workload,
PowerShell, existing pinned Slang setup and reviewed browser Jolt staging.
Follow [browser publishing](../../docs/work/progress/rendering/browser-project-publishing.md)
for those existing prerequisites. Run from the repository root:

```powershell
dotnet build Samples/BrowserUiParity/BrowserUiParity.csproj -c Release
pwsh Samples/BrowserUiParity/Prepare-BrowserShaders.ps1
dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU
dotnet run --no-build --project XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- --build-project Samples/BrowserUiParity/BrowserUiParity.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder BrowserWebGPU
```

The shader preparation uses the existing complete engine recipe catalog,
including UI quad/image/bitmap V2 and `UICanvasSurfaceV1`; generated artifacts
are ignored. The normal publisher stages and verifies the managed game/player,
shader catalog, content and license output before activating its output folder.
Serve that generated site through the existing browser player workflow. Do not
point a harness at the raw source `.asset` or fabricate a cooked manifest.

Keep the checkout's ordinary `Build/CommonAssets` tree available to the Editor.
The actual font path is `Build/CommonAssets/Fonts/Roboto/Roboto-Regular.ttf`,
with `Build/CommonAssets/Fonts/Roboto/LICENSE.txt`. `PrepareBrowserUiFonts`
discovers the authored `UITextComponent` nodes; `CookBrowserDefaultUiFont`
uses the existing FreeType cooking rasterizer to produce the bitmap atlas and
metrics. The publisher packages `/engine/Fonts/Roboto/Roboto-Regular.cooked.asset`
as an essential asset plus `licenses/Roboto-LICENSE.txt`. Browser startup preloads
that font before activating text. Keep the default UI font configured to this
canonical Roboto source and 128-pixel layout em. This sample does not change the
separately pending installed-Editor CommonAssets packaging layout.

## Browser observation and interaction contract

Use a 1280 x 720 canvas for the reference capture. Coordinates below are engine
bottom-left canvas pixels; browser pointer Y is `720 - y` at this size. Account
for the actual canvas CSS rectangle and device pixel ratio. Resize acceptance
should re-query the engine-projected control bounds instead of reusing these
reference coordinates. The screen controls use fixed pixel bounds while the
world view retains its authored orthographic extent.

Observe ordinary accessibility controls through the player's existing
`GetAccessibleControl*` exports or native proxies. Initial traversal has 17
controls, in saved hierarchy order, with the world canvas after the screen
canvas. The readonly status textbox's name and text are both
`Actions: 0; Submits: 0; Cancels: 0`. It is updated only by the actual shared
control callbacks. Wait for the resulting status and semantic revision after
each interaction. Reading a status/export is evidence of observation; invoking
a managed action directly is not evidence of pointer/keyboard delivery.

| Accessible name | Role and initial state | Reference bounds (x, y, w, h) | Action |
| --- | --- | --- | --- |
| Actions: 0; Submits: 0; Cancels: 0 | Readonly single-line textbox | 32, 610, 552, 30 | Reports exact delivered action counts |
| Count Action | Button | 32, 548, 240, 40 | Adds one action |
| Clip enabled | Checkbox, checked | 304, 548, 280, 40 | Changes actual parent clipping and adds one action |
| Single Line | Editable single-line textbox, `single seed` | 32, 484, 552, 40 | Enter adds one submit and action; Escape adds one cancel and action |
| Multiline | Editable multiline textbox, `first line\nsecond line` | 32, 400, 552, 64 | Newlines and edits change text; no action count |
| Read Only | Readonly single-line textbox, `readonly seed` | 32, 348, 552, 36 | Rejects text edits |
| Rename Target | Button | 32, 292, 168, 36 | Toggles the target name/label to `Renamed Target` and back |
| Hide Target | Button | 216, 292, 168, 36 | Toggles target transform visibility |
| Deactivate Target | Button | 400, 292, 184, 36 | Toggles the target button component's active state |
| Reparent Target | Button | 32, 244, 168, 36 | Moves the same target between its two saved parents |
| Remove Target | Button | 216, 244, 168, 36 | Deactivates and detaches the target node |
| Restore Target | Button | 400, 244, 184, 36 | Reattaches the retained target at Home; restores name, visibility and active state |
| Clipped Action | Button, solid red, no rotated glyphs | Visible: 152, 144, 80, 60 | Adds one action only inside the visible crop |
| Mutation Target | Button | Home: 328, 168, 248, 48 | Adds one action |
| Move Clip | Button | 32, 72, 128, 36 | Toggles clip-parent X between 32 and 56 |
| Rotate Clip | Button | 176, 72, 128, 36 | Toggles clip-parent rotation between 0 and 15 degrees |
| Offscreen Action | Button | 896, 216, 240, 64 | Adds one action through the world canvas's actual pawn-owned input |

Every mutation button activation adds exactly one action, even if its requested
state is already present. Text edits do not add actions. The checkbox publishes
its shared `LastState` on its normal late tick; await `aria-checked` rather than
assuming an immediate DOM update. Pointer/touch, Enter/Space activation, Tab,
focus transfer, IME composition and selection must use the normal player input
bridge. Synthetic IME event delivery cannot establish physical IME acceptance.

The clip parent is (32,124,200,100). Its red child is locally (120,20,160,60),
so the original right half, global X [232,312), has no pixels or hit response.
There is no text under the rotating parent; the shared button retains its
accessible name. Rotation uses the engine's rectangular ancestor scissor and
inverse-transform target bounds, not a polygon clip. Reparenting moves the
target to (328,80,248,48), after the Move/Rotate controls in traversal order.
Hiding, deactivating and removal exclude it from accessibility traversal;
deactivation leaves its separate background/label visible. Restore admits a
fresh proxy generation after removal. Removal retains the node for deterministic
reinsertion; it is not a destructive node-disposal test.

## Pixel witnesses

The owned world canvas is (672,96,512,512). Its four 64 x 64 corners are red at
bottom left, green at bottom right, blue at top left and yellow at top right.
Inspect their centers to detect X/Y mirroring. None overlaps the screen canvas.
The world clear is linear RGB (0.125,0.25,0.375). Transparent gaps must preserve
that scene background through the normal output transform.

Inside the world canvas a 50%-alpha red quad occupies (96,256,192,128), followed
by a 50%-alpha blue quad at (192,256,192,128). Away from edges/text, the expected
linear HDR colors before output are `0.5R + 0.5Bkg`,
`0.5Blue + 0.25R + 0.25Bkg` in their overlap, and
`0.5Blue + 0.5Bkg`. Apply the actual Default Mobius/gamma output contract when
checking presented pixels; do not equate screen UI display RGB with scene HDR.
This composition detects double alpha multiplication and wrong render-target
orientation without reading or painting an alternate canvas.

The 2 x 2 image appears at screen (32,24,96,40) and world-local
(64,96,128,128). Its row at v=0 is opaque red/green; v=1 is opaque blue and
yellow with alpha 128/255. Sampling is nearest. Compare interior quadrant
orientation in both routes and allow normal output differences. White headings
and control text exercise the real cooked bitmap glyph path, including clipped
text fields. Do not infer glyph acceptance from solid rectangle pixels alone.

## Validation boundary

This sample is source-staged. Managed build, Editor load/save/cook, complete
browser publication, GPU pixels, interaction, restart/resource retirement and
desktop captures remain pending in an environment with .NET and a browser.
No test was added or run as a substitute for those live gates.

Source review found a normal-publishing gap: desktop `UIMaterialComponent`
defaults create `UnlitColoredForward.fs` with UI state, which the scene-material
projection rejected. The shared browser projection now recognizes exact
UI-only solid consumers before scene classification, preserving shared material,
parameter and raster-state identities in its source-free target. Mixed scene/UI
profiles and arbitrary callbacks reject explicitly. The sample keeps its normal
shared materials and introduces no fixture codec. Compilation, successful
authored cooking/fresh hydration and runtime captures remain required before
this becomes a shipping parity witness.
