# Runtime Regression And NativeAOT Hardening Progress

Updated: 2026-10-02

Status: Active; strict publication and exhaustive regression acceptance remain open.

Checklist: [runtime regression and NativeAOT hardening](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md).
The [branch stopping-point handoff](runtime-hardening-branch-handoff-2026-10-07.md) preserves the exact 2026-10-03 unaccepted edits, warning counts, validation limits, and resume order. Current code work and checks use the new todo and validation destinations.

## Current baseline

- Source commit: `79ab16f8f98778b92624e238a261aa558c1c684d` with the completed shadow investigation changes still uncommitted. Preserve these changes, the staged OpenVR submodule change, and existing untracked dependency directories.
- Evidence root: `Build/_AgentValidation/20261002-211500-runtime-hardening/`. The initial status, diff summary, SDK information, console logs, and test reports are retained there. Evidence is disposable; findings needed for acceptance are recorded here.
- The repository-pinned SDK 10.0.401 is available in the shared validation tooling directory. No SDK pin or dependency upgrade is needed.
- The fresh exhaustive baseline records 4,952 passes and 648 failures. TRX records 90 `NotExecuted` outcomes, including 83 beyond the console's seven skipped tests. The inventory separates 417 Vulkan failures and one hardware failure, leaving 230 software failures requiring classification and repair.
- Previous shadow validation passed 342 targeted tests and 50 isolated checklist cases. Its Vulkan screenshots and CoreCLR parity evidence do not satisfy this checklist's OpenGL or strict NativeAOT publication requirements.

## Initial findings

- The assembly-wide test composition installs asset, rendering, and model services and serializes NUnit workers. It does not yet establish all the per-fixture global-state ownership required by the checklist.
- Non-Vulkan source contracts now require explicit canonical paths; `ReadExactFile` no longer searches by filename. Three focused path tests pass. Legacy Vulkan relocation behavior remains outside this tracker. Stale assertion classification is still incomplete.
- No deterministic randomized full-suite runner was found. Three seeded ordering runs remain required after the current failures are repaired.
- Generated contracts already cover substantial metadata, cooked envelopes, factory registration, GPU records, and transform storage. The related implementation tracker explicitly leaves component construction ordering and strict publication acceptance unfinished; these cannot be marked complete from source presence alone.

## Next validation decisions

1. Classify every current test result by subsystem, failure signature, and scope; preserve explicit Vulkan and hardware exclusions.
2. Establish current strict publish diagnostics and the actual player dependency graph.
3. Exercise the relevant live runtime before changing regression assertions, then repair behavior and narrowly update stale contracts with authorized coverage.
4. Close only after the documented deterministic regression, packaged-runtime, diagnostics, graph, and allocation gates have evidence.

## Current execution findings

- The fresh aggregate Debug build reached test execution. A canonical Release editor build independently succeeded with zero warnings and errors. The exhaustive baseline completed with 4,952 passed, 648 failed and seven skipped out of 5,607 tests (console totals, about 17 minutes of test execution). Classification and TRX outcome auditing are in progress; these are not actionable non-Vulkan totals.
- Strict MonkeyBall publication stopped before NativeAOT analysis: the authored game project lacked `XREngine.Runtime.Host`, and gameplay scene-membership code referenced a concrete PhysX actor. The generator now includes the host assembly, and the sample uses `IPhysicsSceneAttachedActor`. A fresh publish remains required.
- The provider stream parser silently accepted `response.failed`. It now throws `AgentModelException` with the provider message while retaining terminal diagnostics. The original focused test reproduced the defect; all three existing parser tests pass after the fix.
- The humanoid conformance runner was missing its model-asset-pipeline project reference. That build prerequisite is restored. The runner then rejected the corpus before sampling: `avatar-arbitrary-corrections` and `clip-test-anim` have CRLF working-tree bytes whose LF hashes exactly match the manifest, while the declared editable-families clip and schema-7 Unity exporter are absent. No hashes or checks were weakened.
- The first isolated editor build failed copying dependency outputs. A subsequent build passed with zero warnings and errors. Its startup then failed because the locally configured Steam Audio backend lacks its native library. Temporary software validation settings select passthrough audio and OpenGL and restore the original settings afterward; this does not certify Steam Audio.
- MCP endpoint readiness precedes active-world readiness. The parity smoke now waits for world capability and collects only current-run log files instead of mixing historical sessions into the diagnostic count.
- Component construction now has a generated-factory path preserving constructor-time node access. The allocation-free synchronous construction context and exception cleanup are under focused review; a narrow Core build passed with zero warnings and errors. Live parity and construction regression acceptance remain open.
- Disk pressure temporarily prevents editor startup below its 10 GiB minimum. Automatic approval review rejected cleanup of the completed shadow task's disposable build outputs, including after explicit user approval. Filesystem compression preserves the outputs while recovering space; no validation gate has been waived.
- Concurrent broker catalog, model-client, broker tests and broker-guide edits appeared after the baseline snapshot. They are outside this task and must be preserved. The initial broker-catalog test failure must be reassessed against that work rather than independently rewritten here.

## Runtime and publication follow-up

- Strict publication now reaches final NativeAOT compilation. The latest run reports 977 IL2xxx/IL3xxx warning rows; this remains a failed strict acceptance result. Compiler-tool projects no longer inherit player publication properties, and the portable dependency guard recognizes the SDK's implicit NativeAOT compiler tool without allowing explicit unreviewed package references.
- The generated published game project now references source runtime projects rather than copying the editor's package graph. Remaining reachable authoring/reflection and optional-provider roots still require removal or correct static contracts.
- Exact non-wrapping evaluation of manually authored looped clips now samples their endpoint. Unity's imported cyclic source rule remains restricted to imported metadata. The existing runtime assertion passes alongside the focused scene lifecycle, component sibling, and provider checks.
- The GPU test compiler now uses the production shader include resolver. SurfelGI fixtures use the canonical shader directory and fail explicitly for absent required assets. Actual GPU execution then exposed two obsolete fixtures still supplying transforms through culled-command storage; they now supply the current transform atlas while preserving world-space binning and object-space storage assertions. The combined indirect, scatter, physics-chain, and SurfelGI OpenGL run passes 46/46 with no skips.
- MonkeyBall edit-world loading exposed a native-actor readiness assumption in begin-play. The sample now defers native-actor validation and reset until its first pre-physics tick, after component activation. Live edit/play/re-entry acceptance remains pending.
- Two content-addressed humanoid fixtures had only working-tree line-ending differences. Restoring LF reproduces their existing SHA-256 values exactly; Git attributes now preserve those bytes. The user confirms the missing schema-7 exporter and editable-families Unity clip are not available on this machine. Their recorded hashes and reference-capture provenance have not been replaced or waived. Complete reference-conformance acceptance remains blocked on recovery of those exact artifacts or a separately produced, provenance-verified reference corpus.
- The user explicitly authorized runtime/AOT assertion and fixture repairs after validation of each affected runtime path.

- Generated component construction passes five focused regressions, including nested construction and exception cleanup. Cooked array factories support leased, statically registered one-dimensional and jagged arrays while preserving wire-type conversion; focused cooked checks pass.
- Editor play-transition snapshot serialization is now explicitly treated as synchronous authoring work. Nested player callbacks retain parity checks. The latest live retry passed snapshot restoration but crashed in a detached PhysX kinematic-target update; runtime acceptance remains open.
- A later publish failed in cooking after automatic shader import incorrectly claimed a C# gameplay script. The explicit shader import registration now excludes ambiguous .cs. The 977-warning inventory remains the latest completed analysis; the subsequent early failure is not a clean publish.

## Latest validated results

- Detached PhysX kinematic targets now defer until native scene attachment, retain latest-value semantics, and cancel on null or a superseding non-kinematic mode. Two subsequent isolated live runs completed 120 and 180 seconds of play with zero parity violations and no native crash. Repeated transitions within one process and focused cancellation/lifetime checks remain open.
- Seven softbody integration checks pass, including CPU/GPU rotated-offset comparisons for planar anisotropic, nonplanar, collinear, and coincident clusters. The warmed 128-cluster by six-member GPU dispatch measured 30,720 ns median (five samples); this is one-machine evidence, not a cross-hardware performance claim.
- Live camera post-process schema confirms canonical AO labels, GTAO tuning and visibility-bitmask controls. The stale source-contract fixtures are being repaired against public schema behavior.
- Running viewport and composited captures were both visually inspected and remain black. RenderDoc injection reached the named editor, but capture timed out. Logs identify recurring visibility-preparation rejection because the canonical draw image exceeds the GL deformation-sidecar capacity; later stages then fail the required execution order. This remains failed rendering acceptance.
- The next completed strict NativeAOT analysis contains 914 IL2xxx/IL3xxx diagnostics, down from 977. Cooking completes after excluding C# scripts from untyped shader import. Strict publication remains unaccepted; no warnings were waived.

## Current follow-up evidence

- The earlier strict publish reports 751 warnings, down from 914: 704 first-party and 47 third-party/runtime warnings. The exact-line comparison removes 163 diagnostics and adds none. Override annotation mismatches IL2046 and IL3051 are absent. Strict publication still fails; no packaged smoke was run from this result.
- The name-filtered non-Vulkan run reports 4,223 passes, 215 failures and six skips in 11 minutes 55 seconds. This is not an acceptance total: backend-sensitive cases remain despite the name filter. The per-test inventory under `reports/tests/nonvulkan-current-failure-inventory.json` records signatures, owners, baseline overlap and unknown isolation status. The largest retained groups are rendering source/path contracts, humanoid behavior and rendering runtime behavior.
- The AO, PhysX lifetime and cooked-skinning lane passes 20/20. Restoring cooked skinning now publishes its immutable buffer metadata before renderer use. Two external-asset metadata failures remain reproducible in the broader cooked lane and also failed in the baseline.
- Procedural shape restoration now recreates geometry from authored properties after deserialization, disposes replaced render wrappers, and restores explicit event subscriptions and initial culling state while property notifications are suppressed. The live manifest now contains seven mesh submissions instead of zero. The 180-second run completes with zero parity violations and no native crash.
- Three default-viewport images were viewed and show overbright geometry but remain unchanged after the gameplay camera moves. An explicitly targeted camera image was captured after play had ended and is not useful evidence of live camera behavior. Camera-targeted validation remains open; the visible image alone does not establish correct gameplay rendering.
- The empty canonical draw table is now accepted when it has no deformation payloads, preserving valid background-only execution. An oversized table and inconsistent nonempty payload still fail diagnostically.
- Cooked schema inspection and schema-only module methods are excluded from published compilation. Both normal and `XRE_PUBLISHED` Data builds pass with zero warnings and errors. Legacy animation payloads still root the graph serializer, so the whole serializer cannot safely be removed until those payloads have static codecs.

## Restored scene and active-world validation

- Restored shader references now honor the saved asset type before accepting a path-cache hit. Shader text and include paths survive the XRShader/TextFile path collision. Restored transforms explicitly invalidate their local matrix, and begin-play preserves an already possessed pawn in the same world.
- A 300-second OpenGL run completed with zero parity violations. Viewed captures from two camera positions show the authored blue course, orange ball and red bumpers with the expected transforms. These results supersede the black and overbright intermediate captures above.
- Three enter/exit play cycles completed in one process with zero parity violations. `Run-AotParitySmoke.ps1 -PlayCycles 3` exercises repeated restoration and registration cleanup.
- Generic MCP object operations now prefer the active world's node, transform and component instances before global caches. A live ResetRound followed by SetTilt updates the visible scene; the component reports the requested tilt and a viewed capture shows ball movement on the tilted course. Dormant snapshots retain their persistent identities without intercepting gameplay commands.
- Network contract validation passes 42/42 after leased static runtime-type registrations and the current wire-protocol fixture version. Native root-motion and animation serialization checks pass 7/7; AO public-schema checks pass 15/15.
- The focused snapshot/GameMode lane passes 13 cases, including the new asset-cache collision, restored transform and authored-pawn checks. Its remaining inline-animation snapshot failure also occurs in both recorded baselines and remains under repair.
- The subsequent 300-second active-world command run also completed with zero parity violations. An in-memory source-text edit increments the cached shader revision, but later identity inspection shows this may be a dormant snapshot asset. This does not establish live restored-shader invalidation. All 11 shader dependency/subscription checks pass, including idempotent restoration, detached-source isolation and destruction cleanup. All seven MCP component/object identity checks pass.
- Humanoid mapping fixes raise the focused result from 18/42 to 28/42: partial torso chains no longer consume arm branches, semantic mirrored sides drive outward arm traversal, named short fingers exclude metacarpal/twist helpers, and placeholder-only avatar definitions no longer suppress later initialization. Fourteen failures remain; native pose authority and old fixtures require further diagnosis.
- Live material inspection found one stored shader but an empty fragment-stage cache after snapshot restoration. Explicit list ownership and post-deserialization reconstruction are implemented; live acceptance is pending.
- Capture image analysis failed when a 32-by-24 image was requested at 64-by-64: the imaging adapter preserved aspect ratio but read back the requested larger rectangle. Exact-size geometry now carries the runtime codec dimension contract through all resize filters. Existing capture-analysis validation remains pending.
- Typed generated model envelopes now preserve inline animation payloads during snapshot serialization without bypassing the MemoryPack recursion guard. Existing authoring reflection envelopes remain readable. The combined snapshot, asset-packer, cooked-envelope, temporal capture and restored-material lane passes 40/40 with zero build warnings/errors.
- Material shader-list restoration passed another 300-second live run with zero parity violations. Active-component inspection reports its fragment shader; a three-frame OpenGL sequence completes with zero failed/dropped frames, contact sheet and frame differences. The contact sheet and a second camera view were both viewed. Live bloom enable/disable schema inspection also preserves the expected tuned values and visible scene.
- Explicit native humanoid neutral corrections now survive generated canonical corrections, while clear/replacement removes old overrides. The focused lane passes 31/42, including an accepted 12-degree neutral pose and clear-to-bind sequence; remaining preview and custom-axis cases stay open.
- The canonical Release editor build after these changes passes with zero warnings/errors. Its subsequent strict NativeAOT result is recorded below.

- The subsequent canonical Release editor build passes with zero warnings and errors. Strict publication with `-NoClean -NoSmoke -NoEditorBuild` reports 688 IL2xxx/IL3xxx warnings and exits with failure; no allow-warning switch was used. The report classifies 374 general first-party, 174 cooked-binary, 66 authoring/import/cache, 27 other first-party runtime and 47 third-party/runtime diagnostics. The archived report is `reports/aot-warnings-688.md` under the current evidence root. Published animation payloads remain a major dynamic serialization root under active repair.
- Reconciliation of all 215 baseline failures against 14 subsequent focused reports records 18 later passes, 16 later failures and 181 identities without a later observation. The machine-readable `reports/tests/current-reconciled-inventory.json` preserves every baseline failure and exact later report identity; focused passes do not establish full-suite or randomized-order acceptance.

- Deferred object-cache publication was replacing restored mesh identities when an older instance already owned the ID. Explicitly assigned identities now survive publication without replacing that cache entry; constructor-generated identities still resolve collisions. All 40 prefab serialization cases and the tuned bloom defaults check pass (`mesh-identity-bloom.trx`). The unchanged external import stable-closure/rollback case also passes (`external-closure-manifest.trx`), confirming the same identity defect affected imported sub-assets. Package-manifest verification remains independently failing.
- The bloom enable/visibility schema check passes after live bloom inspection and toggling. The cooked schema inspection lane still has one BlendTree model-shape mismatch. The humanoid focused lane now passes 36/42; native-axis expectations and mirrored eye discovery remain under review.

## Requested stopping point — 2026-10-03

The user requested wrap-up before completion. The [branch handoff](runtime-hardening-branch-handoff-2026-10-07.md) preserves the completed, unvalidated, remaining, and blocked state at that time. No commit or merge was performed at that stopping point; unrelated working-tree changes remained untouched.

- Latest humanoid result: 37/42 (`humanoid-eye-6.trx`). Named-eye selection now
  passes. Four authored-axis cases remain production defects; one automatic
  upper-leg baseline expectation was corrected afterward but not rerun.
- The new closed published animation property/value codecs, exact baked
  restoration, strict nested model paths, five version-2 schemas and generated
  registry routes are implemented but compile-only. The narrow Animation build
  passes with zero warnings/errors (`logs/published-animation-strict-compile.log`).
  No new codec tests, integrated generated-player build, strict publish or
  packaged smoke covers this final slice.
- Six new object-cache publication regression cases and the explicit package
  entry-point fixture edit are unbuilt/unrun. Existing prefab and external-import
  runtime regressions passed before those test edits.
- Nested MCP owned-object paths compile in the isolated editor, but their live
  parameter/shader probe and regression coverage are unfinished. The named
  editor session is stopped.
- Automatic tool policy rejected removal of the inactive `temp-build/humanoid`,
  `temp-build/network-contracts` and `temp-build/tests` directories with “blocked
  by policy.” No removal occurred. This was not an AGENTS.md restriction.
  Generated outputs were compressed; logs and reports remain.
- Final broad-suite, three-seed randomized, strict-zero-warning, packaged-runtime
  and full allocation gates remain open. The missing exact Unity exporter/clip
  corpus is still externally blocked. Preserve the open tracker and resume from
  the [ordered branch handoff](runtime-hardening-branch-handoff-2026-10-07.md).
