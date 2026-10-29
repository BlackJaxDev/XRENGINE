# Maintainer Code And Writing Style

Use this guide when writing code, comments, XML summaries, Git text, documentation, and user summaries. It supplements [AGENTS.md](../../../AGENTS.md). Explicit user instructions and current repository rules take priority over historical examples.

## Basis And Limits

This guide uses selected TheraEngine source and commits from before 1 January 2023. The maintainer confirmed that this is the intended period and confirmed the author identity used by the older commits. Personal author names are omitted here.

The source review covers 12 C# files at two fixed revisions, plus selected changes and commit messages from 2017 through 2022. It covers components, actors, transforms, animation, collections, rendering, and editor settings. This is a focused engine sample, not a complete review of every repository or line. Dependency source, generated files, designer boilerplate, and imported third-party commit messages are excluded from style conclusions.

The available XRENGINE history starts on [12 June 2023](https://github.com/BlackJaxDev/XRENGINE/commit/decc1fdfb3f52e503641842732420d0ee3f96533). No pre-2023 XRENGINE commits were found in the local history or the GitHub default-branch history. Later XRENGINE code is not used as evidence of pre-2023 style. Commit dates establish the sample period; they do not prove whether a tool helped write a line.

The rules below have three sources:

- **Explicit preference:** the maintainer's brace instruction, given with this review.
- **Observed pattern:** a repeated pattern in the selected historical code.
- **Current rule:** a requirement from `AGENTS.md`, including STE, `SetField(...)`, allocation limits, file organization, and Git authorship.

## Short Reference

- Use four spaces for each indentation level. Put block braces on separate lines.
- Omit braces around a single simple control-flow statement when the structure is clear. Keep them when they make branch association or scope clear.
- Use guard clauses and early returns. Keep the main operation easy to follow.
- Use `=>` for a single clear expression. Use a block for a sequence of operations.
- Use `PascalCase` for types, methods, properties, and events; `camelCase` for parameters and locals; `_camelCase` for private backing fields.
- Use direct domain names and verbs. State units or coordinate space when they affect correct use.
- Keep related overloads and short members together. Use blank lines to separate operations or responsibilities.
- Explain behavior, ordering, conditions, and reasons in comments. Do not narrate obvious syntax.
- Write short, factual summaries. Name the change, its effect, and the validation result.
- Write a short imperative commit subject. Add a body only when the change needs more explanation.

## Braces And Control Flow

The maintainer explicitly prefers Allman block braces and no braces for a single simple statement when the result is clear. Historical examples support both unbraced guards and braces around more complex branches. A single statement can span several displayed lines. A nested `if` is not a reason to remove braces mechanically.

```csharp
public void Update(float deltaTime)
{
    if (!IsActive)
        return;

    UpdatePosition(deltaTime);
    UpdateRotation(deltaTime);
}
```

Keep braces when they make the owner of an `else` clear. For example, the `else` below belongs to `isVisible`:

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

Braces are also useful when one branch has several statements and the other has one. Use judgment; do not force brace symmetry on every simple `if`/`else` pair. Do not put the controlled statement on the same line as its condition.

The omission rule applies only where C# permits an embedded statement. Methods, types, and `try`/`catch` blocks still need their required syntax. Compact auto-properties such as `public bool IsActive { get; set; }` are normal. Use expression-bodied members for simple methods and accessors:

```csharp
private float _speed;

public float Speed
{
    get => _speed;
    set => SetField(ref _speed, value);
}

public float SpeedSquared => Speed * Speed;
```

This property example assumes an `XRBase` descendant. The `SetField(...)` call follows the current repository rule. Historical direct assignments do not override it.

## Names, Expressions, And Layout

The samples use direct verbs such as `Spawn`, `Despawn`, `CollectVisible`, and `RecalcLocalTransform`. Use names that describe the operation or domain object. Boolean names should state a condition or capability, such as `IsSpawned`, `IsScalable`, or `AllowRecalcLocalTransform`. Names such as `lengthInSeconds`, `frameCount`, `WorldMatrix`, and `LocalMatrix` make units and space explicit.

Use `I` for interfaces and follow the surrounding subsystem's enum convention. Historical enums often use `E`, such as `ERenderPass`; this is not a reason to rename unrelated modern enums. Historical `T` type prefixes and `Del` delegate prefixes are evidence of older APIs, not a requirement for new types.

Explicit local types are common, and `var` also occurs. Prefer an explicit type when it helps explain numeric precision, ownership, or the API result. Use `var` when the type is clear and the local style supports it. The sample does not justify an absolute ban on either form.

Use expression bodies for short getters, forwarding methods, event invocations, and simple calculations. Keep a block when the member has several operations or a condition that takes effort to read. Do not compress nested decisions or several assignments merely to fit one line.

Keep short declarations compact. Wrap long argument and parameter lists at logical boundaries, usually with one item per line. Keep attributes next to their member. Group related overloads and lifecycle methods. Use blank lines between distinct operations, not between every statement. The history does not establish a fixed line-length limit, a universal member order, or a strict `using` sort order.

The samples contain `?.`, `??`, pattern checks, loops, and LINQ. Use the form that makes the behavior clear and satisfies current performance rules. Historical LINQ or array creation is not evidence that allocation is acceptable in a per-frame path. Preserve failure diagnostics; do not add a null fallback merely to shorten code.

## Comments And XML Summaries

The useful historical comments explain purpose and execution rules. Examples include when a component callback runs, which objects render first, and why initialization must not run during deserialization. Use this direct style with the current STE requirement.

- Start a member summary with its action or responsibility.
- Add details for units, coordinate space, ordering, ownership, side effects, and valid input when callers need them.
- Explain a non-obvious decision or invariant beside the relevant code.
- Use `<see cref="..."/>` for symbol references and useful `<param>` or `<returns>` text when needed.
- Keep comments accurate when behavior changes. Do not leave empty XML tags, stale parameter descriptions, commented-out implementations, or temporary progress notes.

These are new style examples, not quotations from the old code:

```csharp
/// <summary>
/// Updates the local transform from translation, rotation, and scale.
/// </summary>
```

```csharp
// Update bounds before visibility tests so they use the current transform.
UpdateBounds();
```

## Commit Messages, Pull Requests, And User Summaries

Historical commit text is uneven. It includes short subjects, past-tense descriptions, long lists of changes, and many `no message` entries. The useful common trait is concrete technical wording. The history does not establish a consistent imperative tense or subject-length rule. Apply the current Git rules in [AGENTS.md](../../../AGENTS.md#git-writing-and-authorship) for those decisions.

For commits, start with a capitalized imperative verb and name the change. Omit the final period. Use a short subject; put a necessary reason or constraint in the body. Avoid vague subjects such as `Updates`, empty-message substitutes, and long comma-separated lists. Preserve the configured Git identity and the repository's attribution rules.

Examples of the current preferred form:

```text
Fix component spawn order
Preserve transform scale during deserialization
Document render command ordering
```

For pull requests, explain the problem and the resulting behavior. Add the relevant validation result and any remaining limitation. Match the amount of detail to the change. Use the repository template when required.

For progress updates and final summaries, lead with the concrete result. Use short sentences and exact technical names. State what changed, why it matters, and what was checked. State a missing check directly. Do not claim runtime success from a build result alone. Avoid promotional wording, repeated conclusions, and a list of every tool call.

Example of a user summary, only when these facts are verified:

> Updated component spawn order. Child components now receive the owner before their spawn callbacks run. The targeted tests pass. The editor runtime check is still pending.

These summary and pull request rules apply the current writing agreement. Historical source comments and commit subjects do not establish a complete personal style for agent conversations or pull requests.

## Historical Habits That Do Not Set Current Rules

The old files contain multiple types per file, direct backing-field writes, empty XML tags, commented-out code, long expressions, and some allocations. Some older accessors use `get { return ...; }`, while later examples use `=>`. Do not copy these details when they conflict with current rules or reduce clarity.

Use one type per matching filename, current namespace and API conventions, `SetField(...)` where required, and current allocation constraints. Do not restore old framework patterns or add regions, prefixes, or wrappers solely to resemble old code. This guide does not authorize unrelated formatting changes.

## Source References

The links below use fixed commits. Dates in this section use UTC. The main snapshot is [TheraEngine at `a49555c`, 2 January 2022](https://github.com/BlackJaxDev/TheraEngine/commit/a49555cd8848694c3465a8231d51d33046b34af3). File conclusions describe the sampled code; they are not a claim of exclusive authorship of every line.

| Source | Evidence |
| --- | --- |
| [Component.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Components/Component.cs) | Allman braces, an unbraced `Despawn` condition, expression accessors, and callback summaries. |
| [HealthComponent.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Components/Logic/Common/HealthComponent.cs) | `_camelCase`, an early return, short `if`/`else` bodies, and retained braces in a mixed-size branch. |
| [TObjectSlim.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Core/TObjectSlim.cs) | Short expression bodies and direct purpose summaries. |
| [TRS.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Components/Scene/Transforms/TRS.cs) | Explicit matrix types, compact overloads, unbraced conditions, and multi-operation accessor blocks. |
| [BaseActor.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Actors/BaseActor.cs) | Lifecycle naming, guards, event invocations, and the reason for an initialization restriction. Also shows historical allocation patterns that current rules restrict. |
| [PropAnimVec3.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Animation/Property/PropAnimVec3.cs) | Related overloads, units in parameter names, explicit types mixed with `var`, expression bodies, and nested branches. |
| [EditorSettings.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Editor/EditorSettings.cs) | Four-space indentation, `_camelCase`, auto-properties, attributes, defaults, and short conditional returns. |
| [RenderCommands.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Rendering/Scene/RenderCommands.cs) | Comments about render order and distance semantics, guards, and wrapped constructor parameters. |
| [EventList.cs](https://github.com/BlackJaxDev/TheraEngine/blob/a49555cd8848694c3465a8231d51d33046b34af3/Engine/Core/Data%20Structures/Lists/EventList.cs) | Event timing summaries and forwarding overloads. Some descriptions are stale, so use the style rather than copying the text. |
| [EventVec3.cs, 5 August 2017](https://github.com/BlackJaxDev/TheraEngine/blob/4b7c71151d67db53da4d59d196cdc99bc59c15a9/Engine/Core/Maths/Transforms/Vectors/EventVec3.cs) | Earlier Allman blocks, field names, unbraced guards, and both old accessor syntax and expression bodies. |
| [Octree.cs, 5 August 2017](https://github.com/BlackJaxDev/TheraEngine/blob/4b7c71151d67db53da4d59d196cdc99bc59c15a9/Engine/Core/Data%20Structures/Trees/Octree.cs) | Direct method names, expression-bodied forwarding, and a comment that states the consequence of a missed update. |
| [PostProcessSettings.cs, 5 August 2017](https://github.com/BlackJaxDev/TheraEngine/blob/4b7c71151d67db53da4d59d196cdc99bc59c15a9/Engine/Rendering/Cameras/PostProcessSettings.cs) | Earlier `_camelCase` fields, compact accessors, and attributes beside members. |

Commit-message counterexamples and useful traits:

- [6 May 2020: `VR fixes`](https://github.com/BlackJaxDev/TheraEngine/commit/c727e2d72108d40b6c5fea1c77ecc8e0de7b99d3) is brief but does not state the specific fix.
- [1 July 2019: `Added neural net graphing visualization`](https://github.com/BlackJaxDev/TheraEngine/commit/121cd2afda181e50d7a06d3770d93a125124c389) names a concrete result but uses past tense.
- [5 August 2017: the rendering and transform changes](https://github.com/BlackJaxDev/TheraEngine/commit/4b7c71151d67db53da4d59d196cdc99bc59c15a9) list several changes and state that shadows still do not work. Preserve the candor about limits; use a shorter subject and a body today.
- [2 January 2022: `no message`](https://github.com/BlackJaxDev/TheraEngine/commit/a49555cd8848694c3465a8231d51d33046b34af3) shows why historical messages cannot serve as the sole standard.

The commit sample is selected, not a frequency study. Initial date-window queries were checked against file history and selected commit details. Maintainer identity confirmation resolves older unlinked authors; imported library messages remain excluded. No percentage or universal rule is inferred from this sample.
