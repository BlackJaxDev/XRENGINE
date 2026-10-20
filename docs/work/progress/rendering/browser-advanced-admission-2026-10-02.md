# Browser Advanced native family admission

The browser host installs the package's exact scoped Advanced catalog on initial
creation and device recovery. Admission checks the canonical compact, finalize,
vertex-pull, depth, GTAO, classification, native shading, and background program
ABIs and their declared limits against the negotiated device. Missing programs
or insufficient limits reject the native family. Selected post effects and
optional surface exports retain their separate exact program requirements.

The native physical contract requires three integer color attachments (16 bytes
per sample), seven storage buffers, four storage texture outputs, sixteen sampled
textures, twelve samplers, and two dynamic uniforms across three bind groups.
The largest native group contains 28 bindings. Resource extents, buffer capacity,
cohort closure, sample count, and geometry/material eligibility remain checked
when the retained output is prepared.

## Ownership and submission

Three bounded output banks carry backend generation, output identity, and a
monotonically increasing incarnation. Repeated reservation of an active output
is idempotent. Releasing its owner invalidates admission immediately, while
recorded and submitted work keep the bank until the real queue-completion
watermark permits retirement. Frame storage remains tied to the canonical
publication's three completion-retained slots. The output ceiling matches the
maximum distinct canonical publications that one atomic frame can record.
Exact-publication reuse shares a scene slot. An impossible additional distinct
publication fails explicitly; older GPU-owned slots remain retryable pending
queue completion.

Native stage authoring validates the exact owner, publication, backend package,
view family, framebuffer, resource generation, and stage order. All required
native operations must finish before the atomic frame can submit. A primary
Advanced output that never reaches native enqueue cannot present a post-only
frame. Diagnostics expose active, retiring, and free output banks separately.

Explicit Required factory selection preserves a new Advanced source through
asynchronous device startup; output binding supplies the physical reservation.
An existing authored pipeline retains its identity. The ordinary unconfigured
factory policy remains unchanged. Recovery revalidates device-dependent
admission and reinstalls the same immutable program catalog.

## Submission capability scope

Advanced GPU visibility uses compute compaction followed by vertex-pulled
indirect draws whose first instance is always zero. Its capability does not
require the optional `indirect-first-instance` feature needed by the generic
indexed material renderer. CPU-direct Advanced draws remain direct draws with
CPU-authored vertex counts.

The native Advanced meshlet probe is scoped to an active Advanced stage-family
output. Hardware task/mesh shader probes and the generic Default meshlet route
remain unavailable. A meshlet-selected native draw without its resident static
meshlets is rejected rather than converted to indexed geometry. Native texture
indirection is described explicitly as cohort bindings rather than mislabeled
as a desktop texture-array or descriptor-indexing implementation.

Single-view, single-sample static triangle cohorts are currently executable.
Integer per-sample MSAA, stereo, deformation, vertex-displacement companions,
and other unimplemented selected profiles retain precise rejection. Admission
does not certify browser image parity or production acceptance; live GPU
compilation, execution, resize, and device-recovery evidence remain required.

## Historical narrow validation

Before the workspace loss, an ignored managed probe loaded the actual cooked
native descriptors and passed 25 checks: missing-family rejection,
complete-family admission without optional indirect first instance, independent
generic/hardware probes, deficient-device rejection, idempotent reservations,
bounded exhaustion, stale-incarnation rejection, prevention of a parallel
reservation for a retiring output despite a free bank, complete-stage
submission, and retention until the supplied completion watermark. The probe
made no GPU calls and provided no browser execution evidence.

The combined browser build, including its dependency-enabled WebGPU renderer
and native Jolt link, passed with zero warnings and errors before the workspace
loss. The final narrow WebGPU build also passed with zero warnings and errors.
Those historical build logs are no longer present. Source reconstruction after
the October 3 workspace loss requires a fresh combined validation gate before
publication. No staging, commit, or push is part of this integration.

After reconstruction, the dependency-enabled Rendering and WebGPU build passed
with zero warnings and errors. The restored admission probe passed all 25 checks
against freshly cooked Advanced descriptors on October 3. This refresh validates
the native admission/reservation slice; the combined final browser/publication
gate and live GPU evidence remain outstanding.
