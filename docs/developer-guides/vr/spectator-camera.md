# Independent VR spectator

`BootstrapVrSpectatorFactory.Create(sceneParent, playerRoot, player, playerCollisionBody)`
creates an explicitly associated local-player follow rig. It does not create a pawn,
possess a controller, install an audio listener, change the tracking origin, or
modify either OpenXR eye camera. Keep the returned `VrSpectatorFollowComponent`
with the local player and destroy its anchor subtree with that player.

## Follow and collision

The anchor combines the explicit player-root horizontal position with the valid
hips-source height and yaw. Without usable hips it uses the player-root heading;
headset pitch, roll, and glances never directly drive the spectator. The smoothed
anchor is evaluated after late animation on the simulation thread. No avatar bone
is mutated from the renderer.

`Settings` provides distance, height, shoulder offset, body-relative aim offset,
vertical field of view, and exponential smoothing rate. The engine faces `-Z`, so
the trailing boom extends along local `+Z`. The existing `BoomTransform` sphere
sweep snaps inward at obstructions and exponentially recovers outward. A supplied
player body is excluded. Without an explicit body, the factory uses static-only
collision filtering to avoid hitting a dynamic player; supply the body to include
other dynamic obstacles. Add additional owner bodies to `Boom.IgnoredComponents`
when the player has multiple colliders.

`RuntimeVrDiscontinuityServices.Publish` announces teleport, snap turn, recenter,
avatar replacement, session-generation, or camera-mode cuts after the corresponding
simulation change. Follow consumes its monotonic version on the simulation thread;
output history resets are consumed on the rendering thread. This resets interpolation,
the spectator camera history, and each independently owned capture history.

## Output ownership and cadence

`Output.Width`, `Output.Height`, and `Output.FramesPerSecond` configure the render
texture. Two `SpectatorTextureCaptureComponent` slots reuse the completion-gated
offscreen implementation with the Default GPU pipeline. Each has its own camera,
viewport, output identity, pipeline instance, and resources. Captures use clean
linear HDR without debug overlays, post-processing, or temporal accumulation.
Each rebuilt slot initially draws without frustum culling to establish imported
skinned bounds, then enables culling after its first completed capture. Each request is
background/deferrable, never an XR-critical dependency. GPU completion is polled;
refresh does not wait for a fence or a consumer. A held slot lease prevents writes
and retirement of that exact texture.

Use `Output.TryAcquireCompletedOutput(out lease)` for capture/preview consumers.
Draw from `lease.Texture`, then release the lease only after the consumer's own
GPU read completion. Reuse this output for multiple previews rather than requesting
another scene capture. A consumer that never releases its leases stalls spectator
refresh, not headset submission. There is no video encoder or audio capture bus.
The render texture is a float color target; applications must apply the intended
tone mapping and force opaque alpha when presenting or encoding opaque video.

`RouteDesktop(viewport, camera)` switches only the chosen desktop viewport. Pass
the first-person preview, spectator camera, or editor camera explicitly. A direct
desktop spectator view disables its texture producer to avoid a second scene render;
selecting another camera resumes the texture producer. Possession and the player's
headset audio listener remain unchanged. This direct desktop mode is distinct from
a capture preview using the completion-gated texture.

## First-person visibility

Call `BootstrapVrSpectatorFactory.ConfigureFirstPersonVisibility` with the avatar
root, explicit headset component, and a layer reserved for that local avatar. It
assigns render-info layers and excludes that layer only from the two eye masks.
The spectator/editor/capture masks still include it. It never disables the meshes
globally. Choose the layer deliberately; do not share it with unrelated objects.
Reconfigure after replacing the avatar or changing its mesh set. Destroying the
spectator restores the previous mesh layers and the relevant bits of the eye masks.

## Validation boundary

Deterministic headless tests exercise follow sign, body yaw, fallback heading,
framerate-independent smoothing, discontinuity reset, degenerate inputs, bounded
settings, boom recovery, eye-mask restoration, and unpublished-output rejection.
Deterministic fence tests additionally exercise pending and rejected submissions,
quarantine after unknown GPU completion, orphaned-slot draining, and rejection of
pre-cut publications. These use controlled fence states rather than real GPU timing.
The native lavapipe validation suite qualifies basic production Vulkan shader and
readback behavior only. It does not establish spectator scene orientation, post
processing, color space, aspect/visibility on a physical GPU, XR coexistence,
headset frame-time impact, or hardware input/audio behavior. Those require the
[named-hardware and visual acceptance procedure](../../work/testing/avatar/avatar-validation.md#openxr-body-calibration-and-spectator).

`BootstrapVrCalibrationFeedbackFactory.Create(parent, player)` separately attaches
world-space calibration messages, tracker assignment labels, marker spheres, and
floor footprints. It reads the same preview assignments used by capture and does
not infer roles or implement another matcher. The avatar eye mask is temporarily
opened while calibrating so the T-pose remains visible. The canvas and labels are
real scene UI, but their headset readability and Vulkan rendering still need visual
acceptance on the supported runtime configuration.
