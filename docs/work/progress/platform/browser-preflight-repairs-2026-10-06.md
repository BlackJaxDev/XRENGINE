# Browser preflight repairs, 2026-10-06

Run [37427839671](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37427839671)
at `ced8ea3f42a1d0f43ea1ba55afb3b1f87e8175f2` completed with seven passing
jobs and two preflight failures. The Windows Editor published both shadow
worlds. Their actual cooked startup comparison, three permitted transient-ID
exceptions, strict receipts, and bundle uploads passed. The bare host,
RollingBall, rendering parity, ordinary Advanced, custom modular, and static
meshlet jobs passed.

## Shadow descriptor identity

The shadow browser job failed before loading its world. Its harness expected
`pipelineArtifact` inside a compiled program descriptor. The real catalog owns
the pipeline scope and pass; that field is not part of the descriptor schema.

The repaired check keeps the exact catalog selection, descriptor and WGSL
hashes, source identity, and pass comparison. It checks the native program name,
the sole `advancedShadeNative` compute entry point, the `[16, 16, 1]` workgroup,
and the absence of a material variant. Caster checks retain semantic version,
vertex and output profiles and now also require the exact program names.
Checks against three actual native descriptors and two canonical caster
descriptors pass. This does not establish a shadow pixel result.

## Quiet trace environment

The UI job failed at `BrowserEnvironmentRejected`, before persistent claim,
browser launch, or trace start. The workflow exported an empty `DEBUG` value.
Pinned Playwright 1.63.0 deletes that empty variable during import. Its bundled
source and six import-only controls confirm this behavior with zero child
process attempts. The local controls used Node 24.19.0; the failed CI job used
Node 22.23.3. The explicit bundled deletion establishes the source behavior.

The guard now permits only absent or empty `DEBUG` after import. It still
rejects every nonempty value. The workflow clears logging before imports.
Other environment checks, assertions, deadlines, capture bounds, cleanup, and
sanitized output rules are unchanged. Independent review confirmed that the
replacement capture remains unused. The failed activation record is retired;
a new exact source, push, attempt-1 run, and separately reviewed activation
record are required before that same approved capture can start.

The preserved UI failure artifact is `11397631830`, SHA-256
`a2ccba410c6c931d7ce1656488853c57520b7cba670fce9d0aff43d862b2983a`.
The shadow preflight artifact is `11397851304`, SHA-256
`cebfc7a3c773abefc08d6043a33368dce22e0e577f59dbf6427b29db26b6b735`.
Neither failure proves the runtime behavior after these repairs.
