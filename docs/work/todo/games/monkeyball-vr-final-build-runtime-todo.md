# MonkeyBall VR Final-Build Runtime Todo

Date: 2026-07-29
Status: Open — reproduced by the user in the packaged NativeAOT game

## Confirmed Final-Build Behavior

Treat the user's validation of the actual packaged executable as authoritative:

- Directional lighting is visible.
- The ball does not run a visible physics simulation.
- WASD and arrow-key input do not rotate the stage.
- Directional shadows are not visible.

The previous window-capture comparison was not sufficient proof of physics or
input. Do not mark these issues fixed again based on editor behavior, source
inspection, changing screenshots, or a successful launch alone.

Reproduce against:

```text
Samples/MonkeyBallVR/Build/Publish/Binaries/MonkeyBallVR.exe
Samples/MonkeyBallVR/Build/Packages/MonkeyBallVR-win-x64.zip
```

The last tested ZIP had SHA-256
`E3018BB0708E93C83DE0DD5560BD42F44C9415BA7EE560C05E539B623750A162`.

## Required Working Rules

- Validate the cooked NativeAOT executable, not only the editor or a managed
  development build.
- Keep `MonkeyBallWorld.asset` authoritative. Do not add a hardcoded runtime
  world or transform-only physics fallback.
- Make missing lifecycle, input, physics, or shadow state fail visibly with
  diagnostics. Do not silently substitute transform animation or unshadowed
  lighting.
- Record numeric runtime state and GPU-resource evidence; screenshots are
  supporting evidence only.
- Change one subsystem at a time and republish the exact Release assemblies
  consumed by launcher generation before testing.

## P0 — Add Packaged-Runtime Observability

- [ ] Add an opt-in MonkeyBall runtime diagnostic mode suitable for NativeAOT,
      enabled by a launch flag or narrowly named environment variable.
- [ ] Log and expose counters for:
  - `MonkeyBallGameComponent.OnComponentActivated`.
  - `MonkeyBallGameComponent.OnBeginPlay`.
  - `PrePhysicsTick` and normal `Tick`.
  - Pawn binding, local-player possession, and `RegisterInput`.
  - W/A/S/D and arrow callback counts plus the published tilt vector.
  - Active physics scene identity and simulation-step count.
  - Course and ball component activation.
  - Course and ball native rigid-body creation and scene membership.
  - Kinematic target position/rotation.
  - Ball physics position, linear velocity, angular velocity, and sleep state.
  - Directional-light activation, `CastsShadows`, shadow-map allocation, and
    shadow-render request count.
- [ ] Put a compact diagnostic line in the HUD showing lifecycle, input,
      pre-physics, physics-step, and shadow-pass counters.
- [ ] Make the diagnostic mode return a nonzero exit code when required
      runtime objects or counters never become valid.
- [ ] Save packaged logs and captures under one new
      `Build/_AgentValidation/<run>/` root.

## P0 — Find the Cooked-World Lifecycle Break

- [ ] Verify the cooked world contains one enabled
      `MonkeyBallGameComponent`, `MonkeyBallPawnComponent`, dynamic ball body,
      kinematic course body, camera, and directional light.
- [ ] Verify the loaded `XRWorld`, scene, and root nodes enter the same
      activation and begin-play lifecycle as editor-authored worlds.
- [ ] Confirm `OnComponentActivated` registers both ticks after the component
      has a world and scene.
- [ ] Confirm `OnBeginPlay` runs and reaches all of these operations:
  - Scene-reference resolution.
  - Pawn binding.
  - Local-player possession.
  - Ball reset.
  - Camera assignment.
- [ ] If begin play is skipped for cooked worlds, fix world/scene activation in
      the engine runtime rather than adding a MonkeyBall-only startup call.
- [ ] Add a cooked-world lifecycle regression test that activates the asset and
      observes component activation, begin play, and tick execution.

Decision points:

- No activation counter: cooked scene/component activation is broken.
- Activation but no begin-play counter: play-state propagation is broken.
- Both lifecycle counters work but no ticks: tick registration/scheduling is
  broken.
- All game ticks work but no physics steps: runtime physics-world startup is
  broken.

## P0 — Restore Real Physics Simulation

- [ ] Confirm the packaged world creates a physics scene using the authored
      gravity, 1/90-second timestep, and two substeps.
- [ ] Confirm both `DynamicRigidBodyComponent.RigidBody` references become
      non-null after activation.
- [ ] Confirm the ball actor:
  - Is dynamic and simulation-enabled.
  - Has the authored sphere collider and material.
  - Is inserted into the active world physics scene.
  - Is awake after reset.
- [ ] Confirm the course actor:
  - Is kinematic with query-target support.
  - Has every authored compound collider.
  - Is inserted into the same physics scene as the ball.
- [ ] Verify the engine steps that scene while the game is playing and that
      rigid-body transforms synchronize back to scene transforms.
- [ ] Apply the stage kinematic target during `PrePhysics`, before the same
      scene's simulation step.
- [ ] Remove the current transform fallback when the native course body is
      absent. In the final game, a missing body must produce an actionable
      error instead of appearing to work.
- [ ] Verify reset writes pose and zero velocities to the native actor, wakes
      it, and updates the interpolation transform.
- [ ] Add a real physics integration test: load the cooked world, simulate a
      fixed number of steps, and assert that gravity/contact and a tilted
      course change the ball's native pose and velocity.

## P0 — Restore Packaged Keyboard and Controller Input

- [ ] Verify local player one exists before possession.
- [ ] Verify `PossessByLocalPlayer` assigns the authored
      `MonkeyBallPawnComponent` to a local controller.
- [ ] Verify that controller owns the final window's `InputInterface` and that
      `TryRegisterInput` runs after the viewport/window is bound.
- [ ] Verify `MonkeyBallPawnComponent.RegisterInput` receives a non-unregister
      interface and retains its keyboard, gamepad, OpenXR, and OpenVR
      registrations.
- [ ] Trace real W and Up Arrow events from the focused packaged window through
      `PublishTilt` to `MonkeyBallGameComponent.SetTilt`.
- [ ] Verify the tilt vector remains nonzero while a key is held and returns to
      zero on release.
- [ ] Verify all equivalent/opposing mappings:
  - W equals Up Arrow.
  - S equals Down Arrow.
  - A equals Left Arrow.
  - D equals Right Arrow.
  - Opposing pairs cancel.
- [ ] Verify gamepad and VR action-set activation independently of keyboard
      registration.
- [ ] Add an input integration test that possesses the cooked pawn, dispatches
      synthetic key-state changes through `InputInterface`, and observes the
      published tilt vector.

## P0 — Verify Stage Rotation and Ball-Centered Pivot

- [ ] Record the input vector, camera yaw, camera-relative world tilt, target
      quaternion, and target translation every diagnostic interval.
- [ ] Assert that held input changes the native kinematic actor rotation, not
      only the authored transform.
- [ ] Assert the stage pivot remains at the ball's current ground-projected
      position within a small numeric tolerance.
- [ ] Verify camera-relative directions again after the follow camera has
      changed yaw.
- [ ] Confirm stage motion, ball acceleration, and camera follow using numeric
      transforms captured from the packaged process.

## P1 — Restore Standalone Non-Cascaded Shadows

- [ ] Explicitly author and cook every required sun setting instead of relying
      on constructor defaults:
  - Dynamic light type.
  - `CastsShadows = true`.
  - `UseShadowAtlas = false`.
  - `EnableCascadedShadows = false`.
  - 2048x2048 standalone shadow-map resolution.
  - Depth storage/encoding and required bias values.
- [ ] Audit `MonkeyBallWorldCookedSerializer` for those properties and add
      authored-versus-cooked parity assertions.
- [ ] Verify light activation calls the standalone shadow-map allocation path
      after deserialization and produces a non-null framebuffer/receiver
      texture.
- [ ] Verify the desktop camera remains in
      `DirectionalShadowRenderingMode.NonCascaded`.
- [ ] Verify course, ball, bumpers, and goal geometry enter the shadow-caster
      collection, including CPU-owned procedural meshes.
- [ ] Verify the light's orthographic shadow camera encloses the playable
      course and uses a valid near/far range.
- [ ] Verify the shadow pass executes every required frame and writes
      nonuniform depth.
- [ ] Use RenderDoc on the exact packaged executable to prove:
  - A dedicated 2048x2048 shadow target exists.
  - No cascade or directional-atlas target is used by this light.
  - The shadow target contains course/ball/bumper depth, not only its clear
    value.
  - The lighting pass binds the standalone shadow receiver texture.
  - `EnableCascadedShadows` is false in the receiving shader.
  - Moving the light changes both the shadow depth and the final image.
- [ ] Add a rendering regression test or deterministic capture contract for
      standalone, non-atlased, non-cascaded directional shadows.

Shadow decision points:

- No shadow-map resource: light activation or cooked state is broken.
- Allocated map stays clear: shadow camera, pass scheduling, or caster
  collection is broken.
- Valid depth but no final shadow: texture binding, transform uniforms, or
  receiver shader selection is broken.

## P1 — Strengthen Final-Build Validation

- [ ] Extend `--aot-smoke` with a MonkeyBall runtime mode that runs enough
      frames to validate lifecycle and physics rather than exiting after asset
      loading.
- [ ] Fail that smoke test unless:
  - Begin play and both game tick groups execute.
  - A local pawn is possessed and input registration completes.
  - Both native rigid bodies exist in an advancing physics scene.
  - A scripted tilt changes the course actor and ball state.
  - A standalone directional shadow map is allocated and rendered.
- [ ] Rebuild the canonical Release editor/tooling output before every
      NativeAOT publish; verify the packaged rendering assembly hash matches
      the just-built source output.
- [ ] Run the focused tests, NativeAOT publish, archive smoke, direct executable
      run, runtime diagnostic capture, and RenderDoc capture in that order.
- [ ] Update the investigation and progress notes only after the packaged
      acceptance criteria below pass.

## Packaged Acceptance Criteria

All criteria must pass in
`Samples/MonkeyBallVR/Build/Publish/Binaries/MonkeyBallVR.exe`:

- [ ] The game reaches begin play and advances normal, pre-physics, and physics
      counters continuously.
- [ ] The ball has a live dynamic actor and visibly/numerically responds to
      gravity and course contact.
- [ ] Holding W rotates the native course actor toward its authored maximum
      tilt and moves the ball within two seconds.
- [ ] Up Arrow produces the same numeric target as W; every other key pair is
      similarly verified.
- [ ] The course rotates around the current ball position instead of the world
      origin.
- [ ] The desktop camera remains upright and follows the ball while yawing
      toward horizontal velocity.
- [ ] R resets the native ball pose and velocities.
- [ ] The sun remains non-cascaded and non-atlased.
- [ ] A dedicated shadow map contains caster depth and produces clearly visible
      moving shadows in the final lighting pass.
- [ ] No loose authoring asset, editor assembly, hardcoded world constructor,
      transform-only physics fallback, or silent unshadowed fallback is used.
- [ ] The repackaged ZIP passes the expanded smoke test and receives direct
      user sign-off.

## Relevant Files

- `Samples/MonkeyBallVR/Assets/Worlds/MonkeyBallWorld.asset`
- `Samples/MonkeyBallVR/Assets/Scripts/MonkeyBallGameComponent.cs`
- `Samples/MonkeyBallVR/Assets/Scripts/MonkeyBallPawnComponent.cs`
- `Samples/MonkeyBallVR/Assets/Scripts/MonkeyBallGameBootstrap.cs`
- `Samples/MonkeyBallVR/Assets/Scripts/MonkeyBallWorldCookedSerializer.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Camera/CameraComponent.cs`
- `docs/work/investigations/games/monkeyball-vr-physics-lighting-2026-07-29.md`
