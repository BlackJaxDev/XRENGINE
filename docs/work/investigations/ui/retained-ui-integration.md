# Retained UI Integration

Status: Active
Date: 2026-10-23
Code scope: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Environment And Scope

Validation uses the existing Linux checkout and its installed .NET toolchain.
All builds use `Tools/Invoke-AgentBuild.ps1`. NUnitLite runs the already built
headless project with `--no-build`. No Windows execution, graphics output, native
IME session, or equivalent ImGui benchmark has run here. The owner approved new
headless regression tests before live validation on 2026-10-23.

## Integration Evidence

The following results describe individual source checkpoints. They do not certify
later edits. The validation document must identify the final checked source.

| Checkpoint | Result | Finding |
|---|---|---|
| Initial expanded headless run | 199 passed, 1 failed, 200 total | World-space input accepted a raw pointer outside the output region. The pointer mapper now rejects it unless capture explicitly permits outside movement. |
| Headless build 11 | Passed; zero warnings and errors | Runtime, rendering, input, and linked test code compiled. Test fixtures use the public world-context setter. |
| Headless run 2 | 222 passed, 8 errors, 230 total | New active UI fixtures lacked a shader-asset service. Scoped test shader assets were added. |
| Editor build 5 | Passed; zero warnings and errors | Complete managed Windows-target editor build on Linux, including Bootstrap, desktop platform, OpenGL, and Vulkan assemblies. |
| Headless build 12 | Passed; zero warnings and errors | Includes the scoped shader fixture and pure dock-layout tests. |
| Headless run 3 | 226 passed, 6 errors, 232 total | Five active-canvas fixtures needed an explicit scheduling host. The remaining failure exposed a desktop material assignment defect. |
| Headless builds 13–15 | Failed | Explicit draw copies replaced unsupported reflection. Later attempts found tooltip namespace/access errors, a DPI local-name collision, and two test compilation errors. These errors were corrected. |
| Headless build 16 | Passed; zero warnings and errors | The recovered source compiled after the environment interruption. No active build remained at recovery. |
| Headless run 4 | 242 passed, 7 errors, 249 total | Six fixtures needed a scoped numeric font. One batching check found that generic transparency setup enabled depth testing and could replace the owned UI shader. Both fixes are in source for the next run. |
| Editor build 6 | Failed; zero warnings, one error | Runtime, both graphics backends, desktop, and Bootstrap compiled. The editor used a non-text date control as a text-editor generic argument. It now uses the retained text field and typed date binding. |

| Headless builds 17–18 | Failed | A mip revision width, context menu namespace, nullable font access, and test buffer constructor needed correction. |
| Headless build 19 | Passed; zero warnings and errors | Includes owned draw payloads, font snapshots, scopes, and controls. |
| Headless run 5 | 258 passed, 4 errors, 262 total | A stretch-anchor fixture expected fixed coordinates. Empty test shader sources lost their parsed uniforms. Material normalization also removed explicit array parameters. The next run uses real repository shaders and preserves owned parameter arrays. |
| Editor build 7 | Failed; zero warnings, two errors | Native tab callbacks captured two local arrays before assignment. Subscription now follows initialization. |
| Editor build 8 | Passed; zero warnings and errors | Complete managed editor build, including detached-window source. No window was opened. |
| Headless build 20 | Failed; zero warnings, one error | A batch test needed the `ShaderVector4` namespace import. Runtime and input compiled. |
| Headless build 21 | Passed; zero warnings and errors | Includes the wrapped-text repair, focus registration ownership, owned texture claims, and pure receipt state. |
| Headless run 6 | 270 passed, zero failed or skipped | Includes all new regression checks. The later required batch-texture binding change is compiled by the next editor check. |
| Editor build 9 | Passed; zero warnings and errors | Includes required texture-binding publication and the hierarchy split with canonical mutation paths. |
| Settings generator build and run | Passed | The wrapper build passed with zero warnings/errors. `dotnet run --no-build` regenerated the schema and both ignored settings files. The schema also reflects the existing `WebAudio` enum value. |
| Headless build 22 | Failed; zero warnings, one error | A new draw test needed the canvas component namespace import. Runtime and input compiled. |
| Headless build 23 | Passed; zero warnings and errors | Includes review repairs for capture re-entry, producer teardown, source invalidation, shared receipt retirement, and focus teardown. |
| Headless run 7 | 275 passed, zero failed or skipped | All linked tests passed. No native renderer or platform runtime was exercised. |
| Editor build 10 | Passed; zero warnings and errors | Final checkpoint managed editor compilation after the review repairs; 3 minutes 7 seconds. |

Disposable logs and NUnit XML are under the task's `Build/_AgentValidation/` run.
These artifacts are not required to build or test the repository.

## Root Causes And Repairs Under Verification

- `UIMaterialComponent` constructed a mesh without assigning the material to its
  base component property. Desktop batching rejected the default quad. The
  constructor now assigns the owned material, and the regression uses the desktop
  material path.
- An IME event's focus token was compared to the pointer interaction revision.
  Focus epochs and recycle revisions have separate purposes. Dispatch now checks
  each token against its matching owner state.
- Layout task completion released its working-page quota before the owner consumed
  the result. The lease now waits for both worker completion and owner retirement.
- Layout callbacks could re-enter capture, dispatch, or application. Owner guards,
  epoch checks, terminal cancellation, and callback regression tests address those
  paths. Completed pages must retire even if a disabled canvas never pumps again.
- Mutable font glyph objects could bypass snapshot revisions. The font now owns
  copies of authored glyph maps and caches immutable metric pages. Runtime readers
  use the owned data. An edited authoring copy must be assigned back to commit it.
- A layout request drained dirty changes before it could reserve a working page.
  Capacity refusal could leave later sparse capture stale. Admission now precedes
  the drain, and failed capture requires a full resynchronization.
- Applying derived size and position changed authored transform dependencies.
  Derived geometry now changes visual, clip, and hit revisions. Authored scale,
  rotation, and position still invalidate their layout dependencies.
- Wrapped text measured against the canvas width can reserve the wrong height
  inside a narrower parent. Operator-specific constraints and bounded remeasurement
  are in source for the next regression run.

## Open Rendering Proof

A GPU fence alone does not release storage referenced by a reusable Vulkan command
artifact. UI ownership must continue through raw requests, prepared operations,
recorded artifacts, submission, presentation, replay, and native command reset.
The output's hit publication must identify the generation actually shown.

A copied render command does not freeze its material, mesh, glyph buffers, shader
definitions, or callbacks. The implementation needs owned draw payloads and
producer-generation leases for mutable viewport and video surfaces. Capacity or
preparation failure must reject a complete candidate with an explicit reason.
These checks remain open until the corresponding implementation and tests pass.

## Checkpoint Review Findings

- Capture adapters can change UI while a candidate is being collected. A final
  lifetime, identity, layout, journal, and painter-order check rejects such a candidate.
- Focus-loss callbacks can request new focus during deactivation. The input
  component now rejects new focus and registration until activation.
- Source textures, shaders, uniforms, and mesh data need publication invalidation.
  Owned capture observers and an explicit raw-storage invalidation API passed their headless regressions.
- Video and web teardown must wait for an active render callback, including
  re-entrant destruction on the render thread. The source review verified the serialized teardown fix; live producer teardown remains untested.
- Publication finalizers can delay resource release and stall bounded producer
  pools. This is a known liveness limit. Deterministic publication holder/reader
  retirement remains implementation work; enlarging pools or forcing collection
  is not the fix.
