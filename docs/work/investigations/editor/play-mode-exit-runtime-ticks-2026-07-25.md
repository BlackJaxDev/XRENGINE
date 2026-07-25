# Play-Mode Exit Leaves Editor Runtime Ticks Stopped - 2026-07-25

## Problem

After entering and then exiting play mode:

- the editor flying camera no longer responds to mouse or keyboard input;
- the native UI FPS text stops updating;
- Dear ImGui and viewport rendering remain responsive.

## Findings

- The editor and world lifecycle states diverge after exit.
  - Before entering play mode, `Engine.PlayMode.State` is `Edit` while the active
    `XRWorldInstance.PlayState` is `Playing`.
  - After exiting, `Engine.PlayMode.State` returns to `Edit`, but the active
    `XRWorldInstance.PlayState` remains `Stopped`.
- `XRWorldInstance.EndPlay()` calls `UnlinkTimeCallbacks()`, which removes the
  world's `PreUpdate`, `Update`, `PostUpdate`, `FixedUpdate`, swap, and visible
  collection callbacks from `Time.Timer`.
- `Engine.PlayMode.ExitPlayModeAsync()` ends each world, restores the edit
  snapshot, restores editor pawn possession, and switches the editor state to
  `Edit`, but it does not restart the edit-mode world lifecycle or relink those
  timer callbacks.
- The flying pawn's input dispatch is a normal world tick:
  `PawnComponent.TickInput()` is registered in `ETickGroup.Normal`.
- The native FPS overlay is also a normal world tick, registered with
  `RegisterAnimationTick(TickFPS)`.
- Restored possession, viewport, camera, keyboard, and mouse bindings are
  present after exit. They cannot do useful work because
  `XRWorldInstance.Update()` is no longer subscribed and therefore never
  dispatches `ETickGroup.Normal`.
- Dear ImGui remains responsive because its window/render path is independent
  of the stopped world update subscription. This makes the failure look like an
  input-capture or frozen-window problem even though the window loop is alive.

## Suggested Solution

Add an explicit edit-runtime lifecycle transition after the edit snapshot is
restored. It should restore the world timer/update/render-collection
subscriptions needed by editor-only nodes without invoking gameplay
`OnBeginPlay` or enabling gameplay physics.

Keep editor pawn restoration after the world is able to accept and dispatch
ticks, then refresh viewport input and camera bindings. Avoid using the full
gameplay `BeginPlay()` operation as an implicit edit-mode restart unless its
gameplay lifecycle side effects are separated first.

## Attempted Solutions

- No runtime change was made during this diagnostic pass.

## Validation Evidence

- Isolated session:
  `Build/_AgentValidation/mcp-sessions/play-exit-freeze-20260725/`.
- MCP before play:
  `playModeState = Edit`, `world.playState = Playing`.
- MCP after an enter/exit cycle:
  `playModeState = Edit`, `timePaused = false`,
  `world.playState = Stopped`.
- A post-exit Vulkan viewport capture still completed successfully, confirming
  that rendering/window processing remained alive:
  `mcp-captures/Screenshot_20260725_033949_675_4f00589ed4a84fd1b064a71d5029d0ec.png`.
- The session log records the world transition to `Stopped`, the editor pawn
  and camera restoration, and the editor transition back to `Edit`, with no
  subsequent world transition to `BeginningPlay` or `Playing`.
- User report: the native FPS text and editor flying-camera input both stop
  after the transition; Dear ImGui continues to work.

## Status

Root cause identified. Fix not yet implemented.
