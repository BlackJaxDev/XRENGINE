# Unit Testing World

The Unit Testing World is the main sandbox for validating engine changes inside the editor. It is driven by a JSONC settings file so you can switch test scenes, turn systems on and off, and adjust startup behavior without recompiling.

## Canonical settings file

- Main file: `Assets/UnitTestingWorldSettings.jsonc`
- Server mirror: `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
- VS Code schema: `.vscode/schemas/unit-testing-world-settings.schema.json`

The main file is generated locally and ignored by Git so each workstation can keep its own test-world tuning. On a fresh checkout, `ExecTool --bootstrap` creates it before launching the editor; you can also create or refresh it with `pwsh Tools/Generate-UnitTestingWorldSettings.ps1`.

For day-to-day work, edit `Assets/UnitTestingWorldSettings.jsonc` directly. The JSONC comments are part of the intended workflow, and the schema gives you hover docs and enum completion in VS Code.

## How to launch it

You do not need to change code to switch into the Unit Testing World. Use one of these entry points.

### VS Code

Recommended launch profile:

- `Editor (Unit Testing World)`

Useful tasks:

- `Generate-UnitTestingWorldSettings`
- `Start-PoseServer-NoDebug`
- `Start-PoseSourceClient-NoDebug`
- `Start-PoseReceiverClient-NoDebug`
- `Start-LocalPoseSync-NoDebug`

### Command line

Run the editor in unit-testing mode:

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

Or set the world mode explicitly:

```powershell
$env:XRE_WORLD_MODE = "UnitTesting"
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

You can use either approach. The VS Code launch profile uses both `--unit-testing` and `XRE_WORLD_MODE=UnitTesting`.

### Visual Studio

Set the startup project to `XREngine.Editor`, then configure these in Project Properties -> Debug:

- Command line arguments: `--unit-testing`
- Environment variable: `XRE_WORLD_MODE=UnitTesting`

## Main workflow

1. Open `Assets/UnitTestingWorldSettings.jsonc`.
2. Pick the test world with `WorldKind`.
3. Enable or disable the systems you want to validate.
4. Launch `Editor (Unit Testing World)` in VS Code, or run the editor with `--unit-testing`.
5. Iterate on the JSONC file until the scene boots into the setup you need.

This flow is useful for rendering checks, scene-setup validation, VR startup testing, import experiments, and networking/pose test setups.

## Settings that matter most

You do not need to understand every property up front. These are the ones that usually matter first:

- `WorldKind`: selects the test scene or scenario to build
- `EditorType`: chooses the editor UI path used inside the test world
- `Rendering.RenderBackend`: selects OpenGL or Vulkan for unit-test startup
- `VR.Mode`: selects desktop, scene-only VR, Monado-backed OpenXR, OpenVR, or OpenXR startup
- `AddPhysics`, `PhysicsBallCount`, `PhysicsChain`: enable physics-focused scenarios
- `ModelsToImport`: imports test assets into the world at startup
- `DynamicPointLightCount`, `DynamicSpotLightCount`, `DynamicLightSeed`: add repeatable animated local-light stress rigs
- `ProceduralSky`, `ProceduralSkyAutoCycle`, `ProceduralSkyTimeOfDay`: enable the dynamic sky and optionally lock it to a deterministic sun position
- `InitializeAtmosphericScattering`: spawns the default planetary atmosphere and enables the matching camera post-process stage
- `GPURenderDispatch`: switches rendering dispatch mode
- `EditorCameraRenderOnDemand`: starts the editor camera in idle reuse mode, where unchanged views skip 3D scene rendering and present the last camera result
- `StartInPlayModeWithoutTransitions`: skips the usual edit-to-play transition
- `EnableProfilerLogging`: turns on profiler logging even without ImGui

The file itself includes comments for each property, and the schema provides enum suggestions and hover descriptions.

## Common scenarios

### Change the test world

Set `WorldKind` in `Assets/UnitTestingWorldSettings.jsonc`.

Examples visible in the generated file include:

- `Default`
- `AudioTesting`
- `MathIntersections`
- `MeshEditing`
- `UberShader`
- `PhysxTesting`
- `NetworkingPose`

### Test model import

Use `ModelsToImport` to add one or more assets at startup. Each entry can choose:

- whether it is enabled
- static vs animated import
- material mode
- importer backend preference
- import flags
- scale and transform offsets

If a startup model references textures outside the authored folder layout, use `TextureLoadDirSearchPaths` to add one or more root directories. The importer will recursively scan those directories by texture file name when the original path cannot be resolved relative to the model.

`ImporterBackend` defaults to `PreferNativeThenAssimp`, which uses a native importer when the format has one available and falls back to Assimp otherwise. That native path now covers FBX, glTF, and GLB startup imports. Set `ImporterBackend` to `AssimpOnly` if you need to force the older compatibility path for a specific FBX or glTF startup import.

For glTF validation work, the checked-in corpus under `XREngine.UnitTests/TestData/Gltf/` is the fastest repeatable source of startup assets. It covers external resources, data URIs, embedded GLB BIN chunks, sparse accessors, skins, morph targets, animations, and malformed-container regression cases.

`FbxLogVerbosity` controls how much native FBX importer/exporter trace output is emitted while the unit-testing world boots. When enabled, those lines go through the engine `Assets` log category, so they show up in the editor console `Assets` tab and in `Build/Logs/.../log_assets.log` when file logging is enabled. glTF does not currently expose a separate verbosity toggle; native glTF warnings and fallback diagnostics also flow through the normal asset-import logging path.

This is the fastest way to spin up repeatable import tests without hand-building the scene each time.

### Lock procedural sky time

When `ProceduralSky` is enabled, the skybox can drive the unit-test directional lights. For deterministic lighting captures, set `ProceduralSkyAutoCycle` to `false` and choose `ProceduralSkyTimeOfDay`; `0.25` is noon and `0.75` is midnight.

### Test dynamic local lights

Use `DynamicPointLightCount` and `DynamicSpotLightCount` to spawn smooth animated debug lights with random colors. `DynamicLightSeed` keeps the generated colors and motion repeatable. `DynamicLightsCastShadows` is enabled by default; when `DynamicLightsForceShadowAtlas` is also enabled, the bootstrap turns on the matching point and spot shadow atlas paths before adding the generated lights.

### Compare continuous rendering and idle reuse

Set `EditorCameraRenderOnDemand` to `true` to make the unit-test editor camera render the 3D scene only when input, camera motion, screenshots, or camera animations invalidate the view. Idle frames reuse the previous camera result, so this is useful for separating swap/present cadence from full scene rendering cost. Leave it `false` when measuring continuous scene render FPS.

### Test atmospheric scattering

Set `InitializeAtmosphericScattering` to `true` to spawn a default `AtmosphericScatteringComponent` and configure the active camera with `AtmosphericScatteringSettings`. Use the generated `AtmosphericScattering` object to tune ground radius, atmosphere height, sun source, scattering coefficients, aerial perspective distance, and quality/debug modes.

### Test XR startup

Use the grouped `VR` object to choose the launch mode explicitly:

| `VR.Mode` | Behavior |
|---|---|
| `Desktop` | No VR pawn and no VR runtime startup. |
| `Emulated` | Scene-only VR: builds the VR-shaped pawn and optional stereo preview without OpenVR/OpenXR runtime startup. |
| `MonadoOpenXR` | Real OpenXR API path against a Monado runtime selected per process. |
| `OpenVR` | OpenVR runtime path for SteamVR/OpenVR validation. |
| `OpenXR` | OpenXR runtime path using the active loader/runtime configuration. |

There are two no-headset VR lanes:

Lane 1 is scene-only VR. It builds the VR-shaped pawn, transforms, and stereo preview without entering the OpenXR API:

```jsonc
{
  "VR": {
    "Mode": "Emulated",
    "PreviewStereoViews": true,
    "AllowDesktopEditing": true,
    "OpenXrRuntimeJson": null
  }
}
```

Lane 2 is Monado-backed OpenXR. It uses the real OpenXR loader and a Monado runtime selected per process with `XR_RUNTIME_JSON`. You can leave `OpenXrRuntimeJson` as `null` to auto-detect common Monado install/build locations, or set it explicitly:

```jsonc
{
  "VR": {
    "Mode": "MonadoOpenXR",
    "PreviewStereoViews": true,
    "AllowDesktopEditing": true,
    "OpenXrRuntimeJson": "C:\\path\\to\\openxr_monado.json"
  }
}
```

`VR.Mode=Emulated` is scene-only. It does not emulate OpenXR API calls, runtime state, swapchains, poses, or frame submission. `VR.Mode=MonadoOpenXR` uses the real OpenXR loader/API with Monado as the selected runtime. Existing `XR_RUNTIME_JSON` environment values win over `VR.OpenXrRuntimeJson`; when both are empty, startup searches `MONADO_RUNTIME_JSON`, `MONADO_INSTALL_DIR`, common Monado install paths, and repo-local Monado build/dependency paths. In Monado mode, startup also prepends the detected `openxr_loader.dll`, Monado runtime, and Monado service directories to the current process `PATH`, then starts `monado-service.exe` when it is not already running.

On Windows, `VR.Mode=MonadoOpenXR` honors the requested `Rendering.RenderBackend`. The Vulkan path queries the selected OpenXR runtime before renderer creation and enables the Vulkan instance/device extensions required by the runtime, then renders each eye into the acquired OpenXR Vulkan swapchain image.

The repo-staged Windows Monado preview window has a right-click simulated HMD pose menu. It can switch between the default figure-eight transform test and a user-input HMD mode. In user-input mode, `W`/`S` move forward/back, `A`/`D` move left/right, `Q` moves up, `E` moves down, up/down arrows control elastic pitch, left/right arrows add persistent yaw, and `,`/`.` control elastic roll. Hold Shift while yawing to turn faster. Position, pitch, and roll ease back to their rest values when released.

When VR is active, desktop output is controlled by `Rendering.VrMirrorMode`.
The default performance posture is a cheap mirror: `BlitSubmittedEye` keeps
`RenderWindowsWhileInVR=true` but composes the desktop window from the submitted
XR eye output instead of rendering a second full scene. Set
`Rendering.VrMirrorMode=Off` for eye-submit-only perf runs. Select
`FullIndependentRender` only when editor diagnostics need a separate desktop
scene path; with `VR.AllowDesktopEditing=true` that path owns its own visibility
group and render commands. `LowRatePreview` keeps the legacy mono/cyclopean
runtime-camera path available at an explicitly paced desktop rate, and
`CyclopeanReconstruct` is reserved for the depth-aware mirror reconstruction
path.

Useful tasks:

- `Install-Monado`
- `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`
- `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`
- `Test-OpenXR-Monado-Smoke`
- `Test-OpenXR-SteamVR-Smoke`
- `Test-OpenXR-SceneOnlyVR-Smoke`

Monado does not currently provide a generic Windows binary installer, so the repo tool builds and stages it from source:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Install-Monado.ps1 -InstallPrerequisites
```

The script clones Monado into `Build\Submodules\monado`, builds it with CMake/Ninja/vcpkg, installs/stages it under `Build\Deps\Monado`, writes `Build\Deps\Monado\monado-env.ps1`, ensures the Khronos `openxr_loader.dll` is available through vcpkg, and copies that loader into the current editor output when the output directory exists. It does not write the Windows OpenXR active-runtime registry key. `ExecTool` option 42 and the `Install-Monado` VS Code task pass `-InstallPrerequisites` automatically so CMake, Ninja, Python, or the Vulkan SDK can be installed through `winget`; Visual Studio 2022 Build Tools with the C++ workload is still required.

Command-line Monado smoke example:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1 -RuntimeJson C:\path\to\openxr_monado.json -SmokeFrames 120
```

On Monado's Windows no-HMD runtime, a passing smoke can complete OpenXR frames
with `ShouldRender=false`; these are counted as no-layer `xrEndFrame` calls in
the summary. After the summary is written, the runner treats it as the
authoritative pass/fail result and may terminate a lingering editor process
that has already finished the smoke contract.

For script-driven launches, these process-scoped overrides select the lane without editing `Assets/UnitTestingWorldSettings.jsonc`:

- `XRE_UNIT_TEST_VR_MODE=Desktop|Emulated|MonadoOpenXR|OpenVR|OpenXR`
- `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=1`
- `XRE_UNIT_TEST_ALLOW_DESKTOP_EDITING_IN_VR=0`
- `XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR=0`
- `XRE_UNIT_TEST_OPENXR_RUNTIME_JSON=C:\path\to\openxr_monado.json`
- `XRE_UNIT_TEST_RENDER_API=OpenGL|Vulkan` maps into `Rendering.RenderBackend`

For highest-framerate Monado Vulkan validation, set `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=0`, `XRE_UNIT_TEST_ALLOW_DESKTOP_EDITING_IN_VR=0`, and `XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR=0` so the editor does not also render the smoothed HMD desktop camera while submitting OpenXR eye frames.

### Test SteamVR OpenXR hardware

Use the SteamVR hardware lane when a real SteamVR headset is connected and SteamVR is the intended OpenXR runtime. This lane is separate from Monado and uses `VR.Mode=OpenXR`; it does not install Monado service recovery callbacks and it does not fall back to OpenVR.

VS Code entry points:

- `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug`
- `Test-OpenXR-SteamVR-Smoke`
- `Editor (Unit Testing OpenXR SteamVR)`

Primary smoke command:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
```

Explicit runtime manifest command:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -RuntimeJson "C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json" -Renderer Vulkan
```

OpenGL can be checked with `-Renderer OpenGL`, but SteamVR OpenXR OpenGL session creation is known to be runtime/driver sensitive. When OpenGL fails and Vulkan succeeds, keep the Vulkan row as the primary hardware lane and record the OpenGL failure diagnostics in `docs/work/testing/`.

The SteamVR smoke writes:

- `reports/steamvr-openxr-startup-diagnostics.json`
- `reports/openxr-loader-preflight.json`
- `reports/openxr-steamvr-smoke-summary.json`
- `reports/openxr-steamvr-smoke-summary.normalized.json`
- `logs/editor.stdout.log`
- `logs/editor.stderr.log`

Record each hardware matrix pass under `docs/work/testing/` with the headset model, controller models, tracker count/roles, expected hand/finger data, runtime version, renderer, summary path, and relevant `Build/Logs/...` session path.

### Test networking pose sync

For the built-in pose-sync scenario, use the existing VS Code tasks instead of setting every variable by hand:

- `Start-PoseServer-NoDebug`
- `Start-PoseSourceClient-NoDebug`
- `Start-PoseReceiverClient-NoDebug`
- `Start-LocalPoseSync-NoDebug`

Those tasks launch the editor in `UnitTesting` mode with `WorldKind=NetworkingPose` and the correct role-specific environment variables.

If you do need to launch it manually, the important environment variables are:

- `XRE_WORLD_MODE=UnitTesting`
- `XRE_UNIT_TEST_WORLD_KIND=NetworkingPose`
- `XRE_NETWORKING_POSE_ROLE=server|sender|receiver`
- `XRE_NET_MODE=Server|Client`
- `XRE_UDP_SERVER_BIND_PORT=5000`
- `XRE_UDP_SERVER_SEND_PORT=5000`
- `XRE_UDP_CLIENT_RECEIVE_PORT=5001|5002`
- `XRE_POSE_ENTITY_ID=4242`
- `XRE_POSE_BROADCAST_ENABLED=0|1`
- `XRE_POSE_RECEIVE_ENABLED=0|1`

## Regenerating the schema and mirrored files

If the settings type changes in code, regenerate the schema and JSONC outputs:

```powershell
pwsh Tools/Generate-UnitTestingWorldSettings.ps1
```

If PowerShell 7 is not installed, use Windows PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Generate-UnitTestingWorldSettings.ps1
```

Or run the VS Code task:

```text
Generate-UnitTestingWorldSettings
```

This refreshes the schema and keeps the generated JSONC files aligned with the current settings type. The root `Assets/UnitTestingWorldSettings.jsonc` file remains local-only.

## Troubleshooting

### The editor is not reading my settings

The editor loads `Assets/UnitTestingWorldSettings.jsonc` relative to the process working directory. In the provided VS Code launch profiles, the working directory is set to the workspace root on purpose.

If you launch the editor some other way, make sure the working directory is the repo root, or the settings file may not be found where you expect.

### I changed the settings type and now the JSONC file looks stale

Run `pwsh Tools/Generate-UnitTestingWorldSettings.ps1`, `powershell -ExecutionPolicy Bypass -File Tools\Generate-UnitTestingWorldSettings.ps1`, or the `Generate-UnitTestingWorldSettings` task.

### I want hover docs and enum completion in VS Code

Make sure the file still points at `.vscode/schemas/unit-testing-world-settings.schema.json` through its `$schema` property.

## Related documentation

- [Documentation Index](../README.md)
- [Architecture Overview](../../architecture/README.md)
- [Rendering](../../user-guide/rendering.md)
- [VR Development](../../user-guide/vr-development.md)
