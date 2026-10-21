# Local Agent Broker History Write Failure

Updated: 2026-10-07

Status: fixed and validated with fake provider responses; deployment uses the
normal versioned setup workflow. No new paid request was needed to recover the
original output.

## Observed Failure

An October 2 batch reached 20 concurrently running independent workers.
Nineteen broker runs reported completion; run
`e380ee6e5e2e411990a6e35d4b02f42d` reported an internal host error with
`Access to the path is denied.` Its provider attempt had already completed:
exact `gpt-6.1-sol`, requested `max`, 208 events, no malformed events, and
10,586 reported tokens. Both the saved broker snapshot's `incremental_text`
and the history record retained the complete response.

The original response was extracted without modifying either original record.
Its UTF-8 SHA256 is
`1888b58aac1811c98d69da0f304b080086008d16567a1abb6ebb7db33995bf02`.
The calling coordinator accepted the original `0x8c052194` proposal unchanged
on 2026-10-07. It passed 64 native and 64 Wasm original-side cases, comparing
complete CPU/FPU state, memory access order, and instruction PCs, with coverage
of all 17 owned instructions. The full 29-function native/Wasm reports are
identical, with fingerprint `0x73fa6b416aa696b2`. Coordinator evidence in the
game repository is `recomp/docs/recovery/resident-function-reviews.json` and
`recomp/docs/recovery/resident-function-validation.json`. The original host
failure remains recorded, and the historical blocking handle owner remains
unidentified.

## Reproduction And Cause

Holding a history record open with `FileShare.Read` reproduced the same
Windows error when `BrokerHistoryStore` used `File.Move(overwrite: true)`.
The exception was `UnauthorizedAccessException`, HResult `0x80070005`.
The old tray/history reader used this sharing mode through `File.ReadAllText`.

A fake Responses run reproduced the full failure path: the provider completed,
`AgentRunRegistry` stored its completed result, and terminal history publication
threw. The generic host catch then replaced the successful result with an
internal failure and empty `final_text`. Incremental text remained recoverable.
The rejected publication target is the run's JSON file under
`Build/_AgentValidation/00000000-000000-shared/local-agent-broker-ui/runs/`.
The saved original message did not retain a stack or blocking-process identity,
so it cannot distinguish the tray reader from another local file holder.

Trace persistence was excluded: its writer already catches storage errors.
Provider failure and model substitution were excluded by the saved terminal
provider diagnostics. A paid retry would have duplicated a completed request.

## Fix

- Read shared history/settings with read, write, and delete sharing.
- Replace existing records/settings with `File.Replace`; create new files with
  `File.Move` from a unique temporary file.
- Serialize snapshot capture with publication so streaming writes cannot
  publish an older snapshot after a terminal write.
- Defer history I/O/access errors, log bounded metadata to stderr, and retry
  pending writes while the broker remains active. Shutdown makes a final
  attempt. Preserve the original provider result throughout.

Adding delete sharing alone while retaining `File.Move(overwrite: true)` still
failed during concurrent refresh validation. Using `File.Replace` removed that
writer failure. Readers can omit a record for one refresh during replacement;
the existing refresh contract retries it and never accepts partial JSON.

## Validation

The original held-reader runtime reproduction now preserves `Completed`, final
text, model identity, and usage while reporting a separate persistence warning.
Two canonical regression tests verify completed-result preservation, recovery
after the reader closes, and atomic writes overlapping 19 history readers.
The isolated .NET 10 harness compiled the canonical fixtures directly and
passed 23 tests: persistence, configuration boundaries, 100-worker admission,
queued cancellation, and existing exact-model integration checks.

The standard repository command selects those same fixtures:

```powershell
dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj `
  --filter "FullyQualifiedName~BrokerHistoryPersistenceTests|FullyQualifiedName~BrokerConfigurationTests|FullyQualifiedName~BrokerConcurrencyTests|FullyQualifiedName~BrokerRunIntegrationTests" `
  -p:XREngineUseExistingNativeBridges=true
```

The local harness used `MSBuildEnableWorkloadResolver=false` because the
workload version pinned by `global.json` is absent; no SDK or workload was
changed. The full engine test suite was not run. Broker publication and MCP
protocol validation use `Tools/Setup-LocalAgentBroker.ps1` and make no API call.

## Limits

Permanent storage denial can leave the tray view stale and prevent final
history persistence. Clients must retain the authoritative result before
closing its broker process in that case. Existing transports and the tray
continue using their loaded deployment until the operator starts a new one.

See the [developer guide](../../../developer-guides/ai/local-agent-broker.md)
and [user guide](../../../user-guide/ai/local-agent-broker.md).
