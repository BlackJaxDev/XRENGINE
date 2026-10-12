# Engine virtual input controls

`UIVirtualStickComponent` and `UIVirtualButtonComponent` are shared engine UI
components in `XREngine.Runtime.InputIntegration`. They use the same
`UIBoundableTransform`, canvas projection, input-blocking hierarchy and render
ordering as ordinary engine interactables. Compose their artwork with
`UIMaterialComponent`, `UITextComponent` and ordinary child UI nodes.

## Authoring and mappings

Place controls beneath the pawn's canvas and link its `UICanvasInputComponent`
to that pawn through `OwningPawn`. Only that canvas's owning local controller
can capture the controls. A detached, hidden or inactive canvas does not route
input. Controls do not discover a global player or invoke gameplay methods.

- The stick defaults to `LeftThumbstickX` and `LeftThumbstickY`. Its center is
  the center of its arranged bounds; its radius is half the smaller dimension.
  `DeadZone` is radial, defaults to `0.12`, and rescales the remaining travel
  to the unit circle. Positive Y points upward. Configure `HorizontalAxis`
  and `VerticalAxis` to target other existing gamepad axes. Trigger outputs
  clamp negative values to zero. The ordinary input device's own axis policy
  still applies after this UI mapping
- The button defaults to `FaceDown`. Configure `Button` to target another
  existing logical gamepad button. It stays held until its captured contact
  ends, including when the contact leaves the button's bounds
- `Value` on a stick and `IsPressed` on either control are transient polling
  properties; they do not publish per-frame property-change notifications
- Existing pawn `RegisterAxisUpdate`, `RegisterButtonEvent` and input-state
  queries receive these values. No browser-specific pawn registration is needed

The physical gamepad and virtual controls share the owning controller's logical
gamepad. Buttons combine with OR. Each axis takes the greater absolute value,
with the physical axis winning ties. Values are not added, and a virtual
release cannot release a still-held physical button. Synthesized callbacks
do not activate a separately focused canvas button or its back-navigation
handler. Physical gamepad navigation keeps its existing behavior.

## Pointer routing and desktop parity

The browser bridge sends generic touch identity, phase and canvas backing-pixel
coordinates. There are no page-authored sticks, action names or gameplay calls.
Each virtual control owns at most one contact. Different controls can hold
simultaneous contacts, allowing a stick and jump button to operate together.

The first touch not claimed by a virtual control follows the existing engine
mouse-backed UI route, retaining normal widget input registration. Other
unclaimed simultaneous contacts are ignored. A claimed virtual touch does not
also produce a mouse click. Real mouse coordinates/buttons retain priority,
and a desktop left-mouse drag over a virtual control follows the same capture
and mapping implementation. Physical mouse input is still available to its
existing gameplay registrations; the canvas avoids treating the same virtual
control drag as a separate UI click.

The primary viewport stream is consumed only by local player one, matching the
existing keyboard/mouse/gamepad host contract. This does not create additional
local players or route primary contacts into another player's canvas.

## Cancellation and bounded storage

Contact storage supports ten simultaneous host contacts and 128 queued
transitions. Adjacent moves from the same contact coalesce without reordering
begin/end events. A transition overflow cancels the whole contact generation;
old moves cannot start a new gesture. Duplicate begins and excess simultaneous
contacts are rejected. A complete button tap between engine frames survives as
a one-tick press; a cancelled contact does not create that pulse.

The controller holds eleven capture slots (ten contacts plus the mouse), a
sixteen-canvas scratch buffer and a 256-hit scratch buffer per canvas. Hit-buffer
or canvas-buffer overflow fails closed. The new contact queue, span-based
quadtree query, capture routing and virtual gamepad aggregation use fixed or
stack storage in steady state. Existing keyboard/text snapshot allocation
behavior is unchanged.

Page focus/visibility loss, composition reset, pointer cancellation or capture
loss, input-source replacement, surface generation/extent changes,
viewport/pawn/controller reassignment, and UI input capture cancel gestures.
Ownership changes record a source-sequence cutoff; queued older begins cannot
capture a replacement pawn. This does not drain or reset another viewport's
shared contact source, and an ownership change inside a callback stops replay.
Hidden, deactivated or reparented controls are released when sampled; mapping
changes also cancel that control. Focusing another regular UI widget releases
virtual gameplay controls while preserving the normal widget's touch gesture.
Cancelled virtual contributions are released even while ordinary input is
being withheld for UI capture. A held physical mouse must be released before
it can acquire another virtual control following a lifecycle cancellation.

See [UI coordinates and layout](../../architecture/ui/01-Architecture.md) and
[the browser font path](browser-ui-fonts.md) for composing rendered controls.
