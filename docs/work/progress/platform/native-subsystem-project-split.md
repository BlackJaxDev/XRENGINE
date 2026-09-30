# Native subsystem project split progress

Status: native extraction and whole-project portability are implemented in source. The owner deferred builds, tests, runtime validation, and debugging until the code work is complete. Physics default promotion remains gated by the owner decision and browser/parity acceptance.

Tracks [the remaining debugging and validation checklist](../../todo/platform/native-subsystem-project-split-todo.md).

## Documentation and source records

The completed project map, dependency direction, composition roots, native asset ownership, and identity rules are maintained in [Runtime Project Organization](../../../architecture/runtime/project-organization.md). Whole-project compilation and guard behavior are documented in [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md). These are the current architecture references; this page preserves checkpoint evidence and limitations.

- [Portable kernel audit](portable-kernel-audit.md): reviewed packages, reflection allowances, and pending qualification.
- [Type identity audit](native-subsystem-type-identities.md): moved CLR names and persisted-name searches.
- [Audio checkpoints](../audio/native-audio-project-split.md) and [physics investigation](../../investigations/physics/native-subsystem-project-split.md): earlier live observations.
- `Tools/Reports/NativeSubsystemInventory/`: reproducible lexical API/package/native-item and public-identity inventory; evaluated build/publish inspection remains required.

## Evidence and limits

Evidence root: `Build/_AgentValidation/20260929-160000-native-subsystem-split/`. Evidence is disposable; this document records the findings needed to resume.

- Standard targeted builds passed with zero warnings for input leaves, physics leaves, collider authoring, NAudio, Steam Audio, DirectStorage, desktop interop, Rive, Ultralight, media, imaging, FreeType, and Skia at their respective coherent checkpoints. Later source migrations require integration revalidation.
- Isolated PhysX editor session reached a playing physics scene and registered native bodies. Vulkan presentation paused with an unrelated prepared-mesh ingress error, so black screenshots do not establish visual physics correctness. A separate Jolt/OpenGL session reached Playing, initialized the Jolt system without errors, and produced viewed geometry captures from two camera positions.
- Jolt named parity/hardening suites and the current neutral contract selection passed 55/55. PhysX shape mutation, lifetime, serialization, geometry/debug/boundary selections passed 30/30. Convex authoring and source API selections passed 14/14. Broader joint, reload, authority, and browser promotion gates remain open.
- Separate isolated legacy OpenAL/EFX and V2 Steam Audio sessions confirmed one listener and a static source in `Playing` state. Steam logs confirmed processor initialization, committed acoustic geometry, and 36 registered probes. Audio tests passed 191/191. Hardware capture and live voice acceptance remain open.
- Native image/media/font smoke passed PNG round-trip, four heightfield samples, character enumeration, two atlas glyphs, and FFmpeg initialization/session construction. It did not establish HLS playback or GPU screenshots.
- Test source work was explicitly cleared by the owner after live runtime checks. Dependency graph contracts, backend fixtures, image consumers, and window ownership source fixtures are updated. Those updates have not been executed in this source-only pass. Native tests distinguish unavailable capabilities from missing registration.
- A relative intermediate-output override placed generated build files inside project source trees. Those exact generated directories were relocated to the task evidence root. The normal compile glob now excludes validation artifacts; subsequent validation must retain normal assembly attribute generation and use absolute isolated output paths.

## Integration qualifications

- Native leaves consume shared neutral contracts; they do not reference each other. Renderer-specific UI/XR implementations receive contracts in lower layers rather than references to another leaf.
- Existing native versions and managed supply paths stay unchanged during extraction. Native copy declarations transfer to the owning leaf while preserving executable layouts.
- Dependency license override keys now follow the moved project/item paths while retaining their existing license, owner, link, and review notes. The generated inventory still exposes pre-existing unresolved notices for the OpenVR submodule binary, the optional Audio2X bridge output, and the checked-in Steam Audio binary; this pass does not reclassify them or authorize distribution.
- `System.Drawing.Primitives` value types remain portable. The source policy distinguishes them from Windows bitmap/image/graphics APIs.
- The portability guard covers normal project sources plus the exact generated render-command registry. Reflection allowances identify individual files and symbols; native APIs, source filtering, unreviewed packages, and native assets remain rejected. Source review does not prove that the guard or browser build passes.
- Public moved names, nested OpenXR enum/delegate identities, and serialized backend enum values are preserved. Runtime-facing native signatures intentionally become neutral contracts. No persisted full-name instance requiring an asset rewrite was found in the documented source audit; live loading remains pending.
- Vulkan secondary GPU contexts now report unsupported ownership explicitly. XR retirement after a partial post-detachment failure keeps native parents pinned and reports a permanent recovery diagnostic; integration validation must exercise successful retirement, retry, and device-loss abandonment.

## Open acceptance

The deferred pass must run the evaluated graph/package/native asset audit, targeted tests, live scene and serialization checks, all application builds/publishes, cooked AOT launcher checks, browser Jolt spike, and whole-project `browser-wasm` builds. Earlier checkpoint results above do not establish correctness of the final integrated source. Physical headset and hardware audio/voice checks remain manual acceptance.

The pinned browser Jolt build, owned single-threaded C ABI wrapper, and throwaway WebAssembly drop/raycast/teardown harness are prepared in `Tools/Dependencies/JoltBrowser/`. No native source was acquired or built in this pass. See the [native supply proposal](../../design/platform/jolt-browser-native-supply.md). The proposal still needs the owner decision, ABI/callback/linkage and no-pthread proof, and complete parity gates before changing defaults. Existing and new desktop defaults therefore remain PhysX. The optional Box3D comparison was not authorized; its integration file map now targets its future leaf project.
