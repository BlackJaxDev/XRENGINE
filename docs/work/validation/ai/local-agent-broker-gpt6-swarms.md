# GPT-6 broker and hierarchical swarm validation

Validated on Windows with .NET 10, 2026-09-23.

## Delivered behavior

- GPT-6 Luna, Sol, and Astra are exact supported API models. Route advice
  defaults to GPT-6; explicit GPT-5.6 selections remain supported and are
  identified as deprecated. Repository Codex defaults and agent roles prefer
  GPT-6.
- `start_agent_run.swarm` runs bounded GPT-6 Luna/max trees. The root and
  intermediate orchestrators decompose and review. Leaves propose one small
  anchored replacement. Host checks prevent overlapping changes and scope
  expansion; disjoint replacements can target one shared file.
- Reviewed proposals are the default. Explicit `auto_apply` authorizes
  hash-checked Windows host application, followed by readback. History retains
  the authority, tree, review decisions, exact changes, and application result.

## Live evidence

Separate Responses API calls to `gpt-6-luna`, `gpt-6-sol`, and `gpt-6-astra`
completed with exactly the requested model IDs. No silent substitutions occurred.

A disposable repository exercised a five-agent hierarchy: root, two child
orchestrators, and two leaves. Review-only mode completed with two proposals
and unchanged files. An opt-in auto-apply run completed with both files written
and read back. The generated C# was then compiled and executed:
`Double(7) == 14` and `Greet("Ada") == "Hello, Ada"`.

A second live four-agent hierarchy assigned two leaves disjoint methods in one
file. Both parent reviews passed and host merging preserved both edits. The
result compiled and executed with `Double(7) == 14` and `Triple(7) == 21`.

Live evidence was generated under the bounded disposable
`Build/_AgentValidation/20260923-105711-broker-swarms/` run. These ignored
artifacts are not required by the application or its tests.

## Local validation

- Broker and tray builds/publish: passed without compiler warnings.
- MCP initialize/list-tools smoke: passed after publishing version 0.10.0.
- 23 focused tests passed, using the tracked route, merger, workspace, and
  runner test sources in an isolated harness referencing the production
  projects. This avoids rebuilding unrelated engine/rendering projects;
  a full solution/test-project build was not established by this validation.
- Coverage includes GPT-6 defaults and deprecated explicit routes; model and
  effort validation; exact-base disjoint merging and overlap rejection;
  stale-file protection; UTF-8 BOM/CRLF preservation; explicit new files and
  raced creation conflicts; excluded paths; single-provider-slot recursion;
  model substitution; output/node/artifact limits; rejection; initial/active
  cancellation; and consistent elapsed-deadline failure in aggregate and tree.

## Limits

Agents receive immutable snapshots and do not run compilers, shells, Git, or
editor tools. Parent review does not replace compilation or runtime validation.
Application is not a crash-atomic multi-file transaction. Conditional rollback
restores existing files where ownership can be established, reports residual
paths, and retains newly created files for inspection after batch failure.
The broker is not an OS sandbox against another process changing directories.
