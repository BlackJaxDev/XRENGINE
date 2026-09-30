# Native physics module validation

The runtime now registers PhysX and Jolt through a physics module catalog. The Jitter implementation builds as an opt-in leaf and is absent from default application composition. Core retains authored physics components and contracts; native backend code and packages live in separate projects.

## Desktop build evidence

- `dotnet build XREngine.Runtime.Core/XREngine.Runtime.Core.csproj --no-restore -v:q` passed with zero warnings.
- Standalone builds of the Jolt, PhysX, Jitter, and Authoring projects passed with zero warnings.
- `dotnet build XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj -v:q` and `dotnet build XREngine.Editor/XREngine.Editor.csproj -v:q` passed with zero warnings before the later media moves in this branch.

## PhysX editor run

The isolated `native-split-physics` editor session loaded the Physics Testing World in Playing state. MCP listed the physics floor, dynamic box stack, joints, and controller course. `log_physics.log` records numerous `PhysxDynamicRigidBody` registrations with the expected collision group and mask. No physics exception or error appeared in that log.

Two viewport captures from distinct camera positions under `Build/_AgentValidation/20260929-160000-native-subsystem-split/mcp-captures/` were viewed and were fully black. The Vulkan log records a terminal `VulkanPresentNowReadinessException` at frame 94 during prepared-mesh ingress (`PackageExceptionUnmatchedHandle`), after physics actors registered. The black viewport therefore does not establish visual physics correctness. The renderer fault is separate from the physics module split and needs its own rendering investigation if it reproduces outside this run.

The named session was stopped through `Tools/Manage-McpEditorSession.ps1`.

## Jolt follow-up

The first planned Jolt/OpenGL Physics Testing run did not launch. Its isolated build stopped in `Tools/Generate-AotFactoryRegistrations.ps1` because that generator still read the moved `XREngine.Runtime.Rendering/VideoStreaming/HlsReferenceRuntime.cs` path. The build log is under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260929-130946-native-split-physics-jolt/build.log`. The media facade was restored and the generator now resolves that source.

A second launch attempt stopped during concurrent imaging and audio project extraction; the build log is under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260929-131614-native-split-jolt-retry/build.log`. The physics height-field dependency on ImageMagick was replaced with an installed image-source contract.

After the integrated Editor build passed with zero warnings, isolated session `native-split-jolt-final` started with OpenGL, the default render pipeline, and Jolt selected. MCP reported the Physics Testing World in Playing state and listed the static environment, box stack, joints, and controller course. The general log records successful Jolt `PhysicsSystem` creation and initialization, and `log_physics.log` contains no error or exception. Two captures from distinct camera positions were viewed at `Build/_AgentValidation/20260929-160000-native-subsystem-split/mcp-captures/Screenshot_20260929_133151_710_1f756d4cd9984021ac1951fe36f9cd5a.png` and `Screenshot_20260929_133207_768_c7e0eab7294d44b795c7dbd9c56ae707.png`; both show the rendered physics scene. The editor output includes `runtimes/win-x64/native/joltc.dll` and the PhysX/CoACD native libraries. The session logs and output are under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260929-132856-native-split-jolt-final/`.

The named session was stopped through the manager. The ignored unit-testing-world settings were restored from the task-run backup to Vulkan, AdvancedRenderPipeline, and PhysX.

## Targeted tests and parity limits

The five named Jolt parity/hardening/integration suites plus `PhysicsBackendContractTests` passed 55/55 with no skips. PhysX shape mutation passed 1/1, dynamic-body lifetime 1/1, scene serialization 9/9, and geometry/debug/boundary tests 19/19. Convex-authoring and source API contracts passed 14/14 after the test fixture installed the optional authoring service.

These results establish module loading and the tested behaviors, but they do not complete the Jolt default-promotion gates. The coverage inventory is:

| Contract area | Current evidence | Remaining proof before default promotion |
| --- | --- | --- |
| Static and dynamic bodies | Drop-box/floor test and live scene | Kinematic behavior across load/reload and solver parity |
| Compound, convex, mesh, height-field geometry | `JoltGeometryParityTests`, including authored poses and metadata | Representative cooked-world reload and PhysX comparison |
| Ray, sweep, overlap queries | `JoltQueryParityTests` cover ordering, layer and actor-type filtering | Cross-backend result tolerances on a shared world fixture |
| Fixed, distance, hinge, prismatic, spherical, D6 joints | Factory/configure/step/release integration cases | Limits, motors, breaking, and long-running stability across backends |
| Character controller | Grounding, steps, slopes, arbitrary up, moving platforms, filtering, and contact tests | Remaining optional inner-body and interaction work in the character-controller correctness plan; matched PhysX trajectories |
| Contacts, debug frames, serialization, replication | Native contact/debug extraction and collider-asset YAML round trip are tested | Full scene reload, authority-field behavior, contact-event equivalence, and browser execution |

The shared contract fixture currently covers desktop catalog selection, scene lifecycle, gravity, and an empty step for installed Jolt and PhysX modules. It does not imply the broader behavior above is equivalent. Jitter remains an uninstalled experimental module; the fixture checks its named missing-module diagnostic.
