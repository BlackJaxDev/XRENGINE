# RenderBench Phase 5.3 material evidence

`XREngine.RenderBench --scenario phase53-materials` is a presentationless
Vulkan correctness scenario for immutable material-table publications, sampled
texture/sampler mutation, and asynchronous texture publication. It creates
no window, desktop swapchain, editor session, or XR session.

## Run

Run from the repository root. Use an ignored output directory under
`Build/_AgentValidation/<task-run>/`.

```powershell
$env:XRE_VULKAN_VALIDATION = '1'
$env:XRE_VULKAN_SYNC_VALIDATION = '1'

dotnet .\Build\RenderBench\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.RenderBench.dll `
  --scenario phase53-materials `
  --scenario-depth both `
  --scenario-repeats 2 `
  --scenario-frames 240 `
  --width 640 --height 360 `
  --output-dir Build\_AgentValidation\<run>\reports\phase53-materials
```

The parent starts one fresh, windowless child for each normal/reversed-depth and
repeat combination. A single child uses `--scenario-lane production` with one
explicit depth convention. The scenario requires both standard and
synchronization validation to be enabled and rejects any native validation
error. The CLI defaults this material scenario to 240 streaming boundaries,
matching `phase53-streaming`; an explicit `--scenario-frames` can still select
a smaller diagnostic budget.

## What it proves

The fixture draws a real sampled deferred material. It first captures an
immutable material-table publication, then mutates a scalar material property.
The scalar mutation must retain the descriptor-closure generation. It then
queues one 4096² RGBA8 mip chain as `VisibleNow`, binds that same pending
`XRTexture2D` to the shaded material, and changes its minification filter and
wrap mode. The current backend uses `TexturePublicationPolicy=PublishedGenerationsOnly`:
the pending texture generation is absent from the required manifest, affected
indirect work is skipped until atomic descriptor publication, and no strict
required-generation admission proof is claimed.

The 4096² chain exceeds the foreground staging ring. The documented 240-boundary
budget allows publication to complete while production frames continue. Admission
retry counters cover all frame admission (including cold banks and other
resources); they are not texture-wait proof. The September 14 follow-up first
used a 48-boundary limit. All four children exhausted it with ten chunks
submitted, nine completed and one in flight. Those failed attempts remain
recorded; their diagnostic counters showed continuing progress, not a stalled
upload worker.

After the initial, scalar-mutation and texture/sampler-mutation receipts, the
scenario retains the opaque pass's immutable CPU publication and performs
receipt-gated native material-table readback. It
requires the native bytes and owner, row generation, row stride, and descriptor
closure generation to match the retained publication. It also requires each
scalar and texture/sampler change to report exactly one sparse material-row
range. The initial retained token is copied and checked again after later
mutations, so it cannot silently alias mutable row storage.

The texture/sampler mutation is replayed through every allocated frame-slot
bank. Each already-warm bank may write exactly one row during that replay. A
following idle window covers another frame-slot cycle and must add no material
page writes, material bytes, descriptor writes, or closure-lease acquires.

## Final matrix evidence

The September 14 report at
`Build/_AgentValidation/20260910-060112-vulkan14-h/reports/followup-e-material-closeout/`
passed normal and reversed depth twice (four children total). Every child
reported:

- 164 accepted production frames and 3 all-frame admission retries;
- 153 accepted receipts whose following query still found an unpublished texture;
- one bound texture with 31 submitted and 31 completed visible-priority upload chunks;
- three receipt-gated native row snapshots matching their immutable CPU tokens;
- one changed 64-byte row for each scalar or texture/sampler mutation;
- descriptor-closure generations `1, 1, 4`: unchanged for the scalar mutation
  and changed for the texture/sampler mutation;
- all-slot warming followed by idle counters of page writes `9 -> 9`, descriptor
  writes `5 -> 5`, and closure-lease acquires `4 -> 4`;
- three frame slots, 1280×720, three owned material banks, and zero pending bank
  allocations; and
- standard and synchronization validation enabled with zero errors and four
  loader warnings.

The final run omitted `--scenario-frames` and validated the new 240-boundary CLI
default. Its reports explicitly identify `TexturePublicationPolicy=PublishedGenerationsOnly`
and `StrictRequiredTextureAdmissionProven=false`. They also retain
`AcceptedFramesBeforeTexturePublication`: accepted receipts whose following
ticket query still found the texture unpublished. A strict required-generation
contract needs a separate explicit workload and probe; this scenario does not
claim one. The affected indirect pass may be omitted while the upload is pending;
these receipts do not prove a last-good textured draw.

The earlier August control, before commit `65ad14a03` changed the backend's
generation-capture policy, recorded 11 accepted frames and 10 admission retries,
with closure generations 1, 1 and 3. Its historical report was
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/reports/phase53-materials-final/`.
It does not establish the current policy's admission behavior.

## Boundaries

This is correctness and provenance evidence, not a performance benchmark. The
native readback is a cold diagnostic operation authorized only by an authentic
completed receipt; it does not feed rendering and does not establish
zero-readback or frame-time performance. Parent and child reports explicitly
set `DiagnosticReadbacks=true`. The scenario also does not prove
in-flight reclamation of an old descriptor closure after its retained token is
released; that needs a separate lifetime/retirement experiment. It makes no
desktop, XR, OpenXR, cross-vendor, or presentation-path claim.
