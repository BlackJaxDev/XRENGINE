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
path remains unchanged. This is not a general custom-UI-material implementation,
and it does not add inherited clipping or change rotated hit-test geometry.

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
