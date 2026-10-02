# Authored game-mode startup in a browser canvas

## Observed sequence

RollingBall's genuine Editor-published player renders and responds to input on
software Chromium and the reported non-fallback Intel Arc/Edge run. The separate
saved RenderingParity world first exposed a cooked array lookup failure for
`System.Single`; the finite framework-data lookup correction in `d8e4c748`
removes that exception.

The next real browser run still times out before the first frame. The renderer
is Ready, the displayed pipeline key has the fallback HDR/AA/MSAA fields, and
mesh draw count is zero, with no pipeline decline or resource-generation failure.
The same result appears in
[run 37048210271](https://github.com/BlackJaxDev/XRENGINE/actions/runs/37048210271)
and the preserved physical-PC evidence. This is an unrendered authored world,
not a successful GPU profile.

## Source diagnosis

`BrowserEngineSession.StartAsync` uses the shared
`Engine.PlayMode.BeginStandalonePlayAsync`. Both standalone helpers cleared the
active and per-world game modes and never called an authored mode's
`OnBeginPlay`. The original intent was only to avoid synthesizing the editor's
fallback `CustomGameMode`, which could spawn a second pawn beside an authored
one. Clearing explicit authored modes also removed required gameplay lifecycle.

RenderingParity's mode possesses its saved inspection pawn in `OnBeginPlay`.
The pawn's own begin callback binds its camera and animation references but does
not possess itself. Thus the browser creates an unpossessed local player and
cannot bind a viewport camera. RollingBall's component possesses directly, so
it did not expose this path.

A viewport without a camera never subscribes its automatic collection/swap
callbacks and returns before pipeline execution. The pipeline's decline string
therefore remains empty. Resize can independently request a pending resource
key, so that printed key is not evidence that a frame or resources progressed.
Changing AA/HDR flags or substituting a camera would hide the cause.

The earlier native construction check called `RuntimeWorld.BeginPlayAsync`
directly. It correctly checked saved pawn/camera and bone aliases but did not
assert standalone active-mode lifecycle or local-player possession. That evidence
does not cover the failing shared-host entry point.

## Correction and acceptance boundary

The correction preserves explicit configured/authored modes in the standalone
entry points while retaining the no-synthetic-mode behavior. Scene startup must
precede the owned mode's begin callback. Its end callback must run before the
world relationship is detached, with balanced ownership, rollback, and teardown
retry after failures. The public mode/world/controller/pawn identities must be
checked through the actual standalone host path, followed by the existing real
browser world run. No possession workaround belongs in the sample.

The shared correction is implemented and independently reviewed. Both rendered
and headless world hosts allow a later end-play retry after partial teardown,
while rejecting recursive end calls. Standalone startup retains ownership across
awaits and callbacks: a stop request cannot be followed by a successful late
startup, and a cleanup exception cannot replace the original startup exception.

The production Host and disposable runtime probe build with zero warnings or
errors. The probe uses real RenderingParity YAML and cooked worlds through both
synchronous and asynchronous standalone entry points. It checks active/world
mode identity, active mode state, local-controller/pawn/camera identity, two
balanced start/stop cycles, duplicate calls, configured mode precedence, explicit
`CustomGameMode`, partial startup rollback, teardown retry, and reentrant stop.
RollingBall still starts with its authored pawn and no synthesized fallback mode.
Rendered and headless host teardown-retry cases also pass. The final evidence
log ends `STANDALONE_LIFECYCLE_ALL_PASS`.

Evidence is recorded under
`Build/_AgentValidation/20261001-225000-lit-surface/logs/standalone-mode-probe-run.log`,
with the exact source/binary hashes in `standalone-mode-validation-sha256.log`
and the disposable reproduction recipe in `scratch/validate-standalone-mode.sh`.
The unused `Engine.EndPlayAllWorlds` helper still bypasses standalone-mode
teardown; browser startup/shutdown uses the corrected standalone pair.

These native lifecycle checks do not establish the first rendered
textured/deformed frame. The real browser acceptance rerun remains required.
