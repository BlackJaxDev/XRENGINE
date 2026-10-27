# Hot-path analyzer

`XREngine.Diagnostics.Analyzers` checks a restricted C# subset in methods marked
with `Engine.Diagnostics.HotPathAttribute`. Unknown behavior produces an error.
See the [project setup](../../../XREngine.Diagnostics.Analyzers/README.md).

## Contract and scope

`[HotPath]` is a checked source contract. Every call must reach a statically
known implementation with a checked source body, an explicit trusted boundary,
or one of the small set of framework intrinsics listed below. A helper does
not become safe merely because its caller has the marker. Add `[HotPath]` to
the helper so that its body and its outgoing calls are checked too. Recursive
call groups use the same rule. The analyzer does not recursively re-walk a
callee at each call site.

The supported guarantee is a checked normal-execution source subset, subject
to trusted boundaries and valid inputs. It is not a proof about all CLR, JIT,
native, or exceptional-path behavior. For example, an invalid array index can
throw an exception even when the source has no explicit object creation.
This analyzer is not a complete Rust ownership system for arbitrary C#.

The analyzer checks method and expression bodies, constructors, operators,
accessors, and marked local or anonymous functions. A marked outer function
also covers its nested function bodies. Ancestor traversal passes through
blocks. A nested marker does not duplicate the outer analysis.

Generated bodies are excluded from analysis and cannot serve as checked call
contracts. Metadata-only `[HotPath]` annotations are not trusted. The analyzer
resolves marker and framework symbols once per compilation. Mutable state is
local to an analysis. Concurrent caches contain only compilation-owned
symbols. Analysis checks cancellation and does not use nested LINQ queries.

## ENG0001: direct allocation

The analyzer rejects reference-type construction, anonymous objects, arrays,
expanded array `params` arguments, boxing, expression trees, nonconstant string
construction, reference-record clones, async functions, and iterators.
It also rejects LINQ calls as a policy rule.

All delegate creation is rejected. This includes noncapturing lambdas and
static method groups because their caches can allocate on first use.
Capturing functions also receive a capture diagnostic. Existing delegate
values cannot be invoked directly under the checked contract; their runtime
call target is unknown.

Struct construction is permitted only when its constructor behavior is also
checked. A generic value-type constraint does not verify a constructor body.
Constant strings and primitive value operations remain permitted.

## ENG0003: unverified calls and lowering

A call must meet one of these conditions:

- Its exact source implementation has `[HotPath]`, a body, and no generated-code
  exclusion. Calls to local functions additionally require `static` so that
  captured outer storage cannot bypass borrow checks.
- Its exact target has `[HotPathTrusted("review reason")]`. The reason must not
  be empty. The trusted body is outside automatic verification.
- It is an exact framework intrinsic: the parameterless `object` constructor,
  `System.Array.Length`, string length or character indexing, or `Span<T>` / `ReadOnlySpan<T>` length,
  emptiness, or element indexing. No namespace-wide allowlist exists.

Array indexed access uses compiler operations. Roslyn can represent array
length as a property read, so its exact framework getter is accepted explicitly.
The [CoreCLR implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/System.Private.CoreLib/src/System/Array.CoreCLR.cs)
reads the stored array length. This does not permit `Array.GetValue`, array
cloning, or arbitrary properties named `Length`. String and span
intrinsics do not authorize `Slice`, storage conversion, or arbitrary members
of those types. Ref-returning element access is permitted for direct value
access. Unsupported ref aliases to those elements are rejected separately.

The call checks include constructors, property getters and setters,
user-defined operators, and conversion methods. Open virtual, interface,
static-abstract, dynamic, delegate, and function-pointer dispatch are rejected
when the exact implementation is not established. A checked base method does
not establish a contract for all overrides. A concrete sealed target or a
direct base call can use its checked body.

Unsupported hidden operations are rejected. These include implicit disposal,
non-array/string `foreach`, event subscription, locks, collection expressions,
interpolated-string handlers, range/index lowering, recursive and list
patterns, deconstruction, tuple equality, nonidentity tuple conversions,
nonidentity null-coalescing conversions, and user-defined short-circuit
operators. Declaration patterns and type tests that can box are also rejected.
Array/string iteration still checks element conversions. A tuple conversion
or deconstruction in a loop is not exempt from these restrictions.

Checked entry points and checked accessed types must not have unchecked static
initialization. Source static constructors can have `[HotPath]`. Static field
and property initializers are rejected because they are outside the checked
method body. Marked instance constructors also reject instance initializers.
Move required initialization into a checked constructor body, or review the
whole initialization path at a trusted boundary. Merely warming up a type does
not change the compiler rule.

## Trusted boundaries

The generator emits `HotPathTrustedAttribute` with the marker. Use a small
wrapper for external or native behavior that the analyzer cannot inspect:

```csharp
[Engine.Diagnostics.HotPathTrusted(
    "Reviewed native scalar call; no managed allocation or retained references")]
private static int ReadCounter() => NativeCounter.Read();
```

The attribute is a manual assertion, not evidence produced by the analyzer.
The review must cover initialization, marshalling, callbacks, allocation,
retention, and applicable input preconditions. Keep a useful review reference
in the reason. A trusted wrapper can be wrong; such a wrapper invalidates the
caller guarantee. Do not put `[HotPath]` and `[HotPathTrusted]` on the same
method to avoid checking its body. `[HotPath]` still requires source checking.

Trusted calls must still have static binding. Ref/ref-like returns and
ref-like `ref`/`out` parameters remain unsupported, including on trusted calls.
Nondefault ref-like constructors are also rejected because they can create
new storage aliases.
They require alias summaries that the analyzer does not yet provide.

## ENG0002: control-flow borrowing

A `ref` or `out` argument reserves its local or parameter after that argument
is evaluated. Earlier argument reads are permitted. Later reads, writes,
and mutable arguments for the same storage produce errors while the
reservation is active. `in` arguments do not start mutable reservations.

The analysis uses Roslyn control-flow graphs. It merges possible reservations
at branch joins and reaches a fixed point across loop back-edges. It uses
lexical block boundaries for reservation lifetime, not basic-block boundaries.
A reservation created in an outer block remains active in child blocks.
Put a borrow in a child block when the value must be used afterward:

```csharp
int value = 0;
{
    UpdateValue(ref value);
}
Consume(value);
```

This is stricter than ordinary C# call semantics. A C# `ref` call normally ends
when the call returns; this policy reserves storage until the specified block
ends. Ref-local aliases to supported local or parameter storage resolve to
that storage. Borrowing a field of a local value type reserves the whole value.

Unsupported alias forms produce errors. These include heap/array element
borrows, ambiguous or conditional ref aliases, ref reassignment, and copies of ref-like views into locals or fields. These restrictions prevent the analyzer from silently
losing storage identity. Exception-flow constructs are rejected until their
borrow transitions are supported. Nested functions have separate analysis;
capturing local-function calls cannot bypass an outer reservation.

The rule does not establish exclusive ownership of arbitrary pre-existing
heap objects or prove that caller-supplied spans are disjoint. Checked methods
must receive valid storage. Callers outside checked code must also preserve
the required non-aliasing of mutable inputs. Unsafe/native ownership belongs
at explicit reviewed boundaries.

## Escape boundaries

Raw pointer and function-pointer operations are rejected in checked methods.
Put unsafe implementation in a reviewed boundary instead of relying on a
partial pointer-lifetime proof. Ref and ref-like returns are rejected until
return alias summaries exist. Ref-like `ref`/`out` parameters are also rejected
because they can export a new view.

The stack-provenance walker still detects local `stackalloc` and address escapes
through local assignment dependencies, returns, and stores. It propagates
possible provenance with a work queue and does not clear prior provenance on
assignment. Fields of local value types carry provenance into their containing
local. This conservative diagnostic supplements the stricter boundaries and
C# compiler ref-safety checks; it is not a complete escape analysis.

## Build enforcement and validation

`Directory.Build.targets` references the analyzer as compiler tooling in every
`XREngine.Runtime.*` project. It does not add a runtime assembly dependency.
The reference removes runtime, trimming, and AOT properties from the analyzer
build. The browser publisher includes the analyzer sources and project in its
source bundle.

Both generated marker types use `Microsoft.CodeAnalysis.EmbeddedAttribute`.
This prevents duplicate-type warnings across `InternalsVisibleTo` references.
The generator emits the embedded attribute definition with the Roslyn 4.14
helper, following the [Roslyn marker attribute guidance](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md#put-microsoftcodeanalysisembeddedattribute-on-generated-marker-types).

The engine coverage uses the strict contract:

| Project and method | Checked work |
| --- | --- |
| `XREngine.Runtime.Core`: `PhysicsChainScalarReferenceKernel.Publish` | Copies particle state into preallocated outputs after scalar and AVX2 simulation steps |
| `XREngine.Runtime.Rendering`: `AdvancedIndexedInstanceGroupPlanner.Hash` overloads and `HashWord` | Hashes retained geometry, material words, and bytes for indexed instance groups |
| `XREngine.Runtime.Core`: `PhysicsChainSimulationClock.SetCadenceProgress` and `ResolveLoop` | Updates cadence progress and counts due simulation steps |
| `XREngine.Runtime.Core`: `PhysicsChainActivityEvaluation.IsQuiet` | Checks normalized activity against the sleep threshold |
| `XREngine.Runtime.Core`: `PhysicsChainCpuBackend.PublishInitialOutputs` and `NextGeneration` | Copies initial particle output and advances the output generation |
| `XREngine.Runtime.Rendering`: `AdvancedDeformationRetirement.Drain`, `FindEmptySlot`, and `MaximumCompletionValue` | Releases completed storage, finds a free slot, and scans frame completion values |

The physics output span must have at least one slot per state. Input and output
storage must not overlap. Hash storage must not overlap the byte input. These
are caller preconditions, not proofs made by the analyzer.

The cadence loop requires a positive finite rate and a positive, bounded
catch-up limit. The retirement scan receives equal-length
storage and completion arrays. Its caller owns those arrays and the generation
count during the scan. Retirement scans use a stateless type so the checked
bodies do not depend on the arena's unchecked static initialization. Allocation
and resource publication remain the arena's responsibility outside these scans.

The 64-bit hash appends its low and high words through a value-returning helper.
This preserves word order and arithmetic while avoiding two mutable borrows
of the same hash in one block. No trusted wrappers or diagnostic suppressions
are used for these kernels.

The outer render, fixed-update, and native physics dispatch loops remain outside
the checked subset. They use callbacks, virtual backends, native pointers,
synchronization, and profiler scopes that require separate contract work.
An analyzer reference does not certify those unmarked bodies or their callees.

All three diagnostics have default severity `Error`. Standard Roslyn
configuration and suppression remain available. Builds must run analyzers,
and policy owners must control suppression if these checks are mandatory.

The isolated compiler tests are in `XREngine.UnitTests/HotPathAnalyzers/`.
They exercise generator output, compiler-host loading, allowed and rejected
calls, hidden conversions, generated and metadata-only targets, concurrent
analysis, borrow scopes, control-flow cases, and escape diagnostics.

| File | Responsibility |
| --- | --- |
| `HotPathAttribute.cs` | Checked source marker |
| `HotPathTrustedAttribute.cs` | Explicit manual trust boundary |
| `HotPathAttributeGenerator.cs` | Emits both attributes |
| `HotPathAnalyzer.cs` | Registration, scope, and descriptors |
| `HotPathSymbols.cs`, `HotPathContracts.cs` | Compilation symbols and call contracts |
| `HotPathOperationWalker.cs` | Direct allocation operations |
| `HotPathContractWalker.cs` | Call targets, initialization, and unsupported lowering |
| `HotPathBorrowWalker.cs` | Graph selection and nested-function traversal |
| `HotPathBorrowAnalysis.cs` | Fixed-point branch/loop merge and lexical lifetimes |
| `HotPathBorrowTransfer.cs`, `HotPathBorrowLoan.cs` | Evaluation order and reservation identity |
| `HotPathEscapeWalker.cs` | Conservative stack provenance |

Roslyn references: [method symbols](https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.imethodsymbol)
and [control-flow graph implementation](https://source.dot.net/Microsoft.CodeAnalysis/Operations/ControlFlowGraph.cs.html).
