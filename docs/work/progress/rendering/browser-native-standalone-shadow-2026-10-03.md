# Native browser standalone shadows

The native Advanced material path now consumes the existing browser standalone
directional, point and spot producers. The admitted profile retains one of each
kind, normal-Z cameras, non-atlas output and authored 8/8 contact-hardening PCSS.
Directional samples use the original depth texture and comparison sampler;
point and spot samples retain radial and projected R16Float encodings. No depth
conversion, visibility readback or CPU submission is introduced.

## Typed resources and shader interpretation

The original bank has ten float 2D texture/sampler pairs, one cube and one array.
An exact depth companion replaces slot nine with depth2D/comparison sampling,
retaining nine float pairs and the cube/array slots. Together with visibility
and AO inputs, both families stay within sixteen sampled textures and twelve
samplers. Four added native/export/MSAA recipes prove the typed interface; all
eight native companions carry the standalone-shadow and engine-surface schema
witnesses. Old artifacts require recooking instead of receiving a changed ABI.

Directional/spot receiver bias evaluates the complete biased coordinate, including
mapped/decal normals and varying normal offsets. Two same-triangle helper
reconstructions are cached per shaded sample needing slope bias, use the quad
pair orientation and original sample offset, and never read a neighboring
triangle's identity/depth. They add GPU work whose cost remains to be measured.
Spot direction and cone use exact frozen authored values, rather than recovering
them from a potentially scaled projection matrix. The shared record layout and
desktop GLSL remain unchanged.

Depth padding is allocated only when its typed family actually needs it. A
failed depth-padding allocation retires only new candidates and preserves the
existing color padding. Logical generation lookup excludes retained tombstone
rows before any shadow validation or binding.

## Producer and resource ownership

World swap publishes immutable, explicitly flagged shadow candidates. Candidate
matrices, origin/range, filter/bias values and texture identity must match the
ordered producer receipt before native shading. Reuse additionally needs the
exact committed ticket, output generation, caster signature and current GPU
allocation. All six point viewport renders must be accepted; a void render call
alone is insufficient evidence of a complete cube.

Current-frame candidates avoid a first-frame circular dependency on an earlier
committed image. Rejected/partial frames cannot authorize cadence reuse. Six
point projection matrices use inline value storage rather than per-frame arrays
or hash-only equality. Explicit WebGPU camera projection queries use the shared
projection/depth-policy cache, retaining authored settings and avoiding ambient
backend differences during world swap.

## Cold publication admission

The Editor freezes material/global metadata before temporary worlds retire and
combines startup and streamed scene requirements. Shadow descriptors represent
future receiver requirements, not manufactured textures or producer receipts.
Shared sampler/pair/bank helpers enforce the selected color/depth bank and the
127-material-cohort ceiling in both cold and live paths.

Native scene consumers declare their routes explicitly. Logical markers,
background, late and post/output commands do not require dummy scene passes.
Known meshlet routes require exact validated cooked payloads; late raster paths
are not misclassified as opaque native materials. The packaged startup audit
resolves packaged dispatch/zero-readback settings. Runtime forced strategy,
environment, quality and game-script changes remain subject to live admission.
Authored decals without a canonical producer and probes without an exact usable
cooked generation are diagnosed before publishing.

## Evidence and remaining acceptance

The integrated WebGPU, Editor and native Browser builds pass with zero warnings
or errors. All 91 production shaders cook, including both typed banks and their
native/MSAA/export companions. Independent source reviews cover the corrected
allocation, face acceptance, projection, tombstone, derivative and modular
declaration boundaries.

A separate saved fixture enables the existing directional and point lights,
selects standalone PCSS and six-face point rendering, and uses 256-pixel targets.
Genuine Editor export produces a 45,034-byte cooked world; fresh-process load
and BeginPlay preserve its pipeline/settings, four material identities and
pawn-camera link. Original authored sample files remain unchanged.

That fixture has not yet rendered in a browser. Moving sources, resized/lost
devices, sloped normal-mapped receivers, spot shadows, MSAA edges and numeric
comparison remain live acceptance work. The physical `71684f00` material pass
uses disabled shadows and does not provide this evidence.
