# Vulkan lifecycle evidence collector

`Tools/Collect-VulkanLifecycleEvidence.py` uses the Python standard library to
collect live Vulkan evidence from a named isolated editor session. It is a
diagnostic harness, not a regression unit test or a phase-closure gate.

## Run

Use an existing **stopped** named session created by
`Tools/Manage-McpEditorSession.ps1`. Configure its environment JSON with
`XRE_VULKAN_VALIDATION=1`, `XRE_VULKAN_SYNC_VALIDATION=1`, and
`XRE_UNIT_TEST_WORLD_SETTINGS_PATH` pointing to the intended generated fixture.
The current camera and mutation sequence targets the Sponza fixture. A different
fixture requires deliberately choosing matching targets and camera positions.

```powershell
python Tools/Collect-VulkanLifecycleEvidence.py `
  --session <stopped-isolated-session> `
  --environment Build/_AgentValidation/<run>/scratch/session-environment.json `
  --output Build/_AgentValidation/<run>/reports/lifecycle-evidence
```

The output directory must not already exist. The script builds Release through
the session manager, records the three relevant DLL hashes, starts the session
with `AllowDestructive` to permit its disposable world snapshot/restore attempt,
and stops that same session in `finally`. It refuses to attach to an already
running session. `--no-build` is available only when the intended session
binaries still exist and have already been verified; stopped-session cleanup
can remove them. It never saves scene or material assets.
The collector waits up to 180 seconds for the world capability after MCP becomes
ready. Use `--cases mutations-world` to collect only warmup, reversible mutations,
and world restoration when repeating those cases; the default is `--cases full`.

## Collected cases

- Warmup, stationary observation, repeated A/B views, and unseen C.
- Reversible Sponza transform and activation changes.
- A reversible scalar material change when a supported target is found.
- Shader reload and two consecutive renderer restarts, each with A/B/A views.
- World snapshot/restore when supported by the running build.

Every observation collects periodic frame/resource/cumulative-validation
snapshots, a screenshot, texture-streaming telemetry, and Advanced pipeline
diagnostics. Raw requests and replies are numbered under `replies/`. Captures
are under `screenshots/`; manager/build/runtime logs are under `logs/`.
`README.md` and `summary.json` give compact collection results. A tool failure is
recorded as incomplete; unavailable cases never silently become successful.

`TELEMETRY_OK` only means the final sampled interval advanced presents, its last
frame completed without the checked resource/binding findings, and the requested
validation configuration was confirmed without cumulative errors/overflow.
`REVIEW` preserves findings without automatically waiving known startup errors.
These statuses do not establish image correctness, mutation effectiveness,
steady-state resource bounds, or a matched performance improvement. Screenshots
are captured after the interval so they do not contaminate that interval, but
they can affect the next interval. Full raw samples remain available for review.

The world snapshot operation may preserve object IDs and undo references; even
a successful roundtrip does not prove distinct-world lifetime or collection.
Simultaneous multiple views, failure injection, in-place buffer replacement,
attached-debugger attribution, matched observer pairs, and regression unit tests
are explicitly outside this first-pass collector.

## Review afterward

Review the screenshots alongside frame progress and current pipeline state.
Separate historical readiness failures from advancing failure sequences, and
startup/teardown validation from steady rendering. Inspect per-interval resource
deltas after warmup and mutations rather than comparing absolute counts across
device replacement. Release builds may omit textual debug logs; cumulative
validation messages in the raw profiler replies remain the primary evidence.
No automatic pass closes the Vulkan remediation TODO.

## Session teardown

Stop named sessions with `-StopTimeoutSeconds 90` when teardown evidence matters. The editor vetoes the first close request and closes about 20 s later; the default 15 s timeout force-stops the process before Vulkan teardown is logged.
