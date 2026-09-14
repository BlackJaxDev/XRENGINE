# Vulkan 1.4 bounded code closeout — 2026-09-13

The requested scope is the remaining Phase H implementation and other planned
code in the modernization TODO. Broader Slang conversion and the full validation
and performance campaign are deferred. This note records code admission and
small runtime checks, not completion of H6/I/J or a performance promotion.

**Subsequent closeout:** the [2026-09-14 final validation](vulkan14-final-validation-2026-09-14.md)
supersedes this note's pending water/H6/I/J status. It records the additional
framebuffer/reload fixes, viewed runtime qualification, measured policy decisions
and explicit D4/I2/I3 limits. The earlier scope and observations below are retained
as the history of the bounded code-only pass.

## Implemented behavior

| Task | Code and decision |
| --- | --- |
| H3 | Native Slang descriptor merging compares the full physical/semantic resource ABI; incompatible native contracts and native/uncontracted sharing fail. The artifact cache version is 7. Earlier ABI/GPU evidence remains in the native Slang investigation. |
| H6 attribution | Optional bounded shader command accounting is hooked at engine GL draw/dispatch issue and Vulkan command recording. Linked-program tokens identify authored source hash/revision, language, stage and entry point. Direct/indirect and known/unknown instancing counts are distinct. MCP configure/query/reset actions expose the data and exclusions. |
| H6 capture | Generic OpenXR eye screenshots use the retained selected-eye preview on the session's owning renderer. Per-eye copy frame IDs are invalidated on failed attempts/resource destruction, and published after the copy API succeeds. Capture verifies availability, active session, renderer ownership and latest rendered stereo generation. |
| I1 | Physical-device ordinary/video unified-layout features are queried separately. RenderBench `--layout-policy general` explicitly enables only the ordinary feature, rejects unsupported devices and non-GPU-pass profiles, and checks actual host enablement. The default remains `specialized`. Both variants retain memory dependencies, initialization and final transfer transitions. |
| I2 | Query/report `VK_KHR_device_address_commands` independently of BDA. Defer an operand implementation until the extension/feature and a suitable workload are available. No address-command feature or entry point is enabled. |
| I3 | Native buffer diagnostics expose the actual tracked allocation's VMA memory type/heap, property flags, mapped/coherent/device-local status and owner kind. Existing placement and completion lifetime remain in use; measurements decide any later placement change. |
| D4 | Keep the rejected barrier candidate reverted. Reopen specialization only with the producer/consumer evidence, scheduling-gap access and matched GPU measurements required by the barrier investigation. No phase number or date triggers an automatic change. |

The two existing Vulkan Slang pilots remain opt-in. GLSL/OpenGL remain independent
of Slang availability. No additional shader port, generated OpenGL Slang route,
Vulkan tessellation capability, dependency update or test method was added.

## XR capture failure and correction

True single-pass stereo releases the old per-eye pipeline. Generic eye capture
previously entered that pipeline's Vulkan readback scope, which threw because
there was no matching resource-planner generation. The exception escaped the
window callback after it unsubscribed, leaving the request's completion source
pending until the MCP timeout.

The shared screenshot scheduler now catches scheduling/scope/readback failures,
preserves caller cancellation, removes pending callbacks and applies a 20-second
deadline. It does not interrupt cleanup already using GPU resources. Late image
results are disposed rather than returning success to a cancelled request.

Explicit OpenXR left/right requests go directly to engine-owned preview copies;
they cannot fall through to a desktop/null framebuffer. Copies must be enabled
with `VrCopyEyePreviewTextures`, and a selected-eye copy frame must be available.
Desktop and XR cadence differ, so freshness compares to the last rendered stereo
frame rather than the current desktop frame counter. The response records
`preview_copy_frame_id`. This is API-issued/recorded copy provenance; successful
readback supplies the captured pixels. Eye previews are 2D, and nonzero layer,
window or viewport indices fail explicitly.

## Narrow validation

All disposable evidence is under
`Build/_AgentValidation/20260910-060112-vulkan14-h/`. Required findings are copied
here because that ignored directory may be reclaimed.

- Isolated Release editor and RenderBench builds passed with zero warnings and
  errors. `git diff --check` passed. No tests were added or changed. The MCP table
  generator completed; its normal-output build reported existing assembly-version
  resolution warnings, separate from the clean isolated builds.
- In a normal OpenGL desktop cohort with Slang deliberately unavailable, the
  collector registered 43 authored stage identities and counted commands for 24,
  with zero dropped registrations. These included the water TCS/TES/fragment,
  forward-plus light-culling compute, detail-preserving mip compute, shadow and
  post-process paths. Camera-controlled desktop/UI screenshots were viewed.
- The Vulkan Monado control registered 49 identities with no dropped
  registrations. The list includes linked-but-unused stages, which must not be
  classified as exercised without nonzero counters. Vulkan counters are recorded
  commands; submission/completion and images remain separate evidence.
- Generic `capture_viewport_screenshot(vr_eye: left|right)` succeeded for both
  896×1007 Monado Vulkan eyes. Both PNGs were viewed and show different eye
  perspectives of the box cohort. The reported copy frame IDs were 370 and 375.
  Disabling preview copies produced an immediate actionable error; the setting
  was restored. The profiler snapshot reported zero Vulkan validation messages
  and errors. This used an already-running Monado service; only the named editor
  sessions created for this work were stopped.
- A four-pass 128×128 I1 GPU fixture smoke used 12 warmup frames, 3 stability
  frames and 8 captured frames per policy. Both policies passed every fixture
  stability gate, including zero capture/worker allocation budget, expected work,
  GPU-query drain, device health and required output hash. Each recorded 32 draws,
  8 submissions, 8 command buffers, 64 barriers and 32 pass iterations. Both
  outputs hashed to
  `40DD0A1DC82C9CDEA098691E35F0591CB193BADEA096B200E86433AC4031AB1F`.
  Effective-configuration hashes differed with the policy as required. A GENERAL
  request for the clear fixture failed explicitly with exit code 1.

These small runs verify the new paths. Their timings are not an I1 promotion
result. The checked-in eight-pass recipe and paired workflow are in the
[layout-policy guide](../../../developer-guides/rendering/renderbench-layout-policy.md).
The RTX 3090 inventory (API 1.4.341, driver 610.88) advertises unified layouts with
ordinary/video features true, but does not advertise device-address commands.
The native query structs were reviewed against Vulkan-Headers 1.4.350.

## Remaining visual and qualification work

H6 is still open. Command issue/recording, successful module creation and shader
asset loading are insufficient proof that all supported shaders produce correct
output. The collector explicitly excludes raw native advanced commands without
XRShader identity, unscoped GL multi-draw, Vulkan native mesh/task recording and
cached command replay. Its authored-source hash does not identify all compiled
permutations. Full validation must combine an attributed corpus matrix with viewed
outputs, reload/mutation controls and actual submission/completion evidence.

The earlier white OpenGL water control used log-average auto exposure against a
mostly black scene. Its exposure texture reached approximately 100 while the HDR
scene remained finite. Manual exposure 1 made the underwater floor and reference
objects readable, but the water surface remained absent despite issued TCS/TES/FS
commands. Disabling separable programs did not make the surface appear. Thus this
is an unresolved visual issue in the authored GLSL control, not an accepted water
result. No speculative shader or pipeline workaround was retained.

RenderDoc startup injection produced an incomplete black scene with only AO/UI
draws, including a retry requesting synchronous compilation and disabled binary
caching. Normal runs without injection rendered the scene. Those captures are not
water-pass evidence, and the capture/tooling difference remains to be investigated.
Both RenderDoc inspection sessions were closed.

Full closeout still needs the H6 visual/corpus result (including water), repeated
I1 comparisons on a suitable workload, I3 traffic/placement measurements, and J1
final outcome documentation. D4 and I2 retain their explicit evidence/hardware
gates. The user has not yet reported whether these changes work in their session.

## References

- [Modernization TODO](../../todo/rendering/vulkan-14-performance-and-shader-modernization-todo.md)
- [Native Slang implementation and pilot evidence](vulkan14-native-slang-2026-09-10.md)
- [Barrier investigation and D4 admission gates](vulkan14-phase-d-waits-and-barriers-2026-09-09.md)
- [MCP diagnostics and captures](../../../developer-guides/ai/mcp-server.md#shader-command-coverage-and-eye-captures)
- [Khronos unified-layout proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_unified_image_layouts.html)
- [Khronos device-address-command proposal](https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_device_address_commands.html)
