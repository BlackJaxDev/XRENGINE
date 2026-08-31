# RenderBench Phase 5.3 material evidence

`XREngine.RenderBench --scenario phase53-materials` is a presentationless
Vulkan correctness scenario for immutable material-table publications, sampled
texture/sampler mutation, and required imported-texture readiness. It creates
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
error.

## What it proves

The fixture draws a real sampled deferred material. It first captures an
immutable material-table publication, then mutates a scalar material property.
The scalar mutation must retain the descriptor-closure generation. It then
queues one 4096² RGBA8 mip chain as `VisibleNow`, binds that same still-pending
`XRTexture2D` to the shaded material *before* the first submission, and changes
its minification filter and wrap mode. This is deliberately not the earlier
five 1024² priority-only queue: the bound texture generation is captured by the
ordinary material dependency path as a required frame-manifest dependency.

The 4096² chain exceeds the foreground staging ring. A required upload that
cannot finish within one production preparation is reported as typed admission
pending; the headless coordinator yields outside production admission and
rebuilds a fresh frame plan. No rejected plan becomes a frame receipt.

After each accepted receipt, the scenario retains the opaque pass's immutable
CPU publication and performs receipt-gated native material-table readback. It
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

The final report at
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/reports/phase53-materials-final/`
passed normal and reversed depth twice (four children total). Every child
reported:

- 11 accepted production frames and 10 typed admission retries;
- one bound required texture with 31 submitted and 31 completed upload chunks;
- three receipt-gated native row snapshots matching their immutable CPU tokens;
- one sparse material-row range for each captured publication;
- descriptor-closure generations `1, 1, 3`: unchanged for the scalar mutation
  and changed for the texture/sampler mutation;
- all-slot warming followed by idle counters of page writes `6 -> 6`, descriptor
  writes `5 -> 5`, and closure-lease acquires `3 -> 3`; and
- standard and synchronization validation enabled with zero errors.

## Boundaries

This is correctness and provenance evidence, not a performance benchmark. The
native readback is a cold diagnostic operation authorized only by an authentic
completed receipt; it does not feed rendering and does not establish
zero-readback or frame-time performance. The scenario also does not prove
in-flight reclamation of an old descriptor closure after its retained token is
released; that needs a separate lifetime/retirement experiment. It makes no
desktop, XR, OpenXR, cross-vendor, or presentation-path claim.
