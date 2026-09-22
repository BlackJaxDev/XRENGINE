# Advanced Pipeline Antialiasing TODO

Last updated: 2026-09-22  
Owner: Rendering  
Status: Mono TAA/TSR/MSAA validated on OpenGL/Vulkan; OpenGL DLAA validated; follow-up remains

## Goal

Make TAA, TSR, MSAA, and DLAA visibly and correctly affect meshes rendered by
the Advanced pipeline on the supported OpenGL and Vulkan paths. Finish the
in-progress work in the current worktree; do not treat a selected AA profile or
a successful build as proof of a correct frame.

The [investigation](../../investigations/rendering/2026-09-22-advanced-pipeline-antialiasing.md)
contains the original diagnosis, implementation notes, runtime observations,
and evidence paths. This document tracks only the remaining work and its exit
criteria.

## Implemented baseline

- [x] TAA/TSR: publish temporally jittered native authoring views after temporal
  Begin, correct perspective jitter direction, and account for current/previous
  jitter in history lookup.
- [x] TAA: declare the accumulation framebuffer with its actual HDR and
  exposure-variance outputs.
- [x] DLAA: connect the Advanced output chain to the existing vendor command
  with final color, depth, motion, and exposure inputs.
- [x] MSAA: add raw multisample visibility, metadata, selection, depth, and
  sample-position resources; per-sample native shading; a coherent visibility
  and depth resolve; coverage-aware HDR composition; an initial background
  blend; and OpenGL and Vulkan execution plumbing.
- [x] Targeted OpenGL and Vulkan project builds, native shader compilation,
  and the final isolated editor builds passed with zero warnings and errors.

## Remaining work

### 1. Correct the render-graph contract

- [x] In `VPRC_AdvancedRenderStage.DescribeRenderPass`, select pass declarations
  and resource uses from the active resource layout/generation. The prior graph
  declared raw accesses and a synthetic resolve even for non-MSAA profiles;
  raw multisample resources are now described only for MSAA.
- [x] For non-MSAA, declare canonical early/late visibility writes, omit the
  raw resolve and raw native-shading reads, and depend AO/classification on
  late raster directly. For MSAA, declare raw early/late writes, resolve into
  canonical targets, then depend downstream work on the resolve. Do not claim
  canonical writes in an MSAA raster pass that only writes raw attachments.
- [x] Check graph descriptions before and after an AA mode change without
  assuming the command chain is rebuilt. Resource invalidation currently does
  not rebuild the command chain. The active generation now supplies profile-
  specific pass metadata; live MSAA, None, and TAA switches showed the expected
  raw/canonical resources and temporal passes.

Exit criterion: each effective profile describes only passes and resources it
executes; Vulkan planning has no non-MSAA phantom resolve or false producer.

### 2. Make MSAA execution and background composition correct

- [x] Rebuild and rerun OpenGL MSAA. Diagnose the last live rejection:
  `MultisampleResolve` did not match the sealed stage family or required
  per-view order despite an admitted `aa=Msaa msaa=4` profile. Separate shader
  startup/admission timing from a persistent command-order defect.
- [x] Verify four-sample raw color/depth attachment descriptors, the active
  graph's raw-raster-to-resolve-to-AO dependencies, and changed mesh-edge
  coverage in both backends against AA-off captures. The canonical HDR target
  has intermediate coverage values at edges that are black with AA off.
- [ ] Capture raw per-sample visibility and depth plus the canonical resolve in
  RenderDoc to independently confirm sample contents and nearest-sample
  sidecars. MCP cannot read multisample textures; the manual RenderDoc trigger
  disconnected before writing a capture.
- [ ] Reproduce or rule out the `without metadata: 100065` warning seen in a
  direct RenderDoc-launched MSAA run. The later isolated Vulkan session showed
  no repeat through MSAA/None/TAA/MSAA, so its cause is not established.
- [x] Define and enforce background source alpha. Inspected skybox shaders
  previously output `vec3`, while destination-alpha blending requires alpha
  one to close uncovered pixels. Admitted sky shaders now output opaque alpha,
  and the background path has an explicit multiple-draw policy.
- [x] Align background material validation with the actual subdraw material.
  The earlier validation examined submaterials while cached render state came
  from the primary material; the selected policy now prevents that mismatch.
- [x] Track sample count and fixed-sample-location mode in OpenGL multisample
  texture allocation identity so a surviving wrapper cannot retain storage
  from a different MSAA profile.
- [x] Rebuild and validate the final Vulkan implementation. The corrected
  frame completed with four-sample targets and visible edge coverage; the
  earlier binding-55 validation failure did not recur.

Exit criterion: OpenGL and Vulkan each render Sponza with four-sample mesh
coverage, no resolve-order or admission errors, coherent visibility/depth
sidecars, and correct background pixels at partial coverage.

### 3. Prove TAA, TSR, and DLAA visually

- [x] On OpenGL and Vulkan, capture Sponza still views and camera motion with
  TAA and TSR. The native temporal view receives current jitter, history is
  ready after settling, and scene color remains fresh across tested mode
  transitions. Compare antialiasing quality and fast-motion ghosting separately
  before claiming visual parity or production quality.
- [x] Recheck and fix the TSR-to-TAA black/stale-color anomaly. Temporal pass
  declaration now uses the active profile, and pass-index lookup uses active-
  generation metadata. Live Vulkan TSR-to-TAA produced nonblack matching color
  input, HDR output, and history; OpenGL did likewise.
- [x] Confirm DLAA invokes the vendor path and produces a valid final image on
  supported hardware. OpenGL on the available NVIDIA setup evaluated NGX DLAA,
  survived two off/on transitions, and presented a nonblack Sponza image.
  Preserve explicit diagnostics when the vendor path cannot run.
- [ ] Establish Vulkan DLAA availability on a configured vendor-capable setup,
  or document the backend's explicit unsupported result. The live Vulkan
  session did not exercise a vendor resolve.
- [x] Check mono behavior in the isolated desktop editor on both backends.
- [ ] Check ordinary stereo behavior on an XR runtime. Quad-view temporal post
  state still has only left/right eye slots; establish its supported behavior
  before claiming quad-view parity.
- [ ] Run a controlled still/moving/cut comparison of final TAA, TSR, MSAA,
  and DLAA mesh-edge quality, including ghosting and background partial
  coverage. The current captures prove execution, fresh output, and MSAA edge
  coverage but are not a full quality assessment.

Exit criterion: before/after captures show that each mode affects mesh edges as
intended, with stable scene color, camera motion, and mode transitions. Record
the actual backend, profile, frame state, captures, and observed limitations in
the investigation.

### 4. Final verification and closeout

- [x] Run the narrowest integrated editor build and live editor path after the
  fixes above. Review rendering/OpenGL/Vulkan logs for persistent validation
  errors and warnings; distinguish startup shader compilation from steady-state
  frame failures. Named live sessions settled and rendered; see the
  investigation for the one Vulkan startup framebuffer error and the separate
  unsuccessful RenderDoc launch.
- [x] Update the investigation with the current result for each AA type and
  backend, including any unsupported hardware or view configuration.
- [ ] Only after live feature validation and the user's explicit clearance,
  add or update regression tests. Review the existing test expectations that
  assume TAA native jitter is absent.

Use a named isolated editor session for the live checks and stop only that
session when done. Keep disposable captures and logs under
`Build/_AgentValidation/`; the 2026-09-22 evidence root is
`Build/_AgentValidation/20260922-111614-advanced-aa/`.
