# Implicit cubemap mip qualification

Date: 2026-10-29
Status: Implemented and qualified on software WebGPU; broader acceptance remains open.

## Coverage gap and implementation

The previous filtered cube profile forced one integer mip with equal LOD clamps.
It did not establish implicit level selection or trilinear blending. The new
profile uses the existing sky shader's ordinary `Sample` operation with a saved
256×256 sRGB cube. Nine complete mip levels contain uniform, high-contrast colors.
The fixture cooker saves and reloads the native asset, verifies its exact
canonical bytes, and publishes it as `/game/SkyCubeImplicit.asset`. The browser
loads it through the production asset source in published interpreter mode.

Ten authored cases cover magnification, three fractional levels, a second face
axis, camera translation, HDR intensity, maximum/minimum clamps, and restoration.
Three fractional cases also run at 640×384 and again at 512×512. Two sessions
produce 32 sampled states. Central regions avoid cube edges and keep the numeric
reference independent of edge derivative rules. The camera moves away from the
foreground; existing profiles retain foreground-depth coverage.

## Independent reference and first failure

For this face-axis projection, the continuous texel footprint is
`rho = 256 * tan(verticalFov / 2) / canvasHeight`. At 512 pixels high, fields of
view 140, 160, and 170 degrees give ideal LODs 0.45810762, 1.50367485, and
2.51476010. At 384 pixels high, each increases by `log2(4/3)`.

The first live probe passed magnification but failed the ideal-logarithm
reference at 140 degrees. Green was 0.68261719 instead of 0.68054677, just outside
the unchanged 0.002 HDR tolerance. Inspection of the official
[SwiftShader sampler](https://raw.githubusercontent.com/google/swiftshader/master/src/Pipeline/SamplerCore.cpp)
identified its approximate logarithm. The cube path projects by the major axis
before differencing, so normalization cancels for these central face-axis rays.
This is separate from the generic derivative transformations described in the
[Vulkan sampling specification](https://raw.githubusercontent.com/KhronosGroup/Vulkan-Docs/main/chapters/textures.adoc).

Let `q = rho² = 2^e * m`, where `e = floor(log2(q))` and `1 <= m < 2`.
Concavity gives `m - 1 <= log2(m)`. The reference therefore brackets LOD between
`0.5 * (e + m - 1)` and `log2(rho)`. The interval has a maximum width of about
0.04304 LOD and covers the inspected SwiftShader approximation. It is not a
universal hardware precision bound. No captured pixel determines either endpoint.
The reference clamps both endpoints, decodes adjacent sRGB texels, blends in
linear space, and takes channel extrema. Shared HDR and display tolerances stay
unchanged. An independent review using only authored colors finds a minimum
post-tolerance rejection margin of 0.049315 for any single mip and a conservative
margin of 0.023304 for interpolation in encoded space across the fractional
cases. Numeric sampling covers levels 0–3. The nine-level storage assertion does
not qualify sampling of every level; the four-color cycle also cannot distinguish
an erroneous four-level offset by color alone.

The initial bounded probe passed all 32 states. The final fixture then increased
the color contrast between mip levels to strengthen rejection of interpolation
in encoded space. Its repeated live probe also passed all 32 states. It retained
30 live GPU resources and zero retiring resources, one source asset/object and
exactly 2,097,144 native pixel bytes. Both source stops were clean. The square and
resized fractional-mip captures were viewed. Only after this live result was the
check added to the regular browser regression.

Two intermediate cooker builds failed during fixture authoring: the cube type has
no authored anisotropy property, and tuple switch colors required explicit byte
conversion. Both were corrected. The existing cube backend fixes anisotropy at
one; no sampler behavior changed. The final cooker build has zero warnings/errors.

## Final evidence and limits

All 24 build/publication gates pass with zero compiler warnings/errors: four
desktop Release builds, nineteen portable BrowserWasm builds, and native
WebAssembly publication. All seven saved and cooked assets repeat byte-identically;
the previous six canonical payloads are unchanged. Fresh official Slang cooking
produces 57 artifacts. The final package contains fourteen passes and 119 assets.
Metadata verification, the compiled closure scan, and bundle checks pass. The
scan finds seventeen assemblies, 8,474 types, 42 transforms, fifteen redirects,
no replication entries, and twelve registered asset types. The final targeted
check passes all 32 states. Final captures were also viewed. The full browser run
passes all 24 selected checks in Chromium 154.0.8037.57 with Playwright 1.63.0 and
explicit software SwiftShader. Eleven sky profiles produce 356 sampled states,
with their expected live resources and zero retiring resources. All 24 strict
indirect lifecycle/resize stages pass. Three optional checks are skipped
explicitly: authored engine autostart, the optional game package, and the
standalone Jolt spike. Final host/page bytes match the tested source. Source
retention counters do not measure immediate native heap reclamation.

This adds end-to-end cooking and shader qualification; it does not change the
production shader, desktop behavior, or numeric tolerances. Complete image/cube
derivative coverage, edge/corner behavior, anisotropy, automatic mips, procedural
sky terms, hardware acceptance, platform compression, and packaged Editor export
remain open. The reference runtime and separate unpublished manifest work remain
unchanged. No full requirement is closed by this bounded profile.

Disposable evidence: `Build/_AgentValidation/20261029-183738-cube-implicit/`.
