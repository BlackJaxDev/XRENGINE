# Work Doc Templates

Use these templates for work docs under `docs/work/`. Each document kind holds one kind of information.

[Work docs index](../../work/README.md) · [Testing docs index](../../work/testing/README.md)

## Document Kinds

| Document kind | Location | Holds | Does not hold |
|---|---|---|---|
| Code todo | `docs/work/todo/<subsystem>/` | Open implementation, refactor, and test-code items. Each item names files, types, or behavior. | Manual runtime checks, screenshots, profiler captures, hardware rows, build logs, completed-work history. |
| Validation doc | `docs/work/testing/<subsystem>/` | Manual, runtime, visual, hardware, profiler, benchmark, and soak checks. Acceptance matrices. Reproduction steps and evidence links. | Code changes. A failed check creates a code item or an investigation instead. |
| Investigation | `docs/work/investigations/<subsystem>/` | Debug history, hypotheses, ruled-out causes, build-by-build logs. | Open backlog. |
| Architecture or guide | `docs/architecture/`, `docs/developer-guides/`, `docs/user-guide/` | Finished design: ownership, invariants, data layouts, flags, environment variables, type and file map, known limits. | Checkboxes and status. |

## Code Todo

```markdown
# <Feature> TODO

Last Updated: <date>
Status: <Planned | Active | Blocked: reason>
Architecture: <link to stable doc>  Design: <link, if not yet stable>
Validation: <link to testing doc>

## Current State
One short paragraph. What exists in code now, with type or file names.

## Open Code Items
### <Area>
- [ ] <Imperative action>. <Files or types>. Done when: <observable code result or unit test>.

## Decisions Needed
- [ ] <Question>. Owner: <role>.

## Out Of Scope
- <Item>
```

Rules for code todos:

- Each box is one code change that a programmer can do and a reviewer can confirm in the diff or with a unit test.
- Unit test code is a code item. Running the editor, capturing frames, profiling, or using hardware is a check. Put checks in the validation doc.
- When a code item is done, remove it. Do not keep checked boxes as history. Move the lasting facts to the architecture doc.
- Do not copy standing rules, invariants, or acceptance prose from the architecture doc. Link to it.
- Before you check, remove, or report an item, confirm its state in code.

## Validation Doc

```markdown
# <Area> Validation

Architecture: <links>  Code todos: <links>

## Setup
Tasks, launch profiles, settings, environment variables.

## Checks
### <Feature>
- [ ] <Check>. Procedure: <command, task, or steps>. Expected: <result>. Last evidence: <date or "none">.

Use a table (`Check | Procedure | Expected | Status | Last evidence`) when several checks share one procedure.

## Hardware Matrix

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
```

Rules for validation docs:

- Group checks by feature. Link each feature to its architecture doc.
- Give each check a procedure, an expected result, a status, and the last evidence date.
- Do not copy standing rules, exit-criteria prose, or branch and merge steps.
- A failed check links to an investigation or creates a code item. It does not become a code checkbox in the validation doc.
- Keep evidence paths under `Build/_AgentValidation/` out of the doc. Copy the required findings into the doc or an investigation.
