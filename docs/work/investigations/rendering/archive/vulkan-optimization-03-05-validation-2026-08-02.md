# Vulkan Optimization 03-05 Validation Investigation

Last Updated: 2026-08-04
Owner: Rendering / Vulkan / Performance Validation
Status: Paused during the directional-light Vulkan stability pass; final Phase
0 allocation closure and the 03-05 matrix remain open
Related Gate: [Vulkan Optimization Workstreams 03-05 Validation TODO](../../../testing/rendering/03-05-optimization-validation-todo.md)

Current-focus boundary: the directional-shadow subsection below is retained as
historical closeout evidence. Any current directional cascade or atlas issue is
owned by the
[Directional Light Vulkan Stability Investigation](../directional-light-inspector-shadow-2026-08-03.md).

## Problem Statement

Execute the consolidated workstream-03 through workstream-05 gate far enough
to determine whether the remaining work is validation-only and whether
workstream 06 can begin. Use a live isolated Vulkan editor session and stop at
the first implementation defect instead of paying for the canonical matrix
against a known-invalid baseline.

## Run Configuration

- Date: 2026-08-02 America/Los_Angeles.
- Named editor session: `broker-03-05-validation`.
- Editor mode: Debug Unit Testing World, Vulkan, 1920x1080, ImGui editor.
- MCP policy: `AllowReadOnly`; no scene, settings, asset, or repository mutation.
- Active submission configuration: `GPURenderDispatch=false`.
- Screenshot artifact:
  `Build/_AgentValidation/mcp-sessions/broker-03-05-validation/mcp-captures/Screenshot_20260802_172419_915_817407601a3d40acb552c6a91bbed5c2.png`.
- Continuation sessions: `validation-03-05-final` for the artifact/allocation
  read-back and camera-separated visual smoke, and
  `validation-03-05-lifecycle` for a fresh async-pipeline startup after the
  dynamic-UI command-buffer fix.
- Continuation screenshots:
  `Build/_AgentValidation/mcp-sessions/validation-03-05-final/mcp-captures/Screenshot_20260802_203152_556_c15cf806b9c14300a3b99124c236450b.png`
  and
  `Build/_AgentValidation/mcp-sessions/validation-03-05-final/mcp-captures/Screenshot_20260802_203229_966_61a69134255d472b88c7f5489b37eef5.png`.
- Local-agent-broker run: `aac2a7d9d921463e8e63445d5cc2d2a6`.
  Requested model and actual model were both `gpt-5.6-sol`; the run completed
  in 54,062 ms with one read-only `get_render_profiler_stats` call, two turns,
  and 12,850 total tokens.

The Codex-owned broker MCP process had started before the user-scoped API key
was available and rejected two pre-run requests without producing a run ID or
incurring API usage. The documented manual stdio launcher inherited the
current user/process environment and completed the named-session run without
putting the key in arguments, prompts, output, or repository state.

## Live Evidence

Two coordinator snapshots three seconds apart and the later broker snapshot
reported the same steady-state disposition:

| Area | Exact steady-frame evidence | Disposition |
| --- | --- | --- |
| WS03 scene-primary reuse | `clean_reuse_count=1`, `record_count=0`, `chains_scheduled=121`, `chains_reused=121`, `chains_recorded=0`, `primary_command_buffers_reused=1`, `primary_command_buffers_recorded=0` | Pass for this static CPU-direct cohort only |
| WS03 package safety | Generation age `1`; stale reuse `0`; rejected packages `0`; Vulkan validation messages/errors `0` | Pass for this narrow smoke only |
| WS03 GPU-driven path | GPU command count `0`, GPU pipeline timing disabled, `GPURenderDispatch=false` | Unproven; not a zero-readback acceptance run |
| WS04 payload cache | Material payload hits `124`, misses `0`, payloads packed `0`, dictionary writes `0` | Cached material payload path is active |
| WS04 binding consumption | Binding snapshots `120`, snapshot entries `4412`, fast snapshots `120`, legacy snapshots `0`, fast draws `192`, legacy fallback draws `0`, frame-material snapshot hits `0`, misses `4` | Fail: “fast” still reconstructs/copies per-draw binding state |
| WS04 dynamic binding work | Dynamic bytes cleared `3256`; dynamic members patched `42` | Fail: unchanged-frame consumer work remains |
| WS04 managed allocation | Binding preparation `1664` bytes, material bindings `576` bytes, binding snapshot copy `1088` bytes | Fail: steady-state binding consumption is not zero-allocation |
| Separate preparation allocation | Frame-op preparation `28064` bytes | Fail, but sequence after the binding ownership fix |
| WS05 stable behavior | All `121` chains reused; current queued/started/completed workers, concurrency, and overlap were `0`; eligibility was `NotEvaluated` | Correctly inactive on the stable cohort; dirty-worker overlap remains unproven |
| WS05 historical activity | Process worker-secondary resets `258`; allocations `103`; current failures/timeouts `0` | Workers existed, but history does not prove concurrent dirty-frame benefit |

The Vulkan screenshot was inspected through a diagnostic thumbnail after the
desktop image viewer hit a OneDrive ACL-helper failure. It contained live,
non-black scene geometry and editor wireframe overlays. No second existing
scene camera was discoverable in the read-only session, so the required
camera-separated visual smoke remains unproven.

## Implementation Continuation Evidence

The bounded producer-artifact implementation progressed through successive
live reads as follows:

| State | Artifact reuse | Explicit fallback snapshots | Snapshot entries | Binding/material/copy allocation |
| --- | ---: | ---: | ---: | --- |
| Original baseline | `0` | `120` | `4412` | `1664 / 576 / 1088` bytes |
| First conservative artifact | `3` | `119` | `4391` | `1664 / 576 / 1088` bytes |
| Pipeline/post-process publication | `106` | `18` | `854` | material allocation subsequently reached zero |
| Final warmed artifact session | `119` | `5` | `267` | `1920 / 0 / 768` bytes |

The implementation added a generation-keyed persistent program-binding
artifact; immutable lighting/AO capture ownership; versioned pipeline-variable
and post-process settings; typed publishers for GTAO/blur, bloom
downsample/upsample, FXAA, and final post-processing; and bounded fallback
reason/sample telemetry. It also removed two universal no-op callbacks that
made otherwise immutable work look mutable.

The new detail field identified `ShadowPackedI0` as the last apparently
unowned post-process value. This was an engine catalog omission: the immutable
forward-lighting snapshot already owned the packed value, but neither retained
it nor included it in the lighting-content hash. The complete packed/array
shadow family is now classified as lighting-owned. A fresh read reported zero
unowned uniforms and zero incomplete runtime publications.

The five remaining fallbacks are explicit rather than silent:

1. renderer callback: `DeferredLightingDir.fs`;
2. material callback: `DeferredLightCombine.fs`;
3. material callback: `Skybox.DynamicProcedural`;
4. renderer callback: `BloomCopy.fs`; and
5. renderer callback: the inline final-present fragment.

The light passes and copy/present passes publish descriptor resources or
deliberately alias a source texture to a shader-specific sampler. They remain
conservative until the producer owns a typed, generation-keyed descriptor
resource publication contract. Caching their numeric output while ignoring
that descriptor identity would be incorrect. The dynamic skybox still owns
camera/time-dependent callback state and needs a typed publisher before it can
leave the fallback path.

`validation-03-05-final` preserved package generation age `1`, zero stale
reuse/rejection, clean primary reuse (`1` reuse, `0` records), and all `121`
command chains reused. Vulkan runtime counters reported zero validation
messages/errors. Stable material-binding allocation reached zero, but binding
preparation and snapshot copy remained nonzero, and `FrameOpPreparation`
remained `28064` bytes before camera movement. These failures keep Phase 0
open.

## Root Cause At The First Implementation Slice

The original dominant cause was per-draw `CaptureProgramBindingSnapshot`
reconstruction inside `VkMeshRenderer.OnRenderRequested`. The persistent
artifact removes that work for `119` representative draws. The remaining five
known callbacks still enter the conservative capture path, so the current
package is not yet the sole producer-complete binding source and the zero-copy
acceptance counters cannot pass.

The separate frame-op allocation is rooted in
`VulkanRenderer.PrepareCommandBufferFrameOperations`, which drains, sorts,
filters, and splits frame-op arrays every frame. Combining both ownership
boundaries in one patch would make read-back ambiguous, so they remain two
ordered slices.

## Suggested Solution And Execution Order

1. Publish an immutable, generation-keyed program-binding artifact at the
   binding producer. Rebuild it only when schema, material, resource, runtime,
   or relevant scoped-binding generations change.
2. Carry/reference that artifact through `OnRenderRequested`; remove
   steady-frame `CaptureProgramBindingSnapshot` reconstruction and snapshot
   copying for qualifying draws. Unsupported callbacks remain an explicit,
   counted conservative path.
3. Add or expose direct counters for producer artifact builds/publications,
   producer artifact reuse, and consumer reconstruction/copies.
4. Validate the binding slice live before changing frame-op preparation.
5. In the immediately following slice, make frame-op drain/sort/split storage
   bounded and stable so `FrameOpPreparation` reports zero allocation after
   warmup.
6. Only after both slices pass, run a dirty-chain cohort for WS05 and a
   GPU-driven cohort for WS03. Do not begin the canonical matrix or workstream
   06 first.

## Post-Change Live Acceptance For The First Slice

On a warmed, unchanged frame:

- Preserve `clean_reuse_count=1`, `record_count=0`,
  `chains_reused=chains_scheduled`, `chains_recorded=0`, and
  `primary_command_buffers_recorded=0`.
- Require `binding_snapshots_captured=0`, `binding_snapshot_entries=0`, and
  zero allocation in `MeshDrawBindingPreparation`,
  `MeshDrawMaterialBindings`, and `MeshDrawBindingSnapshotCopy`.
- Require legacy binding snapshots and legacy auto-uniform fallback draws to
  remain zero.
- Require producer artifact builds/publications to be zero after warmup,
  producer artifact reuse to be nonzero, and consumer reconstruction/copies to
  be zero.
- Preserve bounded package age, zero stale reuse/rejection, zero Vulkan
  validation errors, and zero worker failure/timeout counters.

`FrameOpPreparation=28064` bytes may remain only until the immediately
following bounded slice; it is not waived.

## Attempted Solutions And User Feedback

- A conservative generation-keyed program-binding artifact cache was
  implemented and built successfully in the isolated editor output. The live
  read-back remained visually valid and reported zero Vulkan validation
  errors, but only `3` artifact reuses and `121` conservative fallbacks. Binding
  snapshots (`119`), snapshot entries (`4391`), and the measured allocations
  (`1664` binding preparation, `576` material bindings, `1088` snapshot copy,
  and `28064` frame-op preparation bytes per frame) were materially unchanged.
  This experiment is negative evidence: the representative path includes
  broader per-frame engine bindings, so the narrow cache does not close WS04.
- Broker run `04927470b4ec407ab2bcc5e4f166c67f` requested
  `gpt-5.6-sol` but failed after 175,024 ms before reporting an actual model,
  token usage, turns, or tool calls. Its retryable transport error was
  `The Responses API stream ended before a completed response event.` The
  result is not accepted as analysis evidence.
- The broker analysis remained read-only and performed no editor or repository
  mutation.
- The broader artifact implementation reduced stable fallback snapshots from
  `120` to `5`, snapshot entries from `4412` to `267`, and material-binding
  allocation from `576` to `0` bytes. It is retained as positive evidence, but
  not promoted because binding preparation, snapshot copy, and frame-op
  preparation still allocate.
- The final session log exposed a startup-only dynamic-UI ownership failure:
  a pending async pipeline caused a normal early return after command-buffer
  tracking had already begun. Moving the tracking boundary below all deferral
  checks and abandoning it on exceptions removed the subsequent “still
  recording” reset failure and swapchain recovery. Fresh session
  `validation-03-05-lifecycle` reached frame `1275`; post-stop logs contained
  zero matching recording-state errors, render exceptions, forced recoveries,
  or VUIDs.
- `rdc doctor` passed the Windows, RenderDoc, Vulkan-layer, and capture-tool
  checks. A GPU capture was deliberately deferred: current failures are
  already localized CPU ownership/allocation gates, and diagnostic captures
  cannot substitute for the required later workstream-03 synchronization and
  output inspection.
- Two additional bounded Sol architecture requests
  (`c2592aa1ec0e404fa764f71c14a1e472` and
  `5470bc8f101b42cf86b5795fa5aa735d`) exceeded their useful elapsed-time
  windows and were cancelled. They produced no accepted renderer conclusion;
  this is retained as broker timeout/cancellation evidence only.
- User validation feedback: not yet reported.

## Local Agent Broker Reliability Findings

The broker core path is functional: run
`aac2a7d9d921463e8e63445d5cc2d2a6` completed with an exact requested/actual
model match and a valid read-only editor tool result. Two reliability gaps were
observed around it:

1. `BrokerConfiguration.ReadApiKey()` and the PowerShell launcher read only
   the broker process environment. On Windows, a key newly configured in the
   user environment is therefore invisible to an already-running Codex/MCP
   host. The installed MCP tool failed before creating a run, while a child
   launcher that copied the same user-scoped variable into only the broker
   child succeeded. The launcher should securely hydrate the selected variable
   from the Windows user environment only when the inherited process variable
   is absent, without printing, passing as an argument, or persisting it.
2. A stream that ends before a completion event is retryable, but terminal run
   state omits attempt count, retry history, response/event diagnostics, and
   the actual model observed before completion. The stream parser also lacks
   explicit `response.incomplete` handling. This makes a real retry look like a
   zero-turn, zero-usage preflight failure. Add per-attempt telemetry, retain
   safe response/event metadata, and classify incomplete terminal events.

For long-running high-reasoning slices, an optional Responses API background
mode with polling would reduce dependence on one uninterrupted stream. It must
be an explicit broker policy because background responses are temporarily
stored and have different Zero Data Retention implications. Exact
requested/actual model validation and cancellation must remain mandatory.

## Broker Hardening Implementation And Validation

The broker reliability findings above were implemented before resuming the
renderer gate:

- `BrokerConfiguration` and `Invoke-LocalAgentBroker.ps1` now read the configured
  process variable first and, on Windows when absent, fall back to the same
  user-scoped variable without logging, persisting, or passing the value in
  arguments. The live harness explicitly removed process-scoped
  `OPENAI_API_KEY`; paid runs still started successfully from user scope.
- Publishing now uses immutable
  `Build/AgentTools/LocalAgentBroker-<timestamp>` deployments plus
  `LocalAgentBroker.current`. This allowed version `0.2.0` to publish and pass
  its initialize/list-tools smoke check while the older MCP process still held
  its DLL open.
- Provider attempts now retain turn/attempt, safe response/event metadata,
  exact actual model, terminal status/reason, elapsed time, and retry state in
  both the broker snapshot and nested terminal result. `response.incomplete`
  is explicit, `"error": null` is accepted, and the broker validates the live
  minimum `max_output_tokens=16` before API execution.
- `use_background_mode` is an explicit, default-off run option. It creates and
  polls background Responses, resumes transient polling against the same
  response ID, and calls the provider cancellation endpoint on abandonment.
  The docs disclose temporary provider storage and non-ZDR behavior.
- Route advice no longer escalates a deterministic read-only profiler snapshot
  merely because its subject contains `Vulkan` or `renderer`; hard ambiguity,
  architecture, concurrency, security, and combined GPU-debug signals still
  route to Sol.

Live broker evidence against named session `broker-hardening-validation`:

| Run | Automatic route | Terminal evidence |
| --- | --- | --- |
| `c29da96c6fb8415c90ccfc8647181f7c` | Luna | Completed two background turns with requested/actual `gpt-5.6-luna`, one read-only profiler call, two completed provider-attempt records, 8,432 total tokens, and the expected WS04 nonzero-allocation classification. |
| `6d05ed98ad9d487fbfb2744bc44f603e` | Luna | A 16-token output boundary produced `response.incomplete`, exact actual model, reason `max_output_tokens`, and terminal `BudgetExceeded` classification in both result layers. |
| `1b8c692e7b5847429eec9eafc5ccdaec` | Sol | Cancelled after provider acceptance; requested/actual model matched, response ID was retained, outcome was `cancelled`, and `provider_cancellation_accepted=true` appeared in both result layers. |

The first background probe also exposed and then closed the pre-existing
`"error": null` parsing defect. No broker run silently substituted a model.

## Gate Decision

The later implementation continuation removed the five callback snapshots,
legacy auto-uniform fallback, reflected-schema mismatch, snapshot-copy
allocation, frame-op-preparation allocation, and frame-data-refresh allocation
from the representative warmed path. The final live read reported `129`
persistent artifact reuses, zero artifact builds/fallbacks, zero binding
snapshots/entries, `204` typed auto-uniform fast-path draws, zero legacy draws,
and zero schema-mismatch sites. Primary and secondary recording allocations
were also zero.

The gate is not yet promotable. The same frame still attributed `3360` managed
bytes to `MeshDrawPreparation`: `1624` bytes in resource preparation, `1520`
bytes in binding preparation (`128` publisher state, `1264` artifact
eligibility, and `128` artifact lookup), plus `216` bytes outside those nested
scopes. The broker worker classified these bytes as a validation-only
optimization residual for lifetime/reuse correctness. The coordinator did not
accept that as a gate waiver: repository policy defines warmed per-frame
hot-path allocation as a bug, and Phase 0 explicitly requires zero allocation.
Workstream-03 GPU-driven acceptance, workstream-04
dirty-owner/lifetime/parity stress, and workstream-05 dirty-worker
overlap/benefit remain validation work. Keep the consolidated gate open and
workstream 06 blocked pending allocation closure and the canonical matrix.

## Directional Shadow Regression Found During Closeout

The final ImGui/Vulkan smoke exposed a separate correctness regression: the
directional shadow map appeared to flicker and move to the wrong location after
camera motion. A fixed-camera sequence was stable, but moving to `x=+1`, then
`x=-1`, and returning to the exact original camera produced a pre-fix grouped
result with about `94.6%` changed pixels. Switching only
`CascadeShadowRenderMode` to sequential made the same round trip exactly
pixel-identical, isolating the defect to grouped `InstancedLayered` recording
rather than cascade fitting, light state, or the deferred receiver.

The root cause was in
`VkMeshRenderer.ComputeAutoUniformOwnerIdentity`. Pass-frequency owner identity
included the full `LayeredShadowUniformState` hash, including mutable cascade
and cubemap matrices. Camera motion therefore moved the pass UBO reservation to
a new dynamic offset while reusable secondary command buffers retained the old
baked offset. `VulkanAutoUniformPublicationSnapshot.PassGeneration` already
owns that mutable content generation, so the owner key must remain stable.

`AddShadowPassOwnerIdentity` now hashes only stable pass identity: the shadow
camera reference, directional layered flag/count, and point-light layered
flag/count/face indices. Mutable matrices remain in pass content generation.
Temporary receiver invalidation used during isolation was removed, and the
directional deferred shader's per-light and shadow values retain explicit
object-frequency metadata.

Final optimized grouped evidence is under
`Build/_AgentValidation/20260801-vulkan-command-recording-finish/`:

- The initial-to-`x=+1` comparison changed `1,923,873 / 2,073,600` pixels
  (`92.7794%`), proving that the camera/content actually changed.
- Returning to the exact initial pose changed only `190 / 2,073,600` pixels
  (`0.0092%`), confined to transient editor overlay pixels; the settled image
  had the same result.
- `DirectionalShadowAudit` recorded grouped `InstancedLayered` renders for all
  four cascades with no sequential fallback. Reused cascade provenance matched
  current and rendered matrix/content hashes.
- The warmed runtime reported zero Vulkan validation messages/errors. A Vulkan
  RenderDoc capture at
  `renderdoc/directional-shadow-vulkan-fixed_frame240.rdc` contains `512`
  events, `154` draws, one dispatch, six clears, and no capture log messages.
  It is steady-state pipeline evidence; because that frame reused a clean
  atlas, it does not replace the later workstream-03 synchronization capture.

## Broker Final Review After Codex Restart

After the requested Codex restart, the repaired local broker ran one bounded,
read-only final review against the named isolated session
`validation-03-05-broker-closeout`:

- Run ID: `c21386c827144a9cbed4146ab178e8d2`.
- Requested model: `gpt-5.6-luna`.
- Actual model: `gpt-5.6-luna` (exact match; no silent substitution).
- Terminal result: `completed` in `11,797 ms`, two turns, one
  `get_render_profiler_stats` call, `12,710` total tokens, no retry.
- Local read-back: zero Vulkan validation messages/errors, zero frame-op,
  frame-data, primary/secondary-recording, and command-cache allocations;
  `120` reused chains, zero recordings, zero conflicts/failures, and zero
  retired resources in the worker snapshot.

The worker classified the warmed `MeshDrawPreparation` `3360` bytes as a
validation-only optimization residual for lifetime/reuse correctness. That
classification is retained as worker evidence but does not override the
repository's zero-allocation hot-path rule or Phase 0 exit criterion. It also
assessed
`ComputeAutoUniformOwnerIdentity` / `AddShadowPassOwnerIdentity` as correctly
separating stable shadow-pass identity from mutable cascade/cubemap content,
while noting that a broader owner-hash audit was still useful.

A bounded source audit completed immediately after the worker run searched the
Vulkan tree for every `ShadowUniformState` hash/use. The only full shadow-state
hash outside the value type itself is `VulkanAutoUniformPublicationSnapshot`'s
`ComputePassGeneration`, where mutable matrices intentionally drive content
publication. `ComputeShadowCommandChainStructuralSignature` and the repaired
`AddShadowPassOwnerIdentity` hash only stable pass/layer/face identity. No other
Vulkan owner-identity path was found to include mutable cascade/cubemap data.

The gate remains blocked. The next bounded slice is to remove the proven
publisher/eligibility/resource-preparation allocations and rerun the existing
03-05 validation matrix before deciding whether 06 can start.

## Checklist Reconciliation

An exact-clause audit of the consolidated checklist found `79` total boxes.
Only the Phase 1 narrow Vulkan smoke is fully supported by current retained
evidence, so it is now checked. The remaining `78` boxes stay open because at
least one clause in each still requires Phase 0 allocation closure or the
Release/RVC/GPU, parity, stress, focused-test, or required RenderDoc matrix.
Narrative progress and partial evidence are not treated as completion of a
composite checkbox.

## Broker Restart State After Final Publish

The key-inheritance implementation was correct, but this Codex host was still
connected to a broker process launched from the legacy mutable
`Build/AgentTools/LocalAgentBroker` deployment before those fixes. Key presence
was confirmed in both the current process and Windows user scopes without
reading or printing the value. A fresh immutable deployment,
`LocalAgentBroker-20260803010015466`, published and passed the broker
initialize/list-tools smoke test.

Stopping only this Codex host's exact stale broker child correctly closed its
old stdio transport. Codex does not hot-rebind an MCP stdio server inside an
already-running task, so the requested restart was required and completed
before the review above. No alternate tier or direct-stdio simulation was used
to bypass the transport.
