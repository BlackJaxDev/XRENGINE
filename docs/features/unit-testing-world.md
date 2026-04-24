# Unit Testing World

The Unit Testing World is the main sandbox for validating engine changes inside the editor. It is driven by a JSONC settings file so you can switch test scenes, turn systems on and off, and adjust startup behavior without recompiling.

## Canonical settings file

- Main file: `Assets/UnitTestingWorldSettings.jsonc`
- Server mirror: `XREngine.Server/Assets/UnitTestingWorldSettings.jsonc`
- VS Code schema: `.vscode/schemas/unit-testing-world-settings.schema.json`

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
- `VRPawn`, `UseOpenXR`, `EmulatedVRPawn`: control XR startup behavior
- `AddPhysics`, `PhysicsBallCount`, `PhysicsChain`: enable physics-focused scenarios
- `ModelsToImport`: imports test assets into the world at startup
- `GPURenderDispatch`: switches rendering dispatch mode
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

`FbxLogVerbosity` controls how much native FBX importer/exporter trace output is emitted while the unit-testing world boots. When enabled, those lines go through the engine `Assets` log category, so they show up in the editor console `Assets` tab and in `Build/Logs/.../log_assets.txt` when file logging is enabled. glTF does not currently expose a separate verbosity toggle; native glTF warnings and fallback diagnostics also flow through the normal asset-import logging path.

This is the fastest way to spin up repeatable import tests without hand-building the scene each time.

### Test XR startup

Use:

- `VRPawn: true`
- `UseOpenXR: true` or `false`
- `EmulatedVRPawn: true` if you want the VR pawn flow without requiring a headset session

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

Or run the VS Code task:

```text
Generate-UnitTestingWorldSettings
```

This refreshes the schema and keeps the generated JSONC files aligned with the current settings type.

## Troubleshooting

### The editor is not reading my settings

The editor loads `Assets/UnitTestingWorldSettings.jsonc` relative to the process working directory. In the provided VS Code launch profiles, the working directory is set to the workspace root on purpose.

If you launch the editor some other way, make sure the working directory is the repo root, or the settings file may not be found where you expect.

### I changed the settings type and now the JSONC file looks stale

Run `pwsh Tools/Generate-UnitTestingWorldSettings.ps1` or the `Generate-UnitTestingWorldSettings` task.

### I want hover docs and enum completion in VS Code

Make sure the file still points at `.vscode/schemas/unit-testing-world-settings.schema.json` through its `$schema` property.

## Related documentation

- [Documentation Index](../README.md)
- [Architecture Overview](../architecture/README.md)
- [Rendering Architecture](../api/rendering.md)
- [VR Development](../api/vr-development.md)
