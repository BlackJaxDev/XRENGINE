# Runtime Data Layout Measurements

RenderBench provides one process entry point for asset, packet-codec, and imported
hierarchy measurements. Build it before running:

```powershell
dotnet build XREngine.RenderBench/XREngine.RenderBench.csproj -m:1
dotnet run --project XREngine.RenderBench --no-build -- --scenario runtime-data-layout --runtime-lane all --runtime-manifest <fixture-manifest.json> --scenario-repeats 2 --warmup-frames 60 --scenario-frames 120 --capture-frames 1000 --output-dir Build/_AgentValidation/<run>/reports/runtime
```

The manifest records stable content identities separately from filesystem
locations. Paths resolve relative to the manifest. Archive entries must contain
the current cooked envelope and a registered published codec. A source fixture
uses the authoring importer and is labeled `authoring-import`; it does not
qualify the published asset reader.

```json
{
  "version": 1,
  "monkeyBall": {
    "identity": "rollingball-world@<content-revision>",
    "archive": "<cooked-game-archive>",
    "entry": "<world-entry>"
  },
  "avatar": {
    "identity": "representative-avatar@<content-revision>",
    "source": "<imported-avatar-file>"
  }
}
```

Keep the `monkeyBall` manifest field for version-1 fixture compatibility. It
selects the Rolling Ball world asset.

The report `runtime-data-layout.json` includes runtime, OS, processor count,
GC mode, manifest hash, and fixture content hashes. Failures are included in the
report and return a nonzero process exit code. Use the same manifest, machine,
configuration, and command for comparisons; record the commit and dirty diff
alongside both outputs. Establish the acceptable noise band from repeated runs
before choosing performance targets.

Select `--runtime-lane assets`, `networking`, or `transforms` to isolate work.
The networking lane requires no manifest and no graphics device. The transform
lane requires Vulkan and uses the production submission path; it does not
silently substitute a CPU renderer.

Asset measurements load the Rolling Ball world once and load/unload the avatar twenty times
per repeat. Each repeat reopens archives once; churn cycles share that mapping.
The report separates load/unload wall time, total managed allocation, collection
counts, archive opens, native pool usage, and last-GC LOH/heap sizes. The imported
prefab hierarchy and its owned embedded resources are destroyed on unload.
`GC.GetGCMemoryInfo` cannot report exact LOH allocation bytes or event counts:
its generation sizes describe the most recent GC. Collect an allocation trace
when those values are required; do not label a heap-size delta as allocation.

Packet-codec measurements use 1, 8, and 32 avatars. Pose, clock, and delta lanes
separately time encoding/enqueue, slab decode, and ring forwarding after warmup,
with same-thread allocation totals and payload bytes per tick. This excludes
socket I/O, identity admission, authority checks, and simulation dispatch.

The transform lane instantiates the supplied avatar 1, 8, and 32 times in the
RenderBench Advanced production world and applies deterministic local rotations. It
records propagation and render-matrix publication separately, then records the
whole production frame. Canonical publication measures the world/viewport swap
and canonical-package finalization boundary together. Statistics include p50/p95/max, allocation totals, and
store work counters. Counters are cumulative for the world; last-pass timing
fields are identified by the store contract. This animates the imported
hierarchy without invoking an authored animation clip. It is not headset or
visual-parity evidence.

## Local Realtime Session

The local session driver starts three uniquely named isolated editor sessions,
using the networking pose world and two client roles. It waits for admission,
warms up, subtracts cumulative scope snapshots, saves the JSON report and logs,
and stops only the sessions it started:

```powershell
pwsh Tools/Measure-LocalRealtime.ps1 -OutputDirectory Build/_AgentValidation/<run>/reports/local-realtime -BasePort 25000 -WarmupSeconds 10 -MeasureSeconds 30
```

The existing pose-world avatar settings supply the content. Configure that
fixture before running. A run fails if both clients are not admitted, no server
relay occurs, or the receiver applies no poses. The script does not infer visual
correctness from packet counts; inspect the remote pose in the live editor loop.

MCP tools `set_network_runtime_measurements` and
`get_network_runtime_measurements` expose the same counters for manual sessions.
Instrumentation is disabled by default. Counters measure same-thread managed
allocations and elapsed time in synchronous send enqueue, receive dispatch,
pose application, and server validation/relay scopes. Socket transport work is
outside these scopes; nested relay/send scopes overlap and must not be summed.
Subtract snapshots after warmup instead of resetting counters concurrently.

## AOT Parity Smoke

`Tools/Run-AotParitySmoke.ps1` loads the local authoring project and compiles its
scripts in edit mode before loading the selected world. It verifies play-mode entry
and exit, collects logs, and rejects parity diagnostics. Missing logs and failed
MCP operations fail the smoke. Use `-Mode error` for acceptance and `warn` only
to inventory missing contracts. Compilation alone does not establish parity.
