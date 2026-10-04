# Browser UI clip commands and focused input bridges

Date: 2026-10-02

## Ordinary solid-material publication

The ordinary desktop `UIMaterialComponent` constructor uses the canonical
`UnlitColoredForward.fs` fragment with the UI raster state. Browser publication
now recognizes this UI consumer before ordinary scene-Unlit projection. The
scene-Unlit admission requires a different depth/blend profile and previously
rejected this material before shader companion lookup.

The target-only projection verifies the pinned canonical fragment, exact
material/component types, one finite `MatColor`, empty texture and feature
state, and the existing UI raster profile. It permits only the owning ordinary
UI components' bounds callbacks and the engine surface publisher that empty
surface metadata can install during YAML reload. Custom stages, callbacks,
material features and explicit scene semantics remain rejected.

A read-only size walk uses the ordinary cooked serializer's member selection
and reference traversal before projection. It inventories UI consumers, model
LODs, renderable meshes and overrides, created mesh commands, decals, landscape,
camera/postprocess materials, framebuffer materials and declared pipeline
requirements. Registered root serializers and opaque custom payloads retain
their existing serializer-owned graph contracts. Runtime-created consumers and
later material reassignment still require runtime admission.

One detached, source-free material is retained per original reference. Its ID,
parameter array, `MatColor` object and render-options aliases are preserved;
all ordinary graph references select that same target object. A material also
used by a scene or pipeline consumer rejects with
`BrowserCook.UiSolidConsumerConflict` before its stage is removed. This is a
limit of the UI-only projection contract, not a general WebGPU limitation.
Authored YAML, desktop construction and raw texture storage are unchanged.

Source/structural inspection and diff whitespace validation cover the change.
The genuine Editor cook, fresh binary hydration, alias checks and browser
rendering still require the integrated CI/runtime run; no local .NET runtime
was available for this change.

## Engine-owned UI rendering state

The engine-frame command format now uses version 3, with 112-byte draw records.
The prior 80-byte records had no unused space for viewport and crop state. C#
resolves viewport/scissor values into the reusable command arena; JavaScript
validates their bounds and executes them. Clip motion does not rebuild retained
mesh descriptors, alter material binding keys, or introduce per-draw interop.
The viewport/crop conversion changes the Y convention once at the engine/backend
boundary, and the crop is intersected with both viewport and attachment bounds.

Each operation has a separate pass, so an absent viewport or scissor cannot
inherit the previous operation's state. Zero-area crop records remain in the
packet to preserve resource-upload ordering, but the executor does not draw them.
Managed and JavaScript draw counters and framebuffer-production tracking exclude
those clipped draws. Partial scissored attachment clears remain explicitly
unsupported rather than clearing the entire attachment silently.

`ClipToBounds` quads and bitmap text can now remain in the engine UI batch path.
A crop-keyed run carries a double-buffered value snapshot; the desktop unbatched
path remains unchanged. This is not a general custom-UI-material implementation.
Inherited clipping and rotated hit bounds are addressed by the later shared UI
change described below.

Independent review closed an upload-ordering issue in the initial empty-crop
implementation. The final WebGPU graph compiled with zero warnings/errors;
JavaScript syntax, ordered-upload and zero-area command probes pass. Actual
browser UI crop pixels and full hit-test parity remain acceptance work.

## Focused text and button bridges

The existing browser input leaf feeds shared engine devices and player mappings
from pointer, touch, keyboard, wheel and standard Gamepad events. The focused
text bridge adds browser IME/composition and selection behavior without replacing
the engine's text component. A native DOM button proxy exposes an engine-focused
button's accessible name and activation to the browser. Enter/Space activation
does not also generate a duplicate engine key activation; nonactivation keys,
including Escape and arrows, still reach shared input. Tab keeps browser focus
navigation available.

Both bridges use focused-target identity and generation checks. Text edits,
selection changes and actions additionally carry an expected content version,
so a stale DOM event cannot overwrite a managed edit made between frames.
Pending IME commit suppresses Enter/Escape until the deferred commit is applied.
Removing a proxy restores canvas focus and clears held keys. Hidden proxies are
marked inaccessible and removed from tab order before focus transfer; stop and
restart epochs reject late events and listeners are aborted on teardown.

Accessible labels have a cached value and independent numeric revision. The
unchanged label string does not cross the managed/JavaScript boundary every
focused frame. Label changes do not masquerade as text-content changes or reset
selection. Independent review, JavaScript syntax checks and focused production
method probes pass. The coherent full compile/publish gate records final managed
compilation separately in the current checkpoint.

These are focused-control bridges, not a complete semantic accessibility tree.
Screen-reader traversal of the whole canvas, nonfocused controls, inherited UI
clipping, all widget roles/states and physical IME/assistive-technology behavior
remain open. No browser/device acceptance or general engine-UI completion is
implied by the source and boundary checks.

## Shared inherited clipping follow-up (2026-10-03)

The shared UI crop resolver now intersects a component's rectangular crop with
clipping ancestors. Both the batched quad/text path and the unbatched desktop
2D command use this resolved crop. Transform bounds use all four corners when
forming the axis-aligned scissor for rotated elements; rotation does not make
this a polygon clip. The renderer consumes bottom-left canvas-local pixels and
converts their Y coordinate once at its backend boundary. Canvas input uses the
same crop to reject mouse and contact hits outside visible descendants, after
testing the target's local bounds through its inverse transform. Input blockers
therefore only act where their clipped UI is visible. These paths reuse their
existing input collections and avoid per-hit snapshot arrays.

The canvas-to-local matrix now composes canvas world before the element's
inverse world matrix, which is the inverse of the existing local-to-canvas
matrix under `System.Numerics` row-vector multiplication. The corresponding
parent-to-child conversion uses the same order. A rotated, translated canvas
with a translated child now round-trips a local point through canvas space;
the previous order failed when those transforms did not commute. Hit testing
compares the canvas point directly with the renderer's canvas-local scissor.

Live screen-space acceptance should cover nested clipping and moving/rotated
ancestors on desktop OpenGL/Vulkan and browser WebGPU: a child protruding beyond a clipping
parent must have neither pixels nor hover/click/touch response in the cropped
area, while its visible part remains interactive. Toggle the ancestor's clip,
move it, and confirm the crop updates without recreating the child. The browser
glyph and image batch paths also need actual pixel review. Full canvas
accessibility traversal and physical IME behavior remain separate acceptance.

## Shared offscreen coordinate correction (2026-10-03)

Camera/world canvases previously submitted their 3D world matrices and world
crop rectangles to a local 2D camera and pixel target. Their 2D input-tree bounds
were also left unset, and the world-pointer boundary compared an already-local
point with a translated rectangle. A translated or tilted canvas could therefore
render outside its texture or reject a hit that lay inside its local content.

The shared 2D path now removes the owning canvas's world placement from material
quad and text matrices, in both batched and individual commands. Direct 3D
commands retain their world matrices. Clip and input-tree bounds transform all
four element corners into the same canvas before forming the AABB; transforming
a previously flattened world AABB would lose the orientation of a tilted canvas.
The input tree and world-pointer boundary use a zero-origin local canvas extent.
Screen canvases retain their identity world placement and existing crop values.
No shader, desktop material, or binding contract changes are involved.

The ordinary UI hierarchy composes local matrices up to the owning canvas when
forming these bounds. This avoids a floating-point world/inverse round trip
shifting an exact integer clip edge by one pixel as the canvas tilts or moves.
The targeted Runtime.Rendering/InputIntegration build passes with zero warnings
or errors. An ignored runtime probe passes 77 checks through the actual shared
crop resolver, input broad phase, local hit tests, individual model commands,
and material/text batch snapshots. Screen, translated and tilted world, and
camera-space fixtures all retain the exact `(40, 30, 100, 80)` parent scissor;
the previous world rectangle instead starts at `(815, -205)` in the translated
fixture. Nested clipping, clip toggles, empty intersections and rotated children
are covered. This is a managed execution witness, not GPU pixel acceptance.

## WebGPU offscreen canvas route (2026-10-03)

Camera/world canvases now reuse the existing owned framebuffer, `PreRender`
producer and sorted `TransparentForward` world quad on WebGPU. The Advanced
pipeline also declares and executes the CPU-only producer lane before scene
stages. This does not introduce another UI renderer or replay opaque geometry.
Screen canvases retain lazy offscreen allocation; a stable render-info identity
allows draw-space changes without losing world registration. Resizing retains
the sampled texture object and lets the existing backend generation/retirement
path replace its physical storage and dependent views. Composition requires
current-frame production; an unready canvas defers the frame instead of sampling
an uninitialized or stale allocation.

The cooked quad, image and bitmap-text batches use semantic revision 2 with an
80-byte view block: the existing projection matrix followed by `UIOutputMode`.
Screen mode preserves the existing display RGB and straight-alpha behavior.
Offscreen mode accumulates linear premultiplied RGBA into a single-sample
`Rgba16f` texture cleared to transparent black. RGB and alpha both use
`One, OneMinusSrcAlpha`. The source-free `UICanvasSurfaceV1` material samples that
associated color directly into the scene's HDR target, depth-tests without
depth writes, and applies one WebGPU render-texture Y flip. It is an unlit scene
surface subject to scene exposure and tonemapping, so screen and world canvases
need not have identical displayed brightness. Desktop GLSL and material creation
remain on their existing branch.

sRGB images use a retained `rgba8unorm-srgb` view declared when the same RGBA8
allocation is created, so texels decode before filtering. Explicitly linear
images keep the raw view. Authored tint decodes independently; text fill/outline
colors decode before coverage composition, while bitmap coverage and alpha are
unchanged. A detached image-material cook projection preserves the existing
`BaseColor/IsSrgb` role independently of imported image color space.
Batch/material keys distinguish image interpretations. Both aliases
retain the original texture owner and retire through the existing replacement
and teardown path; no view is created per warmed draw.

Revision-1 UI artifacts remain readable. New publication requires the V2 batch
variants and the canvas-surface variant; older shader catalogs require recooking,
without an authored-asset migration. Individual UI draws, custom stages, rotated
glyphs, non-bitmap text and direct/backdrop-grab world canvases remain explicitly
outside this cooked profile. Offscreen sRGB storage images are rejected rather
than substituting an incompatible sampled view.

### Published UI image metadata

`PublishedUiImageMaterial` has a bounded version-1 payload for the source-free
authored image semantic. It carries one image and the existing 19-byte
`PublishedStandardLitTextureSettings` record: anisotropy, comparison enable and
function, imported color space and usage, normal-map green flip and storage
usage. The lit version-1/version-2 payloads, raw `XRTexture2D` codec, XRTS and
authored YAML layouts remain unchanged. The cooked factory registration and
compiled browser assembly metadata include the new carrier; publishing requires
the newly built assembly closure.

Projection borrows the source image without changing it. Decode restores the
record only onto the newly decoded image and reconstructs the texture list and
BaseColor role with the same object reference. The role's `IsSrgb` bit is encoded
separately, so distinct material roles may interpret one borrowed image
differently. Runtime role lookup requires exact reference identity. The cook
projection joins separate YAML occurrences only after proving equal persistent
identity, complete raw payload and all seven settings. It releases cloned
material subscriptions before disposal and never destroys borrowed images.
Legacy generic UI materials retain their existing reader; unknown versions and
unsupported or nonresident image profiles reject explicitly.

Each custom cooked payload has an independent reference scope. Two material
carriers may retain the same persistent image ID while decoding separate local
images; neither can resolve or change an image decoded earlier in the outer
graph. The runtime witness checks an outer raw image plus two UI carriers: the
outer image retains raw-default anisotropy 1 and each local image restores 8.

## Displayed placement and DOM geometry

Pointer rays and DOM geometry now use the same resolved world/camera placement
and local extent as the composited quad, including startup extent fallback and
the camera-distance clamp. Camera/default world placement also uses the camera's
forward vector directly with `Matrix4x4.CreateWorld`; negating it mirrored local
X relative to the camera's right vector and placed the quad on the wrong side of
its computed bottom-left corner. Explicit authored world transforms are retained.

Spatial DOM bounds use the current observing viewport camera. The canvas's
anchor camera controls placement. Stack-based polygon clipping intersects actual
control corners with the canvas extent, ancestor scissor and homogeneous view
frustum before projecting to top-left normalized DOM bounds. A control crossing
near/far/side planes keeps its visible part. The text editor uses those same
visible bounds; focused-editor generations and cross-canvas ownership are
unchanged. Exact curved-edge bounds under nonlinear lens warps and scene-depth
occlusion are not resolved by this geometric projection. No GPU readback or new
occlusion policy is introduced.

## Offscreen validation

The four production UI recipes cook successfully. The managed Rendering,
WebGPU, InputIntegration, ShaderCooker, BrowserContentCooker and Editor graphs
compile; the final managed probe builds have zero warnings/errors. Runtime
witnesses cover:

- 86 offscreen factory, placement, pointer, resize ownership, physical target,
  premultiplied-alpha, actual Editor image projection and cooked reload checks,
  including all seven settings, anisotropy 8/trilinear, independent sRGB/linear
  roles, exact/shared versus conflicting same-ID aliases, unchanged source/YAML,
  clone disposal, isolated decoded images, sampler edits after material creation,
  payload-version rejection, V2 ABI rejection and legacy V1 artifact reading
- 48 production DOM geometry checks, including screen parity, nested/rotated
  clipping, observer versus anchor, distance clamp, partial frustum/reversed-depth
  cases, text geometry and zero warmed allocations
- 19 actual JS resource/import checks for declared sRGB formats, raw-view
  preservation, shared allocation, bounded rejection and last-alias retirement
- Six lit version-1/version-2 payloads are byte-for-byte identical between frozen
  pre-UI and updated runtime assemblies: all-shared, metallic/roughness-shared and
  four-distinct images. The production `CookedAssetReader` preserves all settings
  and role references (196 baseline and 208 updated checks). The actual compiled
  `BuildBrowser` metadata includes the new UI carrier, and hash-verified metadata
  in Published mode restores it through the production BinaryV2 reader

The final native-WASM browser build also passes with zero warnings/errors,
including the P/Invoke scan, native compilation and linking. The isolated host
runs the same pinned SDK tasks in process for task-host compatibility; no scan,
link or packaging check is disabled.

These are source, cook, managed execution and JS-boundary witnesses; they do not
establish GPU pixels or browser/device/assistive-technology/IME acceptance. The
bounded UI carrier preserves omitted image settings for its admitted published
materials. General standalone/raw texture metadata preservation remains a
separate asset-cooking gap; this change does not migrate that texture schema.

The saved `BrowserUiParity` fixture first reached the genuine Editor YAML loader
on `5e42251` in
[run 37219100537](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37219100537).
It stopped before cooking because two canvas component discriminators used the
UI transform/control namespace instead of the component's actual CLR namespace.
Both now name `XREngine.Components.UICanvasComponent`. All 132 discriminator
occurrences, covering 20 distinct names, were checked against source declarations
and loader contracts; only those two scalar values changed. The aliases and 47
polymorphic transform wrappers are preserved. Source checks do not establish a
successful engine load, cook or browser UI result; the normal publisher rerun
remains required.

The same authoring-path audit also found text/layout and button-child access
before `ComponentsSerialized` assigns component owners. Shared text now retains
those property values while unattached and uses its normal activation refresh;
button text lookup tolerates a missing owner. Text-input updates and focus keep
their existing creation-capable sibling access once attached. Activation replays
nonempty input text without inserting components into the activation collection,
and explicitly refreshes a changed sibling layout if outer cooked hydration is
still suppressing property notifications. Both sibling orders were reviewed.
Initial empty input text preserves independently authored sibling text, matching
the previous empty-default setter behavior; no new omission/explicit-empty
storage distinction is introduced. Attached inactive/editor updates and live
nonempty-to-empty edits retain their existing paths. Independent source review
and whitespace checks pass; engine compilation and load/cook remain pending.

A separate pre-existing cooked desktop limitation remains: parent-button
activation can update an already-built child text color while notifications are
suppressed, leaving the desktop material uniform stale. WebGPU batching reads
the current color directly. That older desktop material refresh path is not
changed by this bounded owner/activation repair.

On `e2c47497`, the genuine Editor publisher gets past world type/component
loading and stops at strict default-font admission. The fixture had no
`Config/engine_defaults.asset`; normal project loading therefore cloned the
engine's Roboto Medium default, while the declared browser default bitmap
profile requires Roboto Regular. The fixture now explicitly selects Regular
through that existing typed settings asset, and staging copies Config with
the other authored inputs. The publisher guard, engine-wide default, font
source/license and bitmap cooking algorithm are unchanged. Successful normal
cook and browser interaction still require the next exact-commit run.

The Windows job of
[run 37225704827](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37225704827)
on `f628183b` successfully loads, cooks and publishes the complete saved UI
project, including its selected font/license and required UI shader variants.
Its actual browser startup then stops while preloading that font:
`IOCompressionBrotli_PlatformNotSupported`. Font payload version 2 uses Brotli
for each atlas mip; the pinned .NET browser assembly deliberately provides an
unsupported-platform stub for that decoder. The
[.NET 10.0.12 project source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.IO.Compression.Brotli/src/System.IO.Compression.Brotli.csproj)
defines that exact failure. AOT, trimming or native build switches do not enable
this managed API in the browser pack.

The owner approved a versioned portable encoding on 2026-10-04. Font payload
version 3 now stores each R8 mip as raw bytes or a block from the existing
managed LZ4 dependency, choosing the smaller form. Desktop retains version 2
reading; a browser loading version 2 receives the named
`BrowserFont.CookedV2RequiresRecook` diagnostic. The reader preserves the
4 MiB-minus-envelope payload limit, 64 MiB decoded-atlas limit, exact mip sizes
and trailing-data rejection. Authored fonts and YAML are unchanged; normal
publication recooks their derived payloads and hashes.

On `051c32f8`, all four genuine Windows Editor publications and the ownership
tests pass in
[run 37229683052](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37229683052).
The UI browser job now completes font preload and reaches engine draw
preparation. The version-3 raw/LZ4 reader therefore runs in WebAssembly without
the former Brotli platform exception. No measured cooked-font size is asserted
by this result.

The [UI job 111522095410](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37229683052/job/111522095410)
still reaches the existing 45-second first-frame timeout. Its final state is
`Ready`, `DefaultRenderPipeline`, 1280×720, HDR disabled, AA `None`, 34 recorded
mesh draws, 35 commands and a pending draw. Pipeline decline, resource-generation
failure, Advanced stages and pending program preparation all report none. The
failure screenshot was inspected and is blank. These counters describe recorded
work, not a submitted complete UI frame; pixels, input and lifecycle acceptance
remain unqualified.

The existing program diagnostic walks every cached WebGPU program, including UI
programs, and the JavaScript shader/pipeline preparation registry. It does not
identify a managed mesh deferral, incomplete texture producer or retained
resource receipt. The first draw deferral now retains its source, reason and
owner without allocating frame strings. The existing startup-status request
formats those references and up to eight retained resource requests, including
their identities, states, owners and bounded descriptor excerpts. Deferred
resource exceptions preserve their actual request or program owner. Mesh and
canvas-surface paths report their concrete preparation stage. Each new frame
resets the retained reason; ready frames report none. Readiness, acceptance,
timeout and fallback policy are unchanged.

This is a diagnostic repair for the unresolved first-frame failure, not evidence
that UI rendering is fixed. Independent source review and whitespace checks cover this
change; the local C# compiler and browser execution are unavailable. The next
exact-commit runtime run must identify the pending owner before a behavior fix
can be justified and the original UI checks can complete.

The next exact witness, `23dde12c` in
[run 37233797886, UI job 111533989821](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37233797886/job/111533989821),
identifies `PublishCanvasSurface=CanvasTextureProducerPending` with zero retained
resource requests. The canvas texture allocation is current, but no producer
has recorded it in the engine frame. The 34 draws and 35 commands remain
unsubmitted because the complete producer/consumer frame is required.

Source tracing confirms that the world canvas registers its producer as a
PreRender method command. Its nested UI pipeline must clear the offscreen color
attachment before drawing any UI, which marks the texture produced even when
the canvas has no glyphs or material quads. The published legacy command-package
path is admitted after swap; lack of explicit package preparation alone does
not explain this failure. The runtime witness still cannot distinguish an
absent callback from a nested pipeline decline before the clear.

The canvas previously discarded the nested pipeline's boolean result through
`Render()`. It now observes `TryRender()` and propagates a declined required
producer through the existing preparation-pending exception on renderers that
require atomic frame authoring. It does not mark a texture produced, accept an
unsubmitted image or change previous-image admission. The startup diagnostic
also reports the shared render-frame identity and outer viewport's published
PreRender command count through the existing per-pass count API, honoring its
command-collection override. It reports at most eight offscreen canvases with
their callback attempts, last callback render frame, hook state, collect/swap
generations, published package state, target size,
cached framebuffer completeness, source/attachment identity and nested
decline/resource failure. Indexed traversal avoids hidden collection snapshots.
The tree walk is limited to 8192 node visits, 8192 components and
depth 128 and runs only when status is requested; frame capture updates only
scalar fields. Callback counters and frame identity are recorded at callback
entry so an inactive or changed-draw-space guard cannot look like a missing
invocation. This closes the missing nested-failure attribution, but the
underlying producer failure and UI acceptance remain unresolved pending the
next exact-commit browser run.

Independent source review and whitespace validation pass. Local compilation
and browser execution remain unavailable; no new tests or publication were
performed for this correction.

The exact `918b91af` witness in
[run 37238768014, UI job 111548627233](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37238768014/job/111548627233)
narrows the failure to missing offscreen collection and publication. At shared
render frame 2662, the outer published PreRender pass has one command and the
offscreen callback has run 2644 times, including that frame. Its canvas is
active, claims installed hooks and has matching 512×512 target/surface texture
identity, but collection remains 0, swap remains -1 and its package is Empty.
The nested pipeline reports `NotPublished`; no draw or clear reaches recording.
The one retained glyph-buffer request is already Ready and unclaimed.

The shared caller-thread timer dispatches both visibility collection and buffer
swap. Source tracing instead identifies an event-registration error:
`EnsureTimerHooksInstalled()` removed each callback before its first addition,
even though its installed flag was false. `RuntimeEngineTimer` forwards those
removals to the host's `XREvent`. When that event already exists, an unmatched
removal enters its removal queue. The first dispatch consumes additions before
removals, so the queued removal deletes the newly installed callback. The saved
Screen Canvas precedes Offscreen Canvas and establishes those events; a later
canvas therefore loses its collect/swap callbacks while still reporting its
installed flag as true. Private installation flags are not cooked fields.

Canvas installation now adds its callbacks only after the existing installed
guard. Deactivation removes only an installed registration. The change leaves
shared event queue ordering, host rebinding and desktop/caller-thread phase
dispatch intact. It also leaves nested package validation and atomic producer
admission in place, so an absent collection cannot become a fabricated empty
publication or an accepted previous image.

The focused production-method reproduction is: keep one unrelated listener in
an `XREvent`; remove a never-added canvas listener; add that listener; invoke.
The existing event code invokes the unrelated listener and loses the canvas
listener. With the corrected canvas installation, its first and repeated
dispatches retain exactly one callback. Source review also covers repeated
install, uninstall before first dispatch, uninstall/reinstall after dispatch,
and host replacement with both pending and applied registrations. If an old
listener is already applied, uninstall/reinstall before dispatch appends the
replacement then removes the old occurrence. If its first addition is still
pending, uninstall cancels that addition and reinstall queues one replacement.
The installed guard prevents repeated installation/removal. These are
source-level traces, not executed C# results. No installed .NET compiler or
PowerShell executable is available locally; no substitute model, new harness
or toolchain installation was used. The next genuine Editor publication and
browser run must demonstrate advancing collect/swap generations, a Published
nested package, an accepted first frame, and the original UI pixel/input checks.

Independent source review approved this two-file correction and the lifecycle
sequences above. Scoped whitespace validation passes. Actual C# reproduction,
compilation and browser acceptance remain unexecuted for this correction.

On `996101db`,
[run 37241689117, UI job 111556548582](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37241689117/job/111556548582)
gets beyond the missing canvas publication and reports the real authored
`BrowserUiParityWorld` running. That establishes an admitted queue submission
with the matching output identity; it does not establish completed GPU work or
correct UI pixels. The next failure is the harness's input-observer wait. It
installed its hook after running, then removed it after five seconds while
waiting six seconds for another natural `syncTextFocus` call. That call follows
an admitted engine Step, so observation unnecessarily depended on another
admitted frame. Source inspection confirms the player and observer import the
same input module and the method is not rebound on the instance.

Failure capture then spent 180 seconds waiting for a stable element during
`scrollIntoViewIfNeeded`. The inspected full-page image is blank and reports
device acquisition after a later device-loss warning. Neither that later image
nor the aborted payload requests prove the earlier observer timeout's cause.
Independent source tracing found no proven shipping admission or resize defect:
64 pending receipts can defer later frames, but their state was not captured;
equal backing extents leave the surface generation unchanged.

The UI harness now holds only the original player entry-script request until
the canvas exists and read-only prototype observers are installed. The entry
is a deferred module, so HTML parsing can create the canvas while the request
is held; the observer imports have no dependency on that entry module. Setup
has a five-second bound and then continues the original script request and
bytes, or aborts that request on failure. The input hook survives startup and
restores after its first natural invocation or cleanup. The existing running,
17-control, pixel and delivered-input assertions remain required.

Explicit snapshots now include managed rendering status, receipt/error-scope
statistics, the host's last frame timestamp, admission wait, visibility, canvas
bounds/backing size and surface generation. Polling control predicates do not
request those heavier diagnostics. Before startup, bounded observations retain
32 status changes, 32 lifecycle/resize records and four existing
`xrengine-canvas-failed` details; failure events are also delivered directly to
the report so a later page-evaluation failure cannot erase them. Failure-only
state and full-page capture run concurrently with five-second limits and do
not scroll or await stable bounds or another animation frame. These changes
repair observation ordering and failure evidence; they do not claim that the
unexplained post-startup delay or device loss is fixed.

Local checks execute the production harness orchestration with mocked page
operations for setup failure/owned-request abort and post-startup observation
failure/original-request continuation. They verify concurrent bounded failure
capture and cleanup, but do not execute browser layout, WebGPU or the managed
engine. JavaScript syntax and whitespace checks pass. Actual pixel, input,
resize and restart qualification still require the next genuine browser run.
Independent source review approved the final harness ordering, bounded evidence,
cleanup and preservation of the player's return/error behavior.

A separate source-reviewed context-menu correction tracks ownership of its
one-shot post-update arming subscription. Previously, hide/reopen before the
next post-update could queue a second removal that deleted the new callback,
leaving outside-left-click dismissal unarmed. Explicit ownership queues one
removal and preserves deferred arming. No local C# execution was performed;
the authored BrowserUiParity world contains no context menu. Reentrant IsOpen
observers and additional same-post-update edge cases have no new runtime
qualification from this correction.
