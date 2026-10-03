# Browser quality and accessible controls

The browser uses the shared engine quality settings and interactive UI components.
This record separates source implementation from live browser acceptance.

## Quality selection

The existing low, balanced and high profiles select canvas extent/DPR limits,
shadow dimensions and cadence, light counts, texture limits and post effects.
The light limit now gates both Default forward lighting and Advanced native scene
publication before resources are acquired. Exceeding it produces a capability
diagnostic rather than silently dropping authored lights.

Disabled GTAO and bloom no longer require their cooked programs or allocate their
effect targets in either Default or Advanced. Advanced removes the AO stage from
its graph and frozen reservation sequence. Native shading uses the shared 1×1
neutral binding and returns AO=1 before any AO texture fetch. The existing
160-byte uniform layout carries the selection in its existing flags word.
All native, export, depth-comparison and MSAA shader recipes declare
`XR_ADV_AMBIENT_OCCLUSION_SCHEMA_VERSION=1`; an older artifact is rejected before
it can consume the neutral binding as a full-size image. The resource profile
captures the selection, so a changed profile requires resource replacement.
Authored camera settings are retained, and desktop shader behavior is unchanged.

## Accessible controls

The browser projects up to 256 shared engine buttons, checkboxes and textboxes
into ordered DOM controls. Explicit accessible labels fall back to engine names;
checkbox state includes mixed values, and textbox roles preserve readonly and
multiline state. The projection follows active canvases linked to the local pawn,
uses clipped engine bounds, and routes focus and activation through the existing
canvas input owner. Unsupported widgets are not assigned invented roles/actions.

Focused text uses the existing native editor for browser text entry, selection
and IME. Control and text generations reject late events after removal, clipping,
focus transfer or teardown. Native keyboard events do not bubble into the proxy's
synthetic engine-key handler. DOM reconciliation preserves the focused editor
and composition when sibling controls move. Recycled entries clear references
to removed engine objects. Traversal is bounded and uses retained storage.

Independent source review corrected and rechecked focus ownership, cross-canvas
transfer, stale editors, pooled references, hidden-control actions and DOM
reordering. The combined WebGPU and native-WASM Browser builds pass with zero
warnings or errors. JavaScript syntax and diff checks pass. Physical Tab and
Shift+Tab traversal, typing/selection/IME, screen-reader output, and repeated
control removal/recreation still require live browser acceptance.

## Shadow helper correction

At `37e197f`, both Chromium CI and a physical Intel browser reject the native
shadow helper: generated WGSL passes private invocation-cache variables to
function-address-space out parameters. The correction reconstructs into local
vectors, then copies both successful helper results into the invocation cache.
It preserves quad orientation, sample offsets, material-normal evaluation and
the derivative algorithm. All eight native shader variants cook successfully;
inspection confirms function-local pointer arguments and cache publication only
after the failure guard. This is emitted-source evidence, not a new rendered
shadow result. The earlier `71684f` physical material run remains valid.

The subsequent `feca3bcc` run passes WGSL validation. On Chromium software Vulkan,
native compute pipeline creation remains pending for 42.94 seconds at the
unchanged 45-second first-frame deadline; validation and memory scopes both
finish successfully after 15.2 milliseconds. This isolates the pending native
pipeline operation from error-scope handling.

A reviewed source simplification evaluates the two ordered shadow helper axes
through one retained two-iteration loop. Both evaluations still run when the
first fails, and cache publication still requires both to succeed. Expanded
surface-reconstruction calls fall from five to three and texture-pair resolution
calls from 43 to 31. All eight native/depth/MSAA/export recipes retain the same
ABI and binding contracts. These structural measurements do not establish an
actual pipeline compilation speedup.

Physical Intel testing of `feca3bcc` renders the ON/OFF caster/receiver fixtures
through three startups and resize checks. Directional 256×256 depth records
three caster draws with GPU completion; shadow-enabled output darkens 4,929
receiver pixels initially and 2,409 after resize outside the foreground
occluders. This establishes visible directional occlusion in that fixture, not
exact PCSS parity. Point shadow targets unexpectedly remain 1024×1024 despite
authored 256×256 values, and all six completed faces contain no caster draws.
That point-resolution and collection defect remains under correction; a cleared
cube is not accepted as point-shadow feature validation.

The subsequent [point restoration fix](../rendering/browser-point-shadow-restoration-2026-10-03.md)
separates authored dimensions from physical cube sizing, defers runtime camera
construction until property notifications are active, aligns collection with
the six-face producer contract, and releases point-owned helper objects.
It passes 121 managed checks and six repeated lifecycle cycles; physical
point-shadow pixels remain pending. The measured remaining base-light command
list is recorded separately.
