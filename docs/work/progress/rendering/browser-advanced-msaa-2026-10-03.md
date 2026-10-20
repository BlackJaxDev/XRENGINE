# Advanced WebGPU per-sample visibility and shading

The mono four-sample lowering retains every covered native surface through shading. This is a source/cook integration record; browser runtime acceptance remains pending.

## Physical contract

`AdvancedVisibilitySampleContract` freezes the raw encoding in resource feature bit 57 and `AdvancedVisibilityStageBackendRequest.SampleEncoding`. The existing canonical single-sample identity, metadata, selection, and depth names/formats remain unchanged. Desktop outputs retain planar 32-bit integer MS-array storage. Inactive configured sample counts do not select multisample storage.

Browser bloom, motion blur, depth of field, and automatic exposure occupy bits 58–61. These are disjoint from visibility/reconstruction flags through bit 55, material exports at 56, packed samples at 57, minimal capture at 62, and the OpenXR family at 63. Previously overlapping effect bits could declare unselected resources merely because a reconstruction flag was present.

The browser canvas advertises `RenderFrameOutputCapabilities.IndependentSceneSamples`: its physical presentation attachment remains single-sample, while `ApplyCurrentFrameProfile` retains the camera/host internal sample count. `RenderFrameOutputDescription.SceneSampleCountOverride` remains the physical count for outputs without that explicit capability, preserving desktop, offscreen, and HMD behavior. A configured four-sample scene therefore reaches its resource generation and native request without making the canvas multisampled.

The WebGPU `PackedUInt16` family requires exactly four samples and one view:

| Raw resource | Format | Values |
| --- | --- | --- |
| `Advanced.Visibility.Identity.Multisample` | `rgba16uint` | draw low/high 16 bits, primitive low/high 16 bits |
| `Advanced.Visibility.MetadataSelection.Multisample` | `rgba16uint` | metadata low/high 16 bits, selection low/high 16 bits |
| `Advanced.Visibility.SamplePosition.Multisample` | `rg16float` | actual Core WebGPU sample position |
| `Advanced.Visibility.DepthStencil.Multisample` | `depth32float-stencil8` | authoritative per-sample depth/stencil |

Every logical word reconstructs as `low | (high << 16)`. Raw zero clears decode to the canonical uncovered draw index zero. The shader resolve writes invalid canonical identities and far depth for uncovered pixels. Integer hardware resolve is prohibited.

The three color attachments cost 20 bytes/sample, or 80 bytes/pixel at x4. The additional `AdvancedShading.SampleRadianceReactive` texture is a four-layer, single-sample `rgba32float` array costing 64 bytes/pixel (126.6 MiB at 1920×1080), before raw depth and existing canonical targets. All allocations belong to the immutable resource generation and remain queue-retained during replacement.

[Core WebGPU](https://gpuweb.github.io/gpuweb/#plain-color-formats) supports multisampled `rgba16uint`; its 32-bit integer formats do not support multisampling. The [mandated x4 sample pattern](https://gpuweb.github.io/gpuweb/#rasterization) is `(3/8,1/8), (7/8,3/8), (1/8,5/8), (5/8,7/8)` in framebuffer coordinates. Publishing these positions from `sample_index` forces per-sample invocation. Native shading uses the same exact pattern without another sampled binding. Multisampled bindings are samplerless 2D textures; floating-point multisampled bindings use `unfilterable-float`, never `float`.

## Programs and execution

| Pipeline key | Interface |
| --- | --- |
| `advanced::visibility-pull-msaa` | Existing 240-byte visibility parameters/storage inputs; three packed/sample-position outputs |
| `advanced::visibility-msaa-resolve` | Group 0 bindings 0/1 raw packed integers, 2 raw depth, 3 dynamic 16-byte parameters: extent, reversed depth, reserved |
| `advanced::shade-classify-msaa` | Existing classification buffers/32-byte parameters; bindings 5/6 are the two raw integer textures |
| `advanced::shade-native-msaa` | Existing groups 0/1 with raw textures at group 1 bindings 0/1/2; group 2 outputs 0 sample radiance/reactive array, 1 velocity, 2 diagnostics |
| `advanced::shade-surface-exports-msaa` | Same native inputs and existing four material-export outputs |
| `advanced::shade-background-exports-msaa` | Existing export-initialization interface; all four uncovered sidecars are zero |
| `advanced::shade-msaa-resolve` | Group 0: 0/1 raw integer textures, 2 sample-result array, 3 HDR, 4 reactive, 5 velocity, 6 diagnostics, 7 dynamic 32-byte extent/background parameters |

The packed visibility module mirrors the existing deformation-aware vertex pull and coverage evaluation, with explicit perspective/sample UV interpolation for per-sample alpha masking. It is a separate WGSL module because cooked artifacts require an exact stage-entry set. Future vertex-pull changes must preserve both companions. The native shader includes the existing canonical `AdvancedShadeContracts`, `AdvancedShadeTextures`, `AdvancedShadeReconstruction`, and `AdvancedShadeLighting`; it does not copy the material/PBR evaluator.

The canonical resolve selects the nearest covered finite normalized depth, retaining the first sample on ties, and writes identity, metadata, selection, and depth from that same sample. The reservation sequence explicitly includes this raw-to-canonical target transition. As on desktop MSAA, depth-pyramid occlusion is disabled; the current WebGPU producer already places all frustum candidates in its early stream. AO uses the canonical resolved depth.

Classification unions all four samples into each tile's exact texture/kernel cohorts. A tile appears once per cohort. Every covered sample, including a diagnostic sample, has one owner. Native cohort dispatches write disjoint f32 sample results. Only the nearest valid-depth sample's owner writes velocity, diagnostics, and material exports; if every covered depth is invalid, native sidecars come from the first covered sample, matching the desktop evaluator driver.

The final compute reduction sums radiance in sample-index order, divides by four, adds background for uncovered coverage, and writes covered-count/four as opacity. It forces reactive to one for partial coverage or mixed complete visibility tuples and otherwise retains the maximum sample reactive value. Color/UNORM8 reactive quantization occurs only at the final canonical store. Uncovered pixels receive zero velocity/diagnostics. An authored background receives zero initial background radiance and subsequently fills only the uncovered alpha fraction through the shared exact additive blend state.

The background validator allows a source alpha-write mask only when the caller explicitly selects that existing multisample coverage override. All other depth, stencil, blend, receipt, and material checks remain in force. Ordinary single-sample validation is unchanged. The separate existing mismatch between browser sky materials (`WriteAlpha=true`) and single-sample Advanced background admission (`WriteAlpha=false`) remains outside this MSAA change.

Native shading retains the existing seven storage buffers, twelve material/global texture-sampler pairs, two dynamic uniforms, and three bind groups. The two raw integer textures, raw depth, and AO bring sampled textures to exactly sixteen. The native output uses three storage textures; optional exports and final reduction each use four. Physical admission additionally requires three color attachments, at least 20 color bytes/sample, four texture-array layers, and the existing exact depth/stencil feature. Selected x2/x8/x16 and stereo profiles remain explicit rejections.

## Verification boundary

Pinned Slang 2026.8 and the updated exact multisampled-binding grammar successfully cooked all seven MSAA companions together with the original single-sample visibility recipe. Cooked descriptors and source dependency hashes were verified by the existing cooker. No tests were added, and no browser/runtime rendering result is claimed here.

Coordinated `XREngine.Runtime.Rendering.WebGPU` and `XREngine.Runtime.Platform.Browser` builds, with all project references enabled, passed with zero warnings and zero errors. A focused ignored probe passed 24 checks using the real browser target description, production `PushFrameOutput`, `ApplyCurrentFrameProfile`, and `BuildResourceGenerationKey`. It verified camera/default x4 with a physical single-sample canvas, distinct x1 identity, inactive x4 under FXAA, and unchanged physical-sample precedence for existing constrained and external outputs. Its packed-encoding assertion supplies the encoding feature bit explicitly; it does not instantiate the Advanced resource builder or substitute for GPU execution. A separate source assertion verifies the disjoint structural feature bits and that reconstruction core flags do not enable browser effects.

Disposable evidence is under `Build/_AgentValidation/20261001-225000-lit-surface/msaa-shaders/`: `build-current-webgpu.log`, `build-current-platform.log`, `cooked/manifest.json`, `profile-mask-check.json`, and `frame-profile-probe/result.log`.

Required browser evidence includes high-bit IDs/sentinels, mixed-material edge samples, masked partial coverage, both depth directions and equal-depth ties, nearest sidecar coherence, authored background coverage, material exports, AA/resize resource replacement, and the selected post-processing output. The managed/cook boundary is ready for the coordinated Editor and browser acceptance group.
