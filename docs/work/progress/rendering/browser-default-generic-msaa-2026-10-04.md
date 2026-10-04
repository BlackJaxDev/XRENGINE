# Default and generic WebGPU multisampling

## Source contract

Default's browser command graph now preserves the requested CPU-direct,
GPU-indexed-indirect or compute-meshlet submission strategy. The depth/normal
prepass retains that exact strategy and the same viewport override as forward
raster. Default's cold requirements use the shared submission declaration to
require the matching cull, LOD, meshlet expansion/refit and ordering programs.
The existing shared collection still owns mixed-material ordering and explicit
CPU-only submissions; this change introduces no substitute submission algorithm.

The mono SDR Default resource profile admits AA None or exactly four MSAA
samples. The immutable resource profile already carries AA and sample count,
so changing either, resizing, or changing AO replaces the declared resource
generation. Framebuffer factories return their logical owners before physical
WebGPU preparation, allowing asynchronous preparation to retain and retry the
same pending-generation object.

For x4, forward opaque, masked, background and transparent draws use matching
RGBA16F color and Depth32Float attachments. With GTAO enabled, the prepass writes
x4 oct-encoded normals and the same raw x4 depth that forward raster later uses.
The cooked `depth-normal-msaa-resolve` fullscreen pass chooses the nearest
covered depth sample and copies that sample's encoded normal and depth into
single-sample GTAO sidecars. Companion normal alpha distinguishes covered samples
from the transparent clear; uncovered pixels retain far depth and zero normal.
The shader handles both depth directions, rejects nonfinite/out-of-range depth,
and never averages surface normals. The graph declares its depth write separately
from stencil and preserves dependencies on both multisample inputs.

After background and sorted transparency, the existing retained GPU color-only
resolve produces the single-sample HDR texture consumed by bloom, exposure and
tonemapping. Raw depth is not blitted or replaced by the AO sidecar.

The common material adapters admit matching one- or four-sample color/depth
targets for standard/color-coverage lit, lit textures, authored texture/alpha,
Unlit, Uber, sky and impostor raster, including exact depth/normal companions.
The generic draw descriptor continues to use the actual framebuffer sample
count, so authored modular pipelines can use the same adapters with their own
declared attachments and resolve commands. Existing directional and local shadow
outputs remain single-sample. The Advanced native per-sample shading path remains
independently implemented and qualified.

## Boundaries

- x2/x8 attachments, stereo/XR output, other Default AA modes and HDR presentation
  remain explicitly unsupported by this resource profile
- The initial x4 source milestone retained the generic reversed-depth rejection.
  The subsequent [reversed-depth integration](browser-generic-reversed-depth-2026-10-04.md)
  adds coordinated frozen-camera comparison/clear lowering, sky reconstruction
  and post-depth consumers. Its builds, cooks and managed checks pass; physical
  known-value output remains open. The resolve shader alone was not that support
- Arbitrary authored pipeline assets remain responsible for declaring compatible
  targets, exact programs and ordered resolve/post-process consumers
- No desktop GLSL, CPU readback, hardware task/mesh shader dispatch or alternative
  AA algorithm was introduced

## Validation

The initial source pass and independent review corrected multisample normal
binding shape, depth-output graph access, asynchronous framebuffer ownership,
prepass viewport-strategy parity and Default's submission-program requirements.
The combined Rendering and WebGPU build passed with zero warnings and errors
against source tree `75e3465d8e165aa447d896b45683eae007d60034`.
The native Browser build also passed with zero warnings and errors against that
same tree. Its successful rerun corrected a validation-command copy-local
override; the initial SDK core-library omission required no source change.
The focused production cook packaged the new resolve recipe successfully:
descriptor `083df46c9a6694cd2a3b8efbf38404810a6f02407fbe247d51b930be23a35958`,
WGSL `9bd28fa5ba905d7ebee97945f125d5b8363b46bb0ba2651b9282b4a0cae9c418`.

A narrow witness linked the frozen production assemblies and invoked Default's
real resource declarations, command graph metadata and cold requirements. It
passed 214 checks across twelve combinations of x1/x4, GTAO on/off, and CPU-direct,
indexed-indirect or compute-meshlet strategy. Checks covered matching forward
attachments, single-sample post-process/AO sidecars, optional resolve presence,
explicit depth writes and raw sampled inputs, resolve ordering after transparency,
preserved prepass strategy, selected compute/ordering program closures, resize
profile replacement identity, and rejection of unsupported x1/x2/x8 MSAA
selections. The witness compiled with zero warnings and errors. Viewport override
precedence and asynchronous generation ownership were independently source-reviewed;
the witness did not execute GPU commands or certify physical generation retirement.

Disposable evidence is under the active validation run's
`scratch/default-msaa-validation/` and `logs/default-msaa-{cook-0658,probe-0705}.log`.
Browser acceptance remains pending: edge coverage and sample preservation, masked
materials, normal and depth resolve, transparent ordering, GTAO on/off,
CPU-direct/indexed-indirect/compute-meshlet variants, resize/profile replacement,
and repeated generation retirement still need rendered evidence.

## Default factory browser cohort

The existing ordinary-Unlit diagnostic now exposes explicit `cpu-x1`, `cpu-x4`
and `cpu-x4-ao` startup profiles. These select the real Default camera and
resource profile before initialization. The original nine material centers are
unchanged; x4 adds a gutter overlap with known front/rear colors, depths and
oct-encoded normals. Quarter-step resolved coverage identifies partially
covered pixels without assuming hardware sample positions. The AO profile
requires nearest-covered depth and the normal from that same sample, plus the
actual resolve/GTAO operation order and native attachment sample counts.
The disabled-AO profile requires those unused sidecars to be absent.

The harness retains source/camera/committed-generation identity, successful
submitted packets, bounded canonical readbacks and failure evidence. It pauses
only its own diagnostic frame pump during grouped samples and restores it with
session/renderer ownership checks. Both x4 profiles repeat non-square resize,
restoration and startup. The Linux cook adds the existing canonical resolve
recipe and verifies the exact ten selected pipeline keys instead of only a
catalog count. Source and JavaScript checks pass; C# compilation and the first
rendered x4 result remain pending. This fixture does not qualify custom graphs,
GPU-indirect/meshlet modes, reversed depth or transparent coverage.
