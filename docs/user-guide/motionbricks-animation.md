# MotionBricks Procedural Locomotion Setup

[Back to user guide](README.md)

Use this guide to set up NVIDIA MotionBricks (SOMA 30-joint procedural locomotion) on any humanoid avatar in XRENGINE, including the official NVIDIA SOMA BVH motion-capture animations, Jolt root-motion physics, third-person controls, procedural eye gaze, and debug visualization.

You can complete the full setup directly inside the ImGui editor without writing code or opening a terminal. An optional C# reference is included at the end for custom hosts or scripted spawning.

## Requirements

- Windows 10 or Windows 11 with an NVIDIA CUDA-capable GPU.
- CUDA 12.x toolkit and TensorRT 10.x SDK.
- Visual Studio Build Tools (or Visual Studio) with the **Desktop development with C++** workload (to compile `MotionBricksBridge.Native.dll`).
- A humanoid avatar imported with a finalized `HumanoidComponent`.

MotionBricks does not fall back to CPU inference. If CUDA, TensorRT, or the SOMA weights are missing, the driver stays inactive and reports the exact reason in `RuntimeDiagnosticStatus` and in the Animation State Machine panel.

Projects that do not activate a `MotionBricksLocomotionMotion` state pay zero TensorRT, CUDA, thread, or weight-loading cost at runtime.

---

## Step 1: Stage TensorRT, NVIDIA SOMA Weights, And BVH Animations

Open the editor and select **Tools -> Dependencies & Builds...** (or **View -> Dependencies & Builds**).

Run these items in order and check that each badge changes to green:

1. **Install CUDA 12.x** (if `Get-CudaToolkit` shows `CUDA_PATH not set`):
   - Select `Get-CudaToolkit` in the left list and click **Run**, or install the NVIDIA CUDA 12.x toolkit from NVIDIA and restart the editor so `CUDA_PATH` is visible.
2. **Stage TensorRT 10.x**:
   - Select `Get-TensorRT`.
   - Enable `SetUserEnvironment` if you want `TENSORRT_ROOT_DIR` persisted across sessions (or provide `ArchivePath` if you already downloaded the NVIDIA TensorRT Windows zip).
   - Click **Run**.
3. **Download NVIDIA MotionBricks SOMA weights and BVH animations**:
   - Select `Get-MotionBricks`.
   - Keep `Precision` set to `fp16` (default, ~363 MB, SHA-256 verified) and keep `SkipPresets` unchecked so the NVIDIA Smart Locomotion presets and BVH mocap clips download too.
   - Click **Run**.
4. **Build the native TensorRT bridge**:
   - Filter by **Native Builds** and select `MotionBricksBridge (C++ Native Bridge)`.
   - Select `Release` configuration and click **Run**.
   - Confirm that the status badge changes to `Built` and the output shows `MotionBricksBridge.Native.dll`.

### What NVIDIA Animations And Presets Are Staged

When `Get-MotionBricks` runs without `-SkipPresets`, it stages everything under `Build/Dependencies/MotionBricks/` (ignored by Git):

| Folder | Contents | How XRENGINE Uses Them |
|---|---|---|
| `soma/` | `soma_n1.json`, `mmm_v6_soma_fp16.onnx` | Defines the 30-joint SOMA N1 skeleton contract and FP16 neural locomotion weights. TensorRT compiles and caches a GPU `.plan` file under `Build/Dependencies/MotionBricks/cache/`. |
| `smart_primitives/soma/` | `locomotion_bs1_linear.smart_primitive.json`, `locomotion_bs2_directional.smart_primitive.json`, `locomotion_bs2_directional_crouch.smart_primitive.json`, `locomotion_style_variations_bs1_linear.smart_primitive.json` | Defines the 1D linear (`Idle`, `Walk`, `Jog`, `Run`), 2D directional strafe, and crouch target pose templates loaded by `SomaSmartPrimitiveAssetLoader`. |
| `motions/soma/locomotion/idle/` | `idle_loop_001__A268.bvh`, `warm_up_neck_001__A359.bvh`, `legs_relax_002__A407.bvh`, `crouch_idle_001__A529.bvh` (plus `neutral_looking_around_R_001__A542.bvh`) | Loops natural standing and crouched idle variations after SOMA finishes braking. |
| `motions/soma/locomotion/bs_1d_linear/`, `bs_2d_directional/`, `bs_2d_directional_crouch/` | Walk, jog, run, strafe, and crouch BVH mocap clips referenced by the Smart Primitive JSON files | Conditions SOMA target local poses (`has_local_poses = 1`) for clean posture, stride rhythm, and arm carriage. |
| `motions/soma/jump/` | `jump_over_obstacle_0_5m_R_001__A415.bvh`, `jump_over_obstacle_no_hand_0_75m_R_103__A416.bvh` | Drives velocity-phased airborne jump/fall poses and stationary landing absorption. |

Prone idle, prone army-crawl, and hands-and-knees stance transitions (`Stand <-> Crouch <-> Hands-and-Knees <-> Prone`) are generated procedurally in `SomaLocomotionPoseBank` so prone works without extra mocap files.

*(Optional)* If you store weights or TensorRT plans outside `Build/Dependencies/MotionBricks/`, set the `MOTIONBRICKS_ONNX_PATH` and `MOTIONBRICKS_CACHE_DIR` environment variables.

---

## Step 2: Build The Character Scene Hierarchy

Use a three-node character hierarchy so physics capsule resizing and camera orbiting stay separate from avatar rotation:

```text
PlayerRoot (RigidBodyTransform)
├── CameraPivot (Transform) -> ThirdPersonBoom (BoomTransform) -> Camera (SmoothedTransform + CameraComponent)
└── FootAnchor (Transform)
    └── AvatarRoot (Transform, imported humanoid model with HumanoidComponent)
```

1. **Create `PlayerRoot`** with a `RigidBodyTransform`:
   - Add `CharacterMovement3DComponent`.
   - Add `CharacterPawnComponent`.
   - Add `CharacterRootMotionBridgeComponent` (**Gameplay -> Character Root Motion Bridge**).
2. **Create the camera rig** as a child of `PlayerRoot`:
   - Add a `CameraPivot` node (`Transform`), a child `ThirdPersonBoom` node (`BoomTransform`), and a child `Camera` node (`CameraComponent`).
   - Add `ThirdPersonCameraRigComponent` (**Camera -> Third-Person Camera Rig**) on `PlayerRoot` and assign `CameraBoom` (`ThirdPersonBoom`), `CameraPivotTransform` (`CameraPivot`), and `CharacterMovement`.
   - On `CharacterPawnComponent`, assign `CameraComponent`, `InputOrientationTransform` (camera transform), `ViewRotationTransform` (`CameraPivot`), and `ThirdPersonCameraRig`.
3. **Create `FootAnchor`** (`Transform`) as a child of `PlayerRoot` and place your imported humanoid **`AvatarRoot`** as a child of `FootAnchor`.
   - `FootAnchor` keeps the avatar's feet planted on the ground when Jolt resizes the capsule for crouch and prone.
   - `AvatarRoot` receives animation yaw rotation without spinning the third-person camera.

---

## Step 3: Build The Animation State Machine In The Visual Graph Editor

1. Select **`AvatarRoot`** in the Hierarchy and add `AnimStateMachineComponent` (if it is not already attached).
2. In the Inspector on `AnimStateMachineComponent`:
   - Assign **`Humanoid`** to the avatar's `HumanoidComponent`.
   - Set **`RootMotionApplicationMode`** to `ExternalConsumer`.
   - Click **`Open State Machine Graph`** (or open **View -> Animation State Machine**).
3. In the **Animation State Machine** panel:
   - Ensure Layer `0` exists (click **`Add Base Layer`** if empty).
   - Click **`+ Add State`** (or right-click the graph canvas) and select **`MotionBricks Locomotion`**.
   - The graph editor uses the AOT-safe `MotionFactoryRegistry` to create `MotionBricksLocomotionMotion`, marks it as `[Entry]`, and automatically adds all seven required parameters in the **Parameters** tab:
     - `MoveX` (`float`)
     - `MoveY` (`float`)
     - `DesiredFacingYaw` (`float`)
     - `StrafeMode` (`bool`)
     - `LocomotionStyle` (`int`: `0` = standing, `1` = crouched, `2` = prone)
     - `IsGrounded` (`bool`)
     - `VerticalVelocity` (`float`)
4. Select the new state node to inspect its **Motion Properties** on the right:
   - Confirm **`MotionBricks Ready`** appears in green. If TensorRT is still compiling or loading the `.plan` cache on first run, click **`Prewarm TensorRT`** or wait a few seconds for warmup to finish.
   - Adjust `WalkSpeedMetersPerSecond`, `RunSpeedMetersPerSecond`, or parameter names if desired.
   - Click **`Rebuild Playback`** in the toolbar after any structural edits so slot layouts and drivers refresh live.

### Combining MotionBricks With Regular Clips And Layers

Because `MotionBricksLocomotionMotion` writes the 49 canonical body and limb humanoid muscle slots (and leaves finger, jaw, and eye slots untouched):

- **Transitions to clips:** Right-click the `MotionBricks Locomotion` state, click **`Make Transition`**, and click another state (such as an `Animation Clip` or `Blend Tree 1D` state). Select the arrow to configure `BlendDuration`, `HasExitTime`, and parameter conditions. When transitioning back into MotionBricks, the engine automatically seeds SOMA's 4-frame 30 Hz context from the live humanoid pose.
- **Upper-body or additive layers:** Click **`+ Layer`** in the **Layers** tab, set `Apply` to `Override` or `Additive`, choose an **`Avatar Mask`** preset (`Upper`, `Lower`, `Hands`, `Face`, or per-body-part checkboxes) to isolate which muscles and root-motion tracks the layer affects, and add states on that layer.

---

## Step 4: Bind Jolt Root Motion And Pawn `XREvent` Callbacks In The Inspector

1. Select **`PlayerRoot`** and configure **`CharacterRootMotionBridgeComponent`** in the Inspector:
   - **`Animator`**: assign `AvatarRoot`'s `AnimStateMachineComponent`.
   - **`CharacterMovement`**: assign `PlayerRoot`'s `CharacterMovement3DComponent`.
   - **`AvatarFacingTransform`**: assign `AvatarRoot`'s `Transform`.
   - **`FootHeightTransform`**: assign `FootAnchor`'s `Transform`.
   - When active, `CharacterRootMotionBridgeComponent` automatically configures `RootMotionMovementModule` (`JumpForce = 4.9`, `GravityScale = 2.0`), sets `TickInputWithPhysics = true`, binds `PhysicsMotionBricksTrajectoryProber` for terrain and obstacle lookahead, and reconciles wall collisions back into SOMA.
2. On **`CharacterPawnComponent`** (on `PlayerRoot`):
   - Enable **`UseRootMotionSteering`**.
   - Find **`RootMotionSteeringUpdated`** (`XREvent<SteeringSnapshot>`), click **`Add Callback`**, select **`PlayerRoot`** in the Node picker, and choose **`CharacterRootMotionBridgeComponent.ApplySteeringSnapshot`** in the Method dropdown.

---

## Step 5: Add Optional Eye Gaze And Debug Visualization

1. **Procedural eye gaze and blinking:**
   - Select **`AvatarRoot`** and add **`HumanoidProceduralEyeGazeComponent`** (**Animation -> Humanoid Procedural Eye Gaze**).
   - Assign **`Humanoid`**, **`Animator`**, **`AvatarFacingTransform`** (`AvatarRoot`'s transform), and **`ViewerCameraTransform`** (`Camera`'s transform).
2. **Live MotionBricks 3D debug overlay (toggleable with `F`):**
   - Select **`AvatarRoot`** and add **`MotionBricksDebugDrawComponent`** (**Animation -> MotionBricks Debug Visualizer**).
   - Assign **`Animator`** and set **`RenderEnabled`** to `false` (off by default).
   - On **`CharacterPawnComponent`** (`PlayerRoot`), find **`LocomotionDebugToggled`** (`XREvent`), click **`Add Callback`**, pick **`AvatarRoot`** as the Node, and select **`MotionBricksDebugDrawComponent.ToggleRenderEnabled`**.

---

## Runtime Controls

With `CharacterPawnComponent` possessed by the local player and `UseRootMotionSteering` enabled:

| Keyboard | Gamepad | Action |
|---|---|---|
| `WASD` | Left Stick | Walk or run relative to camera direction (analog stick magnitude controls walk/jog/run speed) |
| Mouse / Arrow Keys | Right Stick | Orbit third-person camera |
| Hold `Shift` | Left Face Button (`X` / Square, toggle) | Run |
| `Space` | Lower Face Button (`A` / Cross) | Jump (preserves carried horizontal momentum into landing) |
| `C` | Right Face Button tap (`B` / Circle) | Toggle crouch |
| `Z` | Right Face Button hold (`B` / Circle) | Toggle prone (routed through crouch and hands-and-knees transitions) |
| `Q` / `E` | `LB` / `RB` (`LeftBumper` / `RightBumper`) | Toggle left-shoulder / right-shoulder third-person camera view |
| Hold `R` / `T` | Hold `D-Pad Up` / `D-Pad Down` | Zoom centered third-person camera in or out |
| `F` | Top Face Button (`Y` / Triangle) | Toggle 3D MotionBricks trajectory dots, foot contacts, and current/future SOMA skeletons |

---

## Optional C# Setup Reference

If you spawn characters from code or run a custom host without `XREngine.Runtime.Bootstrap`, see the full working setup in `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs` (`ConfigureDesktopMotionBricksLocomotion`):

1. Register the backend once at startup (automatic when using `XREngine.Runtime.Bootstrap`):
   ```csharp
   MotionBricksRuntimeBackend.Register();
   MotionBricksLocomotionRuntimeServices.Prewarm(); // Optional background TensorRT + BVH warmup
   ```
2. Build the `AnimStateMachine` with `MotionBricksLocomotionMotion`, register its seven variables, set `RootMotionApplicationMode = EHumanoidRootMotionApplicationMode.ExternalConsumer`, and bind `CharacterRootMotionBridgeComponent` and `CharacterPawnComponent`'s `XREvent` fields (via `+=` or `XRPersistentCall`).

---

## Troubleshooting

- **`MotionBricks Not Ready` in the State Machine panel:** Open **Tools -> Dependencies & Builds...** and verify that `Get-CudaToolkit`, `Get-TensorRT`, `Get-MotionBricks`, and `MotionBricksBridge (C++ Native Bridge)` all show green ready badges.
- **Character walks on synthetic fallback poses instead of NVIDIA mocap clips:** Re-run `Get-MotionBricks` with `SkipPresets` unchecked so `Build/Dependencies/MotionBricks/smart_primitives/soma/` and `Build/Dependencies/MotionBricks/motions/soma/` contain the `.smart_primitive.json` and `.bvh` files.
- **First launch pauses before locomotion starts:** TensorRT compiles the ONNX graph into a GPU-specific `.plan` file under `Build/Dependencies/MotionBricks/cache/` on the first run. Subsequent runs load the cached `.plan` file quickly on the background worker while the avatar holds an idle pose.
- **Avatar faces backward on spawn:** Call `bridge.AlignAvatarToWorldForward(viewTransform.WorldForward)` before `BindEndpoints()`, or rotate `AvatarRoot` to match the camera forward axis.

## Deeper Docs

- [Animation](animation.md)
- [MotionBricks Developer Guide](../developer-guides/animation/motionbricks-locomotion.md)
- [Physics](physics.md)
- [Component System](components.md)
