# Runtime hardening investigation

Status: Active; editor parity and strict NativeAOT acceptance remain open.

Evidence root: `Build/_AgentValidation/20261002-211500-runtime-hardening/`.

## MonkeyBall edit and play lifecycle

The isolated `runtime-hardening-parity` session uses OpenGL with passthrough audio because the local Steam Audio native library is absent. The original ignored unit-world settings are restored after each run. The sample selects its own Advanced render pipeline when its project loads.

- Edit-world loading originally failed because the sample required native rigid bodies during begin-play, before component activation created them. Native validation now occurs at the first pre-physics tick; authored scene-reference validation and initial round setup remain in begin-play. Edit-world loading subsequently succeeded.
- Entering play initially failed during editor snapshot restoration with strict parity `TypeResolutionScan` for `WorldSettings`. Temporary registrations only moved the symptom to `EFogMode`; those registrations were removed after confirming that the editor snapshot codec is intentionally an authoring serializer.
- The synchronous authoring codec now suspends inherited player diagnostics while preserving nested explicit player checks and exception restoration. Snapshot reference repair and game-mode callbacks retain checks. Focused scope, source-path, and construction tests pass 10/10. The live retry passed snapshot restoration with zero parity violations, then crashed in native PhysX before scene recreation; this is not runtime acceptance.
- A viewport PNG was captured and viewed after the failed transition. It was entirely black while the world was stopped. This is failure evidence, not visual rendering acceptance. Multiple camera views of a running scene remain required.
- No user-reported runtime success has been recorded for these changes.

## OpenGL compute validation

- Generated shader includes failed in nine GPU tests because the test loader sent raw includes to the driver. The loader now uses the production include resolver.
- Sixteen SurfelGI tests used an obsolete shader directory. Correcting it made the tests execute and exposed two stale buffer fixtures. They now bind the current transform atlas while retaining object-space storage and world-space cell expectations.
- The indirect, material scatter, physics-chain shader, and SurfelGI lane passes 46/46 without skips.
- The shared compute capability check queried an indexed workgroup limit with the scalar API. Correcting the query enabled previously skipped softbody and dependency-ordering tests. Actual GPU execution exposed identity rotation for a translated 90-degree cluster. The covariance antisymmetric signs and fixed power iteration were incorrect for that case. CPU and GPU now use bounded symmetric Jacobi diagonalization with the largest algebraic eigenvalue. The original 90-degree GPU case passes; expanded rotation, degeneracy, and dispatch timing checks are pending.

## Reference corpus availability

Two humanoid fixture byte mismatches were solely CRLF conversion; restoring LF exactly reproduces the manifest hashes, and Git attributes now preserve them. The schema-7 exporter and editable-families Unity source clip declared by the corpus are absent. The user confirms they are not available on this machine. Reference hashes and provenance checks remain enforced; complete corpus acceptance cannot be claimed without those artifacts or a newly captured, verified corpus.

## Detached PhysX target investigation

The crash stack ends in queued "setKinematicTarget". Logs show replacement bodies created after the old scene was destroyed and before a replacement scene existed. Released-pointer guards were already present; the missing native precondition was scene attachment. The proposed correction retains the latest target on the physics thread and replays it after native insertion, with cancellation and release cleanup. Live replay and repeated transitions remain required.

## Publication and source import

A later strict publish stopped during cooking because untyped automatic import treated the C# gameplay source extension as a compute shader and generated a shader companion asset. The companion was preserved outside sample assets. Automatic shader registration now excludes ambiguous .cs while explicitly typed shader loading retains support. This cooking failure does not supersede the last meaningful NativeAOT inventory of 977 diagnostics; its empty warning output is not acceptance.

## Subsequent live evidence

The detached-target correction completed two isolated play runs (120 and 180 seconds) with zero parity violations and no native crash. The viewport and composited screenshots from two requested editor-camera positions were both viewed and black. These camera changes may not move the active gameplay follow camera; they do not establish multiple gameplay viewpoints. RenderDoc successfully injected the managed named-session launcher and connected to its editor child, but no capture completed after the trigger. The renderer continues advancing frames while visibility preparation repeatedly rejects the canonical draw image as exceeding its GL deformation-sidecar capacity. Later visibility stages reject their required execution order. Capacity initialization and units are under investigation; the unrelated grid stereo/mono shader compilation error is also recorded.

Cooked skinned-mesh round-trip execution exposed another concrete defect: separately assigned metadata and buffers did not update the immutable skinning-state metadata before canonical validation. The cooked reader now publishes their coherent generation within its deferred object-publication scope before validation. Existing behavioral tests are running; expectations remain unchanged.

## Restored scene invariants

The canonical visibility rejection also covered an empty draw table, not just an oversized one. Empty tables with no deformation payload are valid. After accepting that case and rebuilding procedural shapes from their authored dimensions after deserialization, the live manifest contains seven mesh submissions. The 180-second and subsequent 300-second play runs complete without parity violations or native crashes, but viewed images remain incorrect.

Three independent causes are now supported by live evidence:

- A saved `TextFile` reference for `ColoredDeferred.fs` resolves through the path cache to an `XRShader`. Conversion then fails, leaving restored shader source empty. The before/after shader diagnostics change from 3,224 source bytes to zero. Snapshot reference lookup now checks the saved type before returning cached or loaded assets.
- Six static mesh matrices remain identity while the physics-updated ball moves correctly. Deserialization suppressed the transform setters' local-matrix invalidation. The transform completion callback now marks local and world state dirty with deferred evaluation. World startup evaluates this hierarchy before gameplay activation.
- World begin-play successfully possesses the MonkeyBall pawn, then automatic game-mode spawning replaces it with a flying-camera pawn. The gameplay camera has no active viewport. Automatic startup spawning now preserves an existing, live same-world pawn; explicit spawning retains its replacement behavior.

These corrections are awaiting a fresh isolated build and live validation. The earlier unchanged screenshots do not prove a frozen renderer: the default viewport was not bound to the gameplay camera, and the camera-targeted screenshot tool can render without an active viewport.

## Confirmed scene restoration and command targeting

The shader type, transform invalidation and pawn-preservation repairs pass live OpenGL inspection. The 300-second `parity-snapshot-invariants` run completes with zero parity violations; viewed captures from distinct camera offsets show the course and ball with authored geometry and placement. `parity-repeated` completes three transitions in one process with zero violations.

A further live defect was isolated to generic MCP lookup: the global object cache can contain a dormant snapshot instance with the same persistent ID as a live component. Public ResetRound and SetTilt calls reported success without changing the visible scene, whereas the component-specific lookup reached the active instance. Generic inspection and invocation now resolve active-world nodes, transforms and components first, retaining asset/global fallback for objects outside the world. The next live run reports the requested tilt `(0.2, 0.1)` and visibly moves the ball after public method invocation. Evidence is under `mcp-output/live-object-after-method.json` and `mcp-captures/Screenshot_20261003_001228_675_c361707f2c82447b8f535fe683883957.png` in the current run root.

Restored shader source subscriptions have also been repaired so source edits invalidate the shader after suppressed property notifications. Live hot-reload acceptance for that change remains open.

## Persistent mesh identities and stopping point

Two external-prefab reloads restored a mesh ID correctly from cooked bytes, then
lost it when deferred object-cache publication found the original instance under
that ID and generated a replacement. Explicitly assigned identities now survive
the collision; the original cache owner remains unchanged and generated identities
retain collision retry. All 40 prefab serialization cases and the external import
stable-closure/rollback case pass without changing their GUID assertions.

Material shader-stage caches now rebuild after restoration and pass live
active-component inspection and a 300-second zero-violation run. The added nested
MCP object path is compiled but still needs the actual live parameter/source-edit
probe: direct asset-ID cache lookups are not conclusive for restored instances.

The user requested stopping on 2026-10-03. The current incomplete status,
unvalidated edits, remaining defects and exact external corpus block are recorded
in the [runtime hardening handoff](../../todo/runtime-regression-and-nativeaot-hardening-todo.md#handoff-status--2026-10-03).
