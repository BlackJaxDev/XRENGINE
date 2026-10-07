# Jolt Character Controller Correctness TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Physics Architecture](../../../architecture/physics/overview.md#character-controller-contract)
Validation: [Physics Validation](../../testing/physics/physics-validation.md#character-controller)

## Current State

The fixed-step movement contract, the Jolt update lifecycle, the separate Jolt settings, total capsule height, and the support-state model are in code. `CharacterMotionInputModel` (`Velocity`, `Displacement`) and `CharacterSupportState` are in `PhysicsContracts.cs`. `PhysicsCharacterControllerCapabilities` reports per-backend features. `CharacterControllerComponent` exposes `TotalHeight`, `PredictiveContactDistance`, and `CollisionTolerance`. `CharacterMovementComponent` exposes `SupportState`, `GroundNormal`, and `GroundVelocity`. Tests are in `CharacterMotionBufferTests`, `CharacterMovementGroundSupportTests`, `CharacterMovementModuleTimingTests`, `JoltControllerParityTests`, and `PhysicsSceneSerializationTests`. Character-versus-character collision, the Jolt inner rigid body, dynamic-body pushing, and query visibility are not implemented.

## Open Code Items

### Interaction capabilities

- [ ] Add a `CharacterVsCharacterCollision` registry to `JoltScene` if the shared contract requires character collision. Register and unregister controllers deterministically. `JoltCharacterVirtualController`, `JoltScene`. Done when: two Jolt characters collide and teardown empties the registry.
- [ ] Configure the optional Jolt inner rigid body with the intended layer, shape, mass, and user data if rigid bodies and queries must detect the character. Keep it aligned with the virtual character on move, teleport, resize, up change, deactivation, and scene reset. Done when: the inner body pose matches after each event in a unit test.
- [ ] Define whether characters push dynamic bodies, receive impulses, or both. Map mass and maximum strength to that policy. Done when: `PhysicsCharacterControllerCapabilities.DynamicBodyInteraction` and `MaximumStrength` match the implemented behavior per backend.
- [ ] Define whether controller query hits come from the virtual-character registry, the inner body, or both. Prevent duplicate hits. Done when: `QueryVisibility` is reported per backend and a query returns one hit per character.
- [ ] Show unsupported interaction combinations in the editor. Done when: the controller inspector marks unsupported capabilities for the active backend.
- [ ] Add lifecycle diagnostics for registries, inner bodies, listeners, and native controllers. Done when: counts return to zero after repeated create, step, and destroy cycles.

### Missing tests

- [ ] Add end-to-end cadence tests: 30, 60, 120, and 144 Hz producer ticks against 60 Hz physics, with uneven and jittered deltas, for both input models, both `TickInputWithPhysics` modes, and both backends. Done when: equal input over equal elapsed time gives equivalent trajectories within a documented tolerance.
- [ ] Add a runtime input-model toggle test with commands in flight through the controller. Done when: no command applies twice and no transition spike occurs in either direction.
- [ ] Add walk-off and jump tests from translating, rotating, and accelerating platforms. Done when: inherited platform momentum is applied once.
- [ ] Add wall and ceiling fixtures that assert support state separately from collision flags. Done when: walls and ceilings never report `Supported`.
- [ ] Add jump height and airtime tests for Y-up, Z-up, and non-axis-aligned up on both backends. Done when: results stay within a documented tolerance.
- [ ] Add requested-versus-effective velocity assertions for free movement, wall sliding, corner collision, slopes, steps, and moving ground. Done when: each case asserts both values.
- [ ] Add PhysX-versus-Jolt scenario traces compared against shared behavioral tolerances. Record input model, raw command, requested velocity, gravity and jump velocity, ground point velocity, effective velocity, position, foot position, support state, ground normal, collision flags, contact count, and supporting body. Done when: the traces run without a visible editor or GPU.
- [ ] Add a fixed-step allocation assertion for idle and moving Jolt updates. Done when: the test asserts zero managed allocation after warmup.
- [ ] Add teardown and reload tests for controller registries, contact listeners, inner bodies, and backend diagnostic counts. Done when: all counts return to zero.
- [ ] Add character-versus-character, dynamic pushing, query visibility, and inner-body tests for each enabled capability. Done when: each capability asserts support or an explicit unsupported result.

### Documentation

- [ ] Add API and editor tooltips for the input-model toggle and its units, total capsule height, requested and effective velocity, support state, moving ground, up direction, and backend capabilities. `CharacterControllerComponent`, `CharacterMovementComponent`. Done when: each member has an XML summary and editor description.

## Decisions Needed

- [ ] Decide which interaction features v1 requires: character versus character, pushing dynamic bodies, receiving pushes, and query visibility. Owner: physics and gameplay.
- [ ] Decide the numeric tolerances for cross-backend and cross-up-direction scenario tests. Owner: physics.

## Out Of Scope

- Bit-identical trajectories between PhysX and Jolt.
- PhysX-only features in Jolt: native materials, invisible walls, maximum jump height, scale coefficient, volume growth, and constrained climbing. These stay capability differences.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/physics/jolt-character-controller-correctness-todo.md`

- [ ] Add walkable/too-steep/wall/ceiling contact fixtures that assert support
  state separately from collision flags.
- [ ] Jolt advances controller contacts, support, floor sticking, stairs, and
  moving-ground behavior every fixed step, including idle steps.
