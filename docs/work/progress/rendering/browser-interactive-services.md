# Browser interaction, animation and runtime services

**Status:** Source implementation. Builds, browser/GPU/audio execution and device
qualification remain deferred by request. No Python or validation was executed.

The built-in reference world now combines touch movement/look, a bounded character
controller, CPU skeletal animation, browser DOM interaction and gesture-activated
Web Audio. This is a declared portable sample profile. Static cooked worlds keep
their captured camera and do not silently inherit the demo collision world or its
animation fixture.

Cooked schema 3 can explicitly supply baked animation and static collision data.
It shares the existing character solver and engine hierarchy/timing types; it does
not reuse the demo's animation fixture as a production state machine. See
[cooked content delivery](browser-cooked-content.md) for those payloads and limits.

## Input and UI

`BrowserInput` retains eight pointer slots per canvas, including pointer identity,
kind, phase and normalized coordinates. Capture belongs to that canvas; cancellation,
lost capture, blur, suspension and teardown clear its actions. One left-half touch
controls movement and one right-half touch controls look concurrently. Additional
captured pointers retain identity without stealing these roles. Pointer displacement
is queued until a fixed simulation step, so render frames without a simulation tick
do not discard look motion. Mouse drag controls look without pointer lock.

Focused WASD/arrows and Space provide movement/jump. DOM fields, composition/IME and
modifier shortcuts do not become gameplay actions. A touch-sized Jump button also
supports keyboard/accessibility activation. Optional standard-mapped gamepad polling
uses a deadzone and runs only while the canvas owns focus; browser denial has a
visible reason. Wheel deltas become bounded CSS-pixel input snapshots, available
through `ConsumeWheel`; no camera zoom binding is imposed by the sample.

CSS safe-area insets and dynamic/visual viewport height accommodate orientation,
browser chrome and the on-screen keyboard. Pointer normalization uses the canvas's
CSS rectangle; rendering independently maps it to the bounded physical backing
extent. Page scrolling remains available outside canvas-owned gestures.

The selected essential UI implementation is browser DOM with system fonts and
native text/IME/accessibility behavior. It provides world loading, status/errors,
restart/stop, movement instructions, jump, audio controls and a committed scene-label
form that changes the actual engine scene-node name. GPU rectangles remain the
existing visual overlay; this work does not claim a GPU text-shaping/widget system
or import the native editor.

## Collision and simulation

The selected `static-aabb-character-v1` path is a managed swept-box controller
against at most 64 explicit static AABBs. It resolves the earliest contact and
slides for up to five contacts per step. The sample has a floor, boundary walls
and two visible obstacles whose transforms match the proxies. Rotating decorative
meshes are explicitly non-solid. Spawn overlap is rejected. Movement is 2.5 m/s,
gravity 12 m/s², jump velocity 4.5 m/s and terminal fall speed 20 m/s; the character
box half-extents are (0.25, 0.85, 0.25), with an eye offset of +0.7 Y.

This deliberately excludes dynamic rigid bodies, slopes, step climbing, mesh
colliders and arbitrary authored physics. It is not a fallback for an explicitly
requested native backend. Source candidate review found:

- `Scene/Physics/Physx/PhysxBase.cs` in Runtime.Core wraps unsafe MagicPhysX native
  pointers.
- `Scene/Physics/Jolt/JoltBootstrap.cs` initializes the native Jolt foundation and
  reports a missing `joltc.dll`; its bootstrap is not a browser adapter.
- `Scene/Physics/Jitter2/JitterScene.cs` has world stepping, but actor creation and
  sweep/overlap/raycast methods are unimplemented.

Their existing portable exclusions remain in place. No native package's WASM
compatibility is inferred or newly claimed.

Each fixed step runs character collision/movement, engine scene ticks, then animation
state/hierarchy evaluation. After all bounded catch-up steps, one final pose is
skinned and uploaded, render transforms are published, visibility is collected and
the frame is submitted. Resume/history resets clear stale input and vertical
velocity; ordinary text-field focus changes clear input while gravity continues.
Scene teardown releases the controller and all scene-owned resources.

## CPU animation

The portable path uses parent-before-child local TRS bones, computed inverse bind
matrices, uniformly timed clips, linear translation/scale interpolation and quaternion
slerp. A movement-speed-driven idle/walk blend evaluates a retained mesh-space bone
palette. Four-influence linear-blend position skinning writes retained position/UV
vertices. Limits are 128 bones, 600 frames per clip and 16,384 vertices per mesh.

The sample is an actual two-bone weighted strip with idle and movement clips, not
just a rotating scene node. It uploads the final simulated pose once per render
frame through the existing upload packet. Its bind-pose descriptor bounds do not
cull deformed geometry: the small reference bypasses static frustum rejection.
Normals come from the selected flat-shaded raster profile's world derivatives.
General smooth-normal/tangent skinning, blendshapes, humanoid retargeting, layers,
animation events and GPU animation remain outside this path.

Source review found the candidate `XREngine.Animation/Model/SkeletalAnimation.cs`
commented out, while the active desktop `Property/Core/AnimationClip.cs` pulls in
the larger importer/component/serialization graph. These new focused portable
types do not claim full desktop animator parity. Cooked animation payloads are
not admitted by the current static package schema.

## Audio ownership

`BrowserAudioService` owns one optional context per canvas session. **Enable audio**
calls `resume()` directly from the user gesture; only a running, visible, unpaused
context reports ready. Pending, denied, suspended and closed states have explicit
reasons. Visibility suspension does not promise automatic restart: the user can
enable audio again. Required audio suspends world simulation until it is ready.

Playback supports gain, looping, positional panners, source transforms and an
engine-camera listener update using numeric interop without per-frame pose arrays.
The built-in sample generates a short PCM step pulse. The UI also accepts a bounded
audio file, decodes it through `decodeAudioData`, and plays it at a fixed world
source with optional looping and volume/pause controls. Codec support is determined
by the actual browser decoder; universal MP3/OGG support is not asserted.

Limits: 32 buffers/voices, 8 MiB per encoded clip, 16 MiB pending encoded input, two
concurrent decodes and 32 MiB retained PCM. Decoded clips must be mono/stereo, at
most 120 seconds and no more than 96 kHz. A 30-second decode deadline and owner epoch
prevent stale publication. The browser's internal decoder cannot be cancelled or
have its temporary allocation prebounded; pending reservations stay held until that
operation settles even when timeout/cancellation returns earlier. No native OpenAL,
FFmpeg or NAudio service enters this runtime path.

## Required and optional services

Manifest schema 1 remains compatible. Schema 2, emitted by
`xrengine-browser-content-2`, additionally requires:

```json
"services": { "required": ["dom-ui"], "optional": ["web-audio"] }
```

Names are bounded, unique and disjoint across these arrays. Unknown required
services fail by name before simulation begins. Optional exclusions remain
visible in counter snapshots with reasons. `dom-ui` and `web-audio` are registered
for static cooked content; required Web Audio waits for a successful gesture and
blocks if the API is unavailable. `cpu-animation` and `character-collision` are
admitted by the built-in sample or by actual animated instances and an installed
collision world in schema-3 essentials. Static packages requiring absent payloads
still fail; an unused animation/collision asset does not admit the service.

Native XR, native physics, native editor UI and native video decoding have explicit
exclusion reasons. No required feature is replaced with a no-op. The sample requires
DOM UI, CPU animation and character collision; audio is optional until requested.
The existing forward shader/material profile remains unchanged by service schema 2.

## Remaining acceptance

Touch combinations, capture cancellation, keyboard/IME, gamepad denial, safe areas,
keyboard resize, collision tunneling/contacts/jumps, pause/resume, animation matrices,
pose upload and allocation behavior, gesture denial, codec/decode cancellation,
listener orientation, service gating and repeated unloading need runtime evidence.
The source-completion TODO rows do not close G3 or claim device support.

Native animation/state-machine cooking, cooked audio-source schemas and broader
production physics and UI integrations remain separate work. Compute/indirect and
bounded deformation/visibility implementations are described in the
[reuse audit](browser-compute-reuse-audit.md); device qualification remains open.
