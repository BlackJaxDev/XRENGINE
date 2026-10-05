# Disk-backed shader root reload

Status: investigation active, October 4, 2026.

The integrated reload gate in the [stall remediation checklist](../../todo/rendering/vulkan-stall-remediation-todo.md)
remains open. Restoring a temporary Vulkan native opaque shader probe left old
top-level text around refreshed includes, producing undeclared identifiers. A
fresh named editor session recovered. Earlier evidence and restored-file
confirmation are in the [OpenGL admission investigation](2026-10-03-s16a-opengl-admission.md).

## Owning path and acceptance

Owner: shared `TextFile`/`XRShader` source identity and dependency invalidation.
`ShaderSourceDependencyIndex` registers root paths, but a notification only
invalidates resolved-source caches and increments the shader revision. Vulkan's
generated preamble sources already track that revision; they copy the original
asset's `Source.Text`, which can still contain old root bytes.

Hypothesis: refreshing a clean disk-backed root on its exact file notification
will let existing generated-source revision propagation rebuild correct input.
Reject this hypothesis if the original asset already has new root bytes while
the generated/compiler input remains stale. First append a harmless root
comment in a named Vulkan session, observe original asset text and revisions,
then restore the file byte-for-byte.

Any fix must preserve unsaved in-memory edits, generated preambles and synthetic
root sources. A file path alone does not establish that disk owns a source.
Track successful load/save provenance, and ensure an edit or source replacement
during a read wins over the refresh. Include edits must continue to invalidate
the resolved cache without replacing authored root text. Avoid render-loop disk
polling and per-frame allocations.

Acceptance: zero-warning owning build; live root edit/restoration, include
edit/restoration, and an unsaved root edit surviving disk notification, with
actual source/readiness observations and viewed output. Keep file changes and
runtime mutations serialized and restore authored files exactly. Use bounded
notification waits (30 seconds per edit) and record observed readiness latency;
do not count last-good output alone as successful source adoption. No tests are
added or modified without the previously requested explicit clearance.

## Control observation

The live harmless-comment probe confirms the source-layer cause. The original
asset revision advances 4→5 after the file notification and 5→6 after manual
reload, but its `Source.Text` hash stays equal to the old disk text and never
contains the marker. Restoration advances revision to 7. Disk bytes are restored
exactly. Evidence: `reports/root-reload-control.json` under the existing
`Build/_AgentValidation/20261003-142654-vk-todo/` run. Both file notifications and
explicit manual reload need to refresh eligible roots; snippet-only invalidation
must not accidentally acquire disk authority.

## Source candidate and frozen-output control

The isolated Release build passes with zero warnings/errors. All 32 existing
shader-dependency, resolver-cache and asset-cache tests pass; none were changed.
A warm loaded
source has its disk baseline. Live root edits and include edits reach new ready
Vulkan link generations and render the red/green probes. Root and include
restoration return the expanded source hash to baseline. The root edit takes
4.4 seconds, include edit 7.6 seconds, and restorations 1.2/7.7 seconds in the
completed sequence. These are observed waits, not performance acceptance.

The unsaved blue source survives both disk notification and manual reload in
the original asset and generated resolved source, but viewed captures show
ordinary shading instead of blue. The integrated check remains open. Next
isolate an in-memory-only edit and inspect the native wrapper's actual rewritten
artifact plus stage receipts and captures before attributing a compiler-input
or command-reuse fault. Last-good images and ready metadata alone are insufficient.

Probe corrections: transient absence of the prepared native program during
reload is expected and must be polled. Vulkan artifact identity includes include
timestamps, so identical restored bytes need not restore the artifact identity;
compare expanded source hashes and fresh link generations instead. An interrupted
probe's manual cleanup also made memory differ from its disk baseline; the guard
correctly rejected subsequent refresh until a clean session was started.

Evidence: `reports/root-reload-validation.json`,
`reports/root-source-provenance.json`, and the separately preserved interrupted
probe reports under the existing run. Authored files are restored byte-for-byte.

The isolated memory-only repeat proves the native compiled artifact contains
the blue assignment (`LoadedFromDiskCache=False`). It also proves frozen output:
camera changes, the edit and restoration all retain one exact HDR hash while
enqueue frames advance 138257→147057. Source authority/cache collision is ruled
out for this case. A fresh RenderDoc-friendly session reproduces it. RenderDoc's
trigger API accepts the request, but no presented capture appears because frame
submission has already stopped; no replay session was opened.

`get_render_profiler_stats` identifies the failure at frame 1155 in that fresh
session: `PresentNowReadinessFailed`, `PipelineCompilation`,
`sealed-primary-recording`, disposition `RendererTerminal`. The complete-family
validator counts preparation=0, raster=1, lateCompute=1, lateRaster=1, ao=1,
classification=1, opaque=1, views=1 and valid state. Device loss is false. Later
frames retain that terminal failure and never reach command recording. The
window is not minimized, has a valid extent and does not suppress scene rendering.

Next hypothesis: independent per-stage readiness polls admit a partial family
when compilation changes Pending→Ready between preparation and later stages.
Separate stage capability/intent authoring from executable pipeline readiness:
queue the complete required family and use its existing sealed preparation's
typed retry for Pending/Missing pipelines. Keep the structural completeness
validation and explicit rejection semantics. A
frame-swap-only source notification change cannot prevent readiness from changing
during a later authoring frame. Acceptance requires edit/restore recovery with
advancing successful submission and differing camera captures, in addition to
source and native artifact checks. Do not accept enqueue counters alone.

Additional evidence: `reports/root-memory-only.json`,
`reports/root-memory-native-artifact.json`, `reports/root-memory-renderdoc.json`,
`reports/root-frozen-profiler.json`, and `reports/root-frozen-window-state.txt`.

## Complete-family reload recovery

The partial-family hypothesis is confirmed. Required Vulkan stages now author
intent against physical capability and an existing reservation generation;
executable readiness remains the sealed family's responsibility. Initial or
stale output binding is refreshed only by visibility preparation, preventing
downstream stages from joining an otherwise missing family. Optional shadow
readiness, source/input leases, output identity checks and structural completeness
validation remain enforced. Pending compilation uses the existing frame retry.

The isolated Release build passes with zero warnings/errors. The full live
sequence now passes: root red, include green, unsaved blue, blue preserved across
disk notification and manual reload, and restoration. All twelve two-position
capture windows have fresh completed frames (approximately 243–312 per window)
and no terminal rejection. Viewed restored images show distinct camera views;
expanded source returns to baseline and native link generation reaches 17.
The final source restoration takes 1.188 seconds. This validates recovery, not
the overall frame-rate target or display-tonemapping parity. Authored shader
bytes are restored exactly.

Evidence: `reports/root-reload-validation.json`; the earlier frozen-output
sequence is retained as `reports/root-reload-source-pass-render-freeze.json`.
Existing frame-contract, capability, Vulkan readiness and desktop-loop tests
report 96/101 passing. All five failures also appear with the same causes in the
earlier independent HEAD-worktree run
`Build/_AgentValidation/20261003-015628-vk-100hz/logs/vkwide-head.log`: the pipeline
factory expectation, legacy mesh-pass assertion, missing old pipeline-cache
path, stale manifest literal and stale recording-code lookup. They predate this
fix. No tests were changed; this classification does not replace live validation.

## OpenGL shared-source regression

Next check the same root/include/in-memory sequence on OpenGL with the current
binary before changing its source factory. Hypothesis: specialized shaders that
resolve a synthetic disk include may still ignore unsaved edits in the original
template. Reject that hypothesis if a changed original root reaches a new ready
program fingerprint and renders the blue probe. Acceptance remains two viewed
camera positions, bounded source adoption and exact restoration; source metadata
alone cannot certify output. Preserve specialization transforms and avoid adding
render-loop disk reads or allocations.

The OpenGL control confirms that mismatch. Root red and include green render;
restoration returns the expanded hash and two distinct views. The original
in-memory root then advances to the blue source/revision, but the specialized
program keeps its baseline expanded hash and ready fingerprint throughout the
30-second observation. Evidence: `reports/root-reload-gl-unsaved-control.json`.
The synthetic include bypasses the template's current text. Use the template's
existing `TextFile` for specialization so its `TextChanged` event invalidates all
variants, retaining the immutable transform and fail-fast include resolution.
Object replacement of `template.Source` is separate coverage and remains open.

One initial capture hit the known OpenGL texture-readback `InvalidOperation`;
the repeated sequence records at most three attempts and retries only that
specific capture error. This does not relax source-adoption acceptance. The
interrupted report is `reports/root-reload-gl-readback-interrupted.json`.

The shared authored-source correction passes the full OpenGL sequence. Root red,
include green, unsaved blue and blue after disk notification/manual reload render
in both camera positions. Final restoration returns the expanded source hash and
ready fingerprint to baseline, with two distinct restored views. All twelve HDR
PNGs were viewed. Observed adoption waits are 12.844 seconds for root red, 11.860
for include green, 18.250 for unsaved blue, and 1.157/1.219/1.250 for restorations.
These driver compile/link waits are recorded, not claimed as a latency improvement.
The isolated Release build passes with zero warnings/errors in 13.53 seconds.
Shader disk bytes are restored exactly. Evidence:
`reports/root-reload-validation-gl.json` and `logs/root-reload-gl-candidate.log`.

Read-only review confirms the shader and program subscriptions detach on destroy
and all four factory callers retain their transforms. A suspected allocation in
the readiness method was rejected by inspecting its compiled IL: Roslyn emits an
inline-array local, with no `newarr` or `newobj` in that method. No speculative
allocation fix was made. MSAA/stereo, replacement of the source object itself,
driver-parallel overlap, and the broader integrated reload matrix remain open.

Existing OpenGL linking-policy, mesh-lifecycle, shader-dependency and resolver
tests finish 65/66 passing (`reports/tests/gl-authored-reload.trx`). The sole
failure remains `GLMeshRenderer_UsesCombinedProgramsWithoutDuplicatingPendingUberCompiles`:
its old source-string assertion also failed in the previous run and mismatches
HEAD. No test files were changed. The named editor is stopped after validation.
