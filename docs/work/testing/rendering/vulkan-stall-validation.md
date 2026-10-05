# Vulkan Stall Validation Protocol

Use this protocol for each change in the
[remaining-work checklist](../../todo/rendering/vulkan-stall-remediation-todo.md).
It preserves the existing validation requirements. Moving these instructions
out of the checklist does not waive a gate or grant test clearance.

The [validation history](../../progress/rendering/vulkan-stall-remediation-results.md)
records prior results and their limits. Follow the
[editor workflow](../../../developer-guides/ai/agent-editor-workflows.md)
for current session and capture commands.

## Mandatory One-By-One Protocol

**Only one implementation item may be Active. Do not begin the next fix until
the current fix has passed its focused build, live behavior, performance and
applicable regression checks, with evidence recorded.** A section containing
multiple independent changes is not an exemption: split them into child items
and validate each child before starting the next.

Read-only evidence gathering may run independently. Overlapping edits, runtime
mutations and A/B measurements must remain serialized. Do not combine readiness,
invalidation, resource publication and UI changes in one unvalidated patch.

For every implementation item, complete this gate:

- [ ] Record the entry evidence, exact owning path, one falsifiable hypothesis,
  affected workloads, smallest coherent change, and a check that could reject it.
- [ ] Define acceptance before editing: correctness invariants, target metric,
  numeric budget/tolerance, sample count, observation window and observer overhead.
  Use measured baseline variability and existing master contracts; do not choose
  a convenient threshold after seeing the result.
- [ ] Record ownership/thread affinity, source/configuration generations,
  cancellation semantics, lock order, publication and retirement dependencies.
  Require an explicit design review before changing concurrent/native lifetimes.
- [ ] Implement only this item. Identify this exact source diff and the binaries
  used for validation; a build from before the edit is not evidence for the fix.
- [ ] Immediately run the narrowest useful check. Run the owning project build
  with no new warnings, then the relevant isolated editor path. A successful
  build, quiet log or `git diff --check` alone cannot close a runtime item.
- [ ] Exercise the original trigger plus the item's cold/warm, pending/failure,
  mutation and lifetime cases below. Actually view saved images when rendering
  or UI behavior is involved; tool success is not image validation.
- [ ] Compare baseline and changed captures under matching conditions. Confirm
  the targeted mechanism changed, performance met the predeclared budget and
  neighboring stages did not absorb the removed cost or accumulate backlog.
- [ ] Check affected regressions, warnings, allocation/retention growth and
  teardown separately from steady-state behavior. Explain every new diagnostic.
- [ ] Record pass/fail, evidence, remaining risks and user feedback. Only then
  mark the item Validated and select the next item.

### Failure And Deferral Rules

- On failure, stop progression. Repair the same item and rerun its checks. If the
  hypothesis was falsified, record that result and reassess the nearest owner.
  Do not build another fix on top of a known failing change.
- A flaky or ambiguous result is not a pass. Repeat with a discriminating capture;
  if evidence remains insufficient, mark Blocked and report the blocker.
- If a candidate's entry condition is disproved, mark Deferred/Not Applicable
  with evidence and the condition for reopening it. Do not mark it Fixed.
  Advancing past an unvalidated behavioral change requires an explicit scope
  decision; do not silently waive a failed gate.
- Any rollback must be scoped to this item's own edits. Preserve unrelated user
  work. Do not reset the worktree or switch branches to obtain an A/B baseline.
- A change to instrumentation or measurement settings requires a comparable
  baseline before accepting performance results from subsequent items.
- A reordering requires a written evidence-based reason and satisfied dependency
  gates. The measured steady-state owner may take priority over cold-only work;
  this must not become simultaneous implementation.

### Status And Test Clearance

Use Pending, Active, Blocked, Validated, Closed, or Deferred/Not Applicable. Checked
implementation boxes are not closure. Validated means the item's live gates
passed; Closed additionally means its required follow-ups and applicable test
work/user confirmation are resolved. Record test clearance separately.

Do not add or modify regression tests until live feature validation succeeds and
the user explicitly clears test work, as required by repository policy. Existing
tests may support diagnosis or verify a sound change when applicable, but must
not replace live validation. A clearance request or a checklist is not clearance.
Pending test approval remains visible and does not permit claiming full closure.
Once cleared, add only focused deterministic coverage in the existing test project
and rerun the corresponding focused checks before closing the item.

## Comparable Workloads And Attribution

Freeze source revision, local diff and binary hashes. Record build configuration,
SDK/runtime, device/driver/power state, backend, submission mode, validation
layers, debugger, CPU/GPU observers and logging. Also record scene readiness,
camera path, output/internal resolution, effective AA, executed features and
native/canonical draw coverage. A HUD draw count is not the full native workload.

Keep cold process, persisted-cache restart, warmed stationary and controlled
motion conditions separate. Use at least three matched runs per performance
condition and at least 60 seconds per warmed window. Declare and justify any
alternative before comparison. Elapsed warmup time alone does not prove readiness.
Do not delete user caches to force a cold run.

Compare minimal-observer and instrumented conditions. A change to observation
requires fresh overhead and retention admission before later speedup claims.
Record all-frame counts, p50/p95/p99/max, successful presents, missing GPU
samples, dropped/suppressed events, queue delay, loading time and backlogs.
Use actual allocation-owner/thread evidence; one thread counter is not a
cross-worker total.

Split acquisition wait, held-body work, native calls, GC pauses and descheduling.
For detailed attribution, account for at least 99% of the root interval and
expose unattributed gaps of 50 us or more. If capture cannot meet this contract,
record a blocker. EventPipe samples alone do not establish on-CPU execution.
Use aligned scheduler/native evidence when needed. Never sum overlapping CPU/GPU
intervals or subtract unrelated medians to infer an exclusive leaf cost.

Keep configured GPU preferences separate from executed passes. Compare CPU and
GPU independently. Dense GPU observations need their own overhead admission;
capture/replay time is not an unperturbed frame benchmark.

## Publication Mutation And Temporal Matrix

For each case, record source command/primitive, world/view, mutation/publication
identity, admitted/consumed frame, expected visibility boundary and actual output.
Inspect images, velocity/history and relevant descriptor/resource identities.
An unreachable race or failure remains unverified.

| Case | Required proof |
| --- | --- |
| Stationary commands; new or reused publication | Accepted identities remain current with zero identity-only scene-content callbacks. Stable handles alone do not permit stale-snapshot reuse. |
| Transform motion, then stop | Real changes reach the declared frame. Previous/current transforms and velocity advance and settle. |
| Material value/resource/override, visibility/pass, geometry/primitive count | Correct membership, bindings and geometry reach every consumer. Include an off-camera object moved into view and in-place native arena-buffer replacement. |
| Add/remove/re-add, shared mesh/material, edit bursts | Final state is visible. Recycled IDs cannot expose stale resources; coalescing cannot discard newer changes. |
| Mutation during callback or after capture; secondary views | Acknowledgement preserves newer pending state. Collections cannot clear another view's updates. No deadlock or unbounded retry. |
| Rejected/aborted/retried/superseded publication; disposal | No provisional failed identity becomes consumable. Accepted content stays coherent and dependencies retire exactly once. |
| Pending upload, cancellation, failed successor upload, competing transitions | Transfer precedes binding. Exact tickets/generations show bounded progress, recovery or explicit failure, with no premature readiness or double retirement. |
| Camera cut/motion, disocclusion, moving object, resize, pipeline/world/view switch | Correlate frame/view/history, jitter, matrices, velocity, depth and resets. Inspect multiple positions and sequences for trails or stale output. |
| Vulkan/OpenGL and multi-view | Match accepted work and settings, verify visible mutations, and record hardware/fixture limits. Equal resident counts alone do not prove parity. |

## Validation Operations

These instructions apply to each runtime validation run. Select the narrow
owning build, then build/run an isolated editor that contains that exact change.
Typical owning projects are:

```powershell
dotnet build .\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Do not run all five for every item. Shared-code changes require the relevant
backend/dependent validation; a Vulkan-only change does not require unrelated
application builds. Use isolated outputs if normal editor outputs are locked.

- [ ] Follow repository scratch retention and reserve one bounded task run before
  creating evidence. Use its `logs/`, `reports/`, `mcp-output/`, `mcp-captures/` and,
  when necessary, `renderdoc/` folders. Never rely on disposable evidence as the
  sole durable record of a gate result.
- [ ] Verify requested backend/scene/settings before launch. Use a unique named
  session per live iteration through the session manager; never manipulate the
  user's editor by process name or assume a default MCP endpoint belongs to it.
- [ ] Use `pwsh` where installed; the recorded Windows environment also supports
  this explicit Windows PowerShell session-manager invocation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Start -Name <unique-item-session>
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Invoke-Mcp.ps1 -Session <unique-item-session> -Method ping
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Stop -Name <unique-item-session>
```

- [ ] Capture CPU profiles and state through that exact session. Store viewport
  captures under the current task run and view the PNGs. For visually ambiguous
  pass/resource failures, use the repository RenderDoc workflow; do not treat
  capture/replay timings as an unperturbed performance benchmark.
- [ ] Review the owned session's logs, separating steady-state issues from
  shutdown noise. Restore temporary settings, stop only owned sessions, close
  capture tools and record cleanup before completing the gate.

### Per-Item Gate Record

Copy this record into the investigation for each item/child; link it from the
remaining-work checklist when its status changes. Use repository-relative
evidence paths.

```text
Item / owner / status:
Prior validated item and any approved reordering:
Hypothesis and disconfirming check:
Baseline source/configuration/cache/scene identity:
Change scope and dependency/lifetime invariants:
Predeclared metrics, budgets, tolerance, repetitions and window:
Changed source diff and validated binary/session identity:
Focused build command and result, including warnings:
Live scenarios and exact evidence paths:
Before/after distributions, sample validity and observer overhead:
Correctness/images, failure cases, retention and adjacent regressions:
Pass/fail decision and reason; does it explain the original symptom?:
Test clearance state, approved focused checks and results:
User confirmation / remaining risks / next permitted item:
Temporary settings and owned session cleanup:
```

