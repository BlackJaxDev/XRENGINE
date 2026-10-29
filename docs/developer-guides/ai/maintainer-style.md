# Maintainer Code And Writing Style

Use this guide for code, comments, XML summaries, Git text, documentation, and user summaries. It supplements [AGENTS.md](../../../AGENTS.md). Explicit user instructions take priority.

## Braces And Control Flow

Use four spaces for indentation and Allman braces. For a single simple control-flow statement, omit braces and put the statement on the next indented line. Count statements, not displayed lines. Use guard clauses and early returns.

```csharp
public void Update(float deltaTime)
{
    if (!IsActive)
        return;

    UpdatePosition(deltaTime);
    UpdateRotation(deltaTime);
}
```

Keep braces when nested control flow, scope, or mixed branch sizes are hard to read. Make the owner of each `else` clear:

```csharp
if (isVisible)
{
    if (needsUpdate)
        UpdateBounds();
}
else
{
    RemoveFromVisibleSet();
}
```

Do not force brace symmetry. Methods, types, and `try`/`catch` blocks still need braces. Compact auto-properties are fine.

## Names And Layout

- Use `PascalCase` for types, methods, properties, and events; `camelCase` for parameters and locals; `_camelCase` for private backing fields.
- Use `I` for interfaces. Follow the surrounding enum, namespace, and API conventions.
- Use direct domain names and verbs. Boolean names state a condition or capability. Include units or coordinate space where needed, such as `lengthInSeconds` or `WorldMatrix`.
- Put one type in each matching filename. Keep related overloads and lifecycle members together.
- Separate operations with blank lines. Wrap long argument lists at logical boundaries. Keep attributes next to their member.
- Use explicit types when they clarify precision, ownership, or API results. Use `var` when the type is clear.
- Use `=>` for a single clear expression. Use a block for several operations or complex conditions.
- In `XRBase` descendants, use `SetField(...)` for property changes:

```csharp
private float _speed;

public float Speed
{
    get => _speed;
    set => SetField(ref _speed, value);
}

public float SpeedSquared => Speed * Speed;
```

Keep per-frame paths free of heap allocations unless profiling justifies them. Follow the allocation rules in [AGENTS.md](../../../AGENTS.md#code-rules). Preserve failure diagnostics; do not add null fallbacks merely to shorten code. Avoid unrelated formatting changes.

## Comments And Documentation

Use ASD-STE100 Simplified Technical English: short sentences, active voice, and consistent terms. Keep code identifiers and technical names exact.

- Start XML summaries with the member's action or responsibility.
- Explain intent, invariants, ordering, ownership, units, side effects, and input limits when needed. Do not narrate obvious syntax.
- Use `<see cref="..."/>` for symbol references. Add useful `<param>` and `<returns>` text.
- Keep comments accurate. Remove empty XML tags, stale descriptions, commented-out code, and obsolete progress notes.

```csharp
// Update bounds before visibility tests so they use the current transform.
UpdateBounds();
```

## Commits, Pull Requests, And Summaries

Write a short imperative commit subject. Start with a capital letter and omit the final period, for example `Fix component spawn order`. Add a body only to explain a necessary reason or constraint. Preserve the configured Git identity and follow the [Git writing and authorship rules](../../../AGENTS.md#git-writing-and-authorship).

For pull requests and user summaries, state what changed, why it matters, what was checked, and what remains unresolved. Use the required pull request template. Match the detail to the change. State missing checks directly; a successful build does not prove runtime behavior. Avoid vague claims, promotional wording, and tool-call logs.
