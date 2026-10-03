# Browser UI clip commands and focused input bridges

Date: 2026-10-02

## Engine-owned UI rendering state

The engine-frame command format now uses version 3, with 112-byte draw records.
The prior 80-byte records had no unused space for viewport and crop state. C#
resolves viewport/scissor values into the reusable command arena; JavaScript
validates their bounds and executes them. Clip motion does not rebuild retained
mesh descriptors, alter material binding keys, or introduce per-draw interop.
The viewport/crop conversion changes the Y convention once at the engine/backend
boundary, and the crop is intersected with both viewport and attachment bounds.

Each operation has a separate pass, so an absent viewport or scissor cannot
inherit the previous operation's state. Zero-area crop records remain in the
packet to preserve resource-upload ordering, but the executor does not draw them.
Managed and JavaScript draw counters and framebuffer-production tracking exclude
those clipped draws. Partial scissored attachment clears remain explicitly
unsupported rather than clearing the entire attachment silently.

`ClipToBounds` quads and bitmap text can now remain in the engine UI batch path.
A crop-keyed run carries a double-buffered value snapshot; the desktop unbatched
path remains unchanged. This is not a general custom-UI-material implementation.
Inherited clipping and rotated hit bounds are addressed by the later shared UI
change described below.

Independent review closed an upload-ordering issue in the initial empty-crop
implementation. The final WebGPU graph compiled with zero warnings/errors;
JavaScript syntax, ordered-upload and zero-area command probes pass. Actual
browser UI crop pixels and full hit-test parity remain acceptance work.

## Focused text and button bridges

The existing browser input leaf feeds shared engine devices and player mappings
from pointer, touch, keyboard, wheel and standard Gamepad events. The focused
text bridge adds browser IME/composition and selection behavior without replacing
the engine's text component. A native DOM button proxy exposes an engine-focused
button's accessible name and activation to the browser. Enter/Space activation
does not also generate a duplicate engine key activation; nonactivation keys,
including Escape and arrows, still reach shared input. Tab keeps browser focus
navigation available.

Both bridges use focused-target identity and generation checks. Text edits,
selection changes and actions additionally carry an expected content version,
so a stale DOM event cannot overwrite a managed edit made between frames.
Pending IME commit suppresses Enter/Escape until the deferred commit is applied.
Removing a proxy restores canvas focus and clears held keys. Hidden proxies are
marked inaccessible and removed from tab order before focus transfer; stop and
restart epochs reject late events and listeners are aborted on teardown.

Accessible labels have a cached value and independent numeric revision. The
unchanged label string does not cross the managed/JavaScript boundary every
focused frame. Label changes do not masquerade as text-content changes or reset
selection. Independent review, JavaScript syntax checks and focused production
method probes pass. The coherent full compile/publish gate records final managed
compilation separately in the current checkpoint.

These are focused-control bridges, not a complete semantic accessibility tree.
Screen-reader traversal of the whole canvas, nonfocused controls, inherited UI
clipping, all widget roles/states and physical IME/assistive-technology behavior
remain open. No browser/device acceptance or general engine-UI completion is
implied by the source and boundary checks.

## Shared inherited clipping follow-up (2026-10-03)

The shared UI crop resolver now intersects a component's rectangular crop with
clipping ancestors. Both the batched quad/text path and the unbatched desktop
2D command use this resolved crop. Transform bounds use all four corners when
forming the axis-aligned scissor for rotated elements; rotation does not make
this a polygon clip. The renderer consumes bottom-left canvas-local pixels and
converts their Y coordinate once at its backend boundary. Canvas input uses the
same crop to reject mouse and contact hits outside visible descendants, after
testing the target's local bounds through its inverse transform. Input blockers
therefore only act where their clipped UI is visible. These paths reuse their
existing input collections and avoid per-hit snapshot arrays.

The canvas-to-local matrix now composes canvas world before the element's
inverse world matrix, which is the inverse of the existing local-to-canvas
matrix under `System.Numerics` row-vector multiplication. The corresponding
parent-to-child conversion uses the same order. A rotated, translated canvas
with a translated child now round-trips a local point through canvas space;
the previous order failed when those transforms did not commute. Hit testing
compares the canvas point directly with the renderer's canvas-local scissor.

Live screen-space acceptance should cover nested clipping and moving/rotated
ancestors on desktop OpenGL/Vulkan and browser WebGPU: a child protruding beyond a clipping
parent must have neither pixels nor hover/click/touch response in the cropped
area, while its visible part remains interactive. Toggle the ancestor's clip,
move it, and confirm the crop updates without recreating the child. The browser
glyph and image batch paths also need actual pixel review. Full canvas
accessibility traversal and physical IME behavior remain separate acceptance.

## Shared offscreen coordinate correction (2026-10-03)

Camera/world canvases previously submitted their 3D world matrices and world
crop rectangles to a local 2D camera and pixel target. Their 2D input-tree bounds
were also left unset, and the world-pointer boundary compared an already-local
point with a translated rectangle. A translated or tilted canvas could therefore
render outside its texture or reject a hit that lay inside its local content.

The shared 2D path now removes the owning canvas's world placement from material
quad and text matrices, in both batched and individual commands. Direct 3D
commands retain their world matrices. Clip and input-tree bounds transform all
four element corners into the same canvas before forming the AABB; transforming
a previously flattened world AABB would lose the orientation of a tilted canvas.
The input tree and world-pointer boundary use a zero-origin local canvas extent.
Screen canvases retain their identity world placement and existing crop values.
No shader, desktop material, or binding contract changes are involved.

The ordinary UI hierarchy composes local matrices up to the owning canvas when
forming these bounds. This avoids a floating-point world/inverse round trip
shifting an exact integer clip edge by one pixel as the canvas tilts or moves.
The targeted Runtime.Rendering/InputIntegration build passes with zero warnings
or errors. An ignored runtime probe passes 77 checks through the actual shared
crop resolver, input broad phase, local hit tests, individual model commands,
and material/text batch snapshots. Screen, translated and tilted world, and
camera-space fixtures all retain the exact `(40, 30, 100, 80)` parent scissor;
the previous world rectangle instead starts at `(815, -205)` in the translated
fixture. Nested clipping, clip toggles, empty intersections and rotated children
are covered. This is a managed execution witness, not GPU pixel acceptance.

The browser non-screen rejection remains intentional. Browser UI variants still
admit only the display target, and the existing world-canvas composition material
uses desktop GLSL. Completing this path requires admitting the actual offscreen
attachment, a cooked world-composition material, and a defined alpha/color-space
contract across the offscreen and scene passes. The generic WebGPU framebuffer
resource and binding implementation already exists. Coordinate validation does
not establish browser offscreen execution or physical-device clipping parity.
