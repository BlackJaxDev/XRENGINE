# Unity Script Host

The Unity script host provides source inspection, assembly graph resolution,
and source compilation commands. It does not execute Unity scripts, create
native avatar assets, or authorize code execution.
Normal Unity imports keep their existing data-conversion behavior.

## Conversion Boundary

This tool converts authoring output into capabilities that XRENGINE already
supports. It does not authorize adding Unity runtime features to the engine.
Unity and SDK type names exist only at the import boundary so original scripts
can compile and, when supported, operate on temporary authoring data. These
types must not become runtime components, player dependencies, or executable
payloads in published assets.

Implement an import adapter only where it can produce supported native data.
Do not add a second scene system, Animator evaluator, PhysBone solver, Unity
player loop, or source-platform networking to close a conversion gap. A missing
engine capability remains a documented limitation and separate future work.
Compilation success does not establish that source behavior can be converted.

Conversion reporting must distinguish these cases:

| Case | Required record |
|---|---|
| Supported conversion | Source object/member, existing native target, and preserved behavior |
| Converter not implemented | Existing native capability and the missing import mapping; runtime support is not yet proven |
| Native capability absent | Behavior that cannot be represented, visible functionality lost, and the capability a separate engine feature would require |
| Authoring operation unavailable | Script/API and affected build step; the build is blocked, so final runtime loss can be unknown |

Record any approximation and its visible difference. Do not silently remove a
required feature or describe it as converted. Existing optional-loss policies
can retain a usable subset with explicit diagnostics; a failed scripted build
must not be published as a successful conversion. The script host reports
compilation blockers and observed unsupported authoring operations. The editor
records selected avatar conversion branches. Both reports remain partial;
complete per-feature coverage is not implemented yet.

Existing import examples include PhysBone data mapped to `PhysicsChainComponent`
and supported material properties mapped to native materials. Source-specific
physics interactions and authority behavior can be lost. Avatar animation layers
and expression networks currently remain import evidence rather than executable
avatar behavior. See the [conversion guide](unity-conversion-integrations.md#avatar-component-conversion)
for the implemented mappings and limits.

## Build And Run

Build the standalone tool from the repository root with the pinned .NET 10 SDK:

```powershell
& ./Tools/Invoke-AgentBuild.ps1 -DotnetArguments @('restore', 'XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj')
& ./Tools/Invoke-AgentBuild.ps1 -DotnetArguments @('build', 'XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj', '--no-restore')
& ./Tools/Invoke-AgentBuild.ps1 -DotnetArguments @('build', 'XREngine.UnityScriptHost.Compatibility/UnityEngine/XREngine.UnityScriptHost.Compatibility.UnityEngine.csproj')
& ./Tools/Invoke-AgentBuild.ps1 -DotnetArguments @('build', 'XREngine.UnityScriptHost.Compatibility/UnityEditor/XREngine.UnityScriptHost.Compatibility.UnityEditor.csproj')
```

Select a prepared Unity source project with an `Assets` directory. The project
can be an extracted engine import project or an original Unity project. The
tool does not extract archives or fetch missing packages. Create an output
directory outside the source project, then use a new report filename:

```powershell
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- inspect --project "<source-project>" --entry "Assets/Avatar/Main.unity" --root "Avatar" --output "<report-directory>/source-inventory.json"
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- verify --project "<source-project>" --inventory "<report-directory>/source-inventory.json"
```

`--entry` and `--root` are optional inspection inputs. Entry paths must name an
existing `.unity` or `.prefab` under `Assets`, without traversal. The root name
is recorded, not resolved against the scene hierarchy. `RootSelectionValidated`
therefore remains false. Use `--help` for the command syntax. The output
directory must already exist. Existing report files are never overwritten.

| Exit code | Meaning |
|---|---|
| `0` | The requested inspection, verification, graph, compilation, or metadata analysis completed. This does not establish script runtime support. |
| `1` | Invalid arguments, invalid metadata, a path/budget failure, or an I/O failure. |
| `2` | Verification found different source content or selection. |
| `3` | The assembly graph or source compilation failed. Read the report diagnostics. |
| `130` | The command was cancelled. |

## Pinned Local Package Source

`package` fingerprints one local package directory. It reads the manifest name
and version, records every file and directory, and computes a path-independent
content digest. It does not evaluate package code or load binaries.

```powershell
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- package --source "<package-source>" --output "<report-directory>/package.json"
```

`compose` copies a project and explicitly pinned local packages into a new
directory. Its JSON lock uses this shape:

```json
{
  "SchemaVersion": 1,
  "SourceContentSha256": "<source-inventory-sha256>",
  "EntryPath": "Assets/Avatar/Main.unity",
  "RootName": "Avatar",
  "Packages": [
    {
      "Name": "com.example.authoring",
      "Version": "1.2.3",
      "Path": "../package-source",
      "ContentSha256": "<package-content-sha256>"
    }
  ]
}
```

Package paths are relative to the lock file. Source selection must match the
source inventory digest. Package name, version, and content must match each pin.
The resolver rejects existing destination names and never replaces a source
package. It validates all pins before it creates output, checks each copied file,
then checks both original inputs and the complete output file set. The output
contains `source-inventory.json` and `composition.json` after successful copying.
Partial files can remain after failure; they do not have a success record.

```powershell
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- compose --project "<source-project>" --lock "<lock-directory>/sources.json" --output "<report-directory>/new-source-project"
```

This is explicit local source composition, not a package registry solver. It
does not fetch packages, choose newer versions, interpret dependency ranges,
or resolve Unity built-in modules. The next `graph` or `compile` command still
reports missing dependencies. Package binaries remain inventoried inputs.
An explicit `Imported` reference can admit a managed DLL as compiler metadata;
composition itself grants neither metadata admission nor code execution.
Composition uses the inventory entry, path, depth, and byte limits. Output must
be outside all inputs. Reparse points are rejected. The copy is not an immutable
asset snapshot or an execution trust grant.

## Assembly Graph And Compilation

The commands use a JSON `UnityCompilationProfile`. It selects the target,
symbols, source assembly roots, compiler SDK, framework reference pack, and
language version. The versions must already be installed. The tool does not
download dependencies. This example selects source that does not need Unity APIs:

```json
{
  "SchemaVersion": 1,
  "Name": "generic-authoring",
  "UnityVersion": "2022.3.22f1",
  "TargetPlatform": "Editor",
  "Defines": ["UNITY_EDITOR"],
  "RootAssemblies": ["Authoring.Tools"],
  "IncludeTestAssemblies": false,
  "SdkVersion": "10.0.401",
  "ReferencePackVersion": "10.0.14",
  "LanguageVersion": "9.0",
  "SourceReferenceAssemblies": [],
  "References": []
}
```

Supply all target symbols explicitly. Package version defines come from direct
embedded package manifests. Loose manifests do not activate version defines.
An empty root list selects all eligible assemblies. The Unity version in the
profile is a caller-selected compatibility target; it does not certify that
the source or references match an installed Unity Editor.

```powershell
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- graph --project "<source-project>" --profile "<profile-directory>/profile.json" --output "<report-directory>/assembly-graph.json"
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- compile --project "<source-project>" --profile "<profile-directory>/profile.json" --output "<report-directory>/new-compilation"
```

The graph resolves nearest asmdef/asmref ownership, GUID references, predefined
assemblies, Editor placement, platform filters, define constraints, and supported
version ranges. Empty or omitted version expressions require resource presence; bare
numeric versions set a minimum version. It reports missing dependencies and cycles.
For a recognized Unity release version of 2019.1 or later, an absent named
asmdef reference produces non-error diagnostic `UNITYHOST2019`. The compiler
omits that reference and still rejects source that uses missing types. An
unresolved GUID, a known assembly excluded for the target, and a required
precompiled reference remain errors. Older or unrecognized Unity versions keep
strict missing-reference errors. This follows Unity's
[optional dependency rule](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-semver.html).
It does not create aliases or select an unpinned package version.
Directories with names that start with `.` or end with `~` stay in the inventory
but do not enter compilation. Unknown assembly options fail instead of silently
changing source selection. Legacy `optionalUnityReferences: ["TestAssemblies"]`
marks a test assembly. `IncludeTestAssemblies` defaults to false. When enabled,
these nodes require NUnit and Unity TestRunner metadata. Predefined assemblies
do not automatically reference them. Other legacy reference values and Unity
alpha/beta/patch release comparisons remain unsupported. Final Unity release
versions can use the `fN` suffix. See Unity's
[assembly definition format](https://docs.unity3d.com/es/2020.2/Manual/AssemblyDefinitionFileFormat.html).

References are metadata inputs, not executable plugins. Each `References` entry
has `Path`, `Sha256`, `AssemblyName`, and `Role`. Paths resolve relative to the
profile directory, except for `Imported` paths described below. Roles are
`Compatibility`, `Platform`, `UnityEngine`, `UnityEditor`, or `Imported`.
Engine/editor names and their dotted module names require the matching role.
`noEngineReferences` and `overrideReferences` control automatic graph edges.
Explicit `Platform` references remain available with either option enabled.
Use this role for approved platform metadata, such as annotation attributes,
that source can use without a precompiled-reference declaration. It does not
fetch dependencies or bypass reference checks.
The tool verifies the reference hash and PE assembly name. It rejects project
files with a host compatibility role, duplicate identities, and source/reference
identity collisions. Package DLLs are never loaded or executed by these compiler
commands. Shared Unity API modules are described below. SDK types come from
user-imported source or explicitly selected DLL metadata. Missing references
and members produce explicit diagnostics.

### Imported DLL Metadata

The `Imported` role selects a user-provided managed DLL for compiler metadata:

```json
{
  "Path": "Packages/com.example.authoring/Plugins/Example.Build.dll",
  "Sha256": "<exact-file-sha256>",
  "AssemblyName": "Example.Build",
  "Role": "Imported"
}
```

This path is relative to the inspected project, uses forward slashes, and must
name an inventoried `.dll` under `Assets` or `Packages`. The inventory length
and hash, selected hash, and PE assembly name must match the captured bytes.
Absolute paths, traversal, reparse points, and missing inventory entries fail.
The capture reads PE metadata without loading the assembly or running module
initializers. Native DLLs, managed executables, netmodules, and mixed-mode images
fail. Framework and source identity conflicts still fail. Imported DLLs cannot
replace the host, Contracts, shared failure-state assembly, or reserved Unity
assembly identities.

`Imported` follows ordinary precompiled-reference selection. It is available
automatically when `overrideReferences` is false. When that flag is true, the
consumer must declare the reference. It does not behave as a `Platform` reference.
The explicit profile selects the DLLs; Unity PluginImporter platform selection
is not implemented. An imported DLL is copied only into the local compiler
capture, not an engine project or a published native asset.

`ImportedAssemblies` in the result records source path, hash, size, assembly
and dependency identities, bitness requirements, module references, and counts
of P/Invoke, internal calls, and native method bodies. Metadata-only inspection
accepts an IL-only assembly with P/Invoke declarations and reports them; this
does not permit those calls to execute. Each image has a 1 MiB decoded-text
limit. The combined report has an 8 MiB text limit and 32,768-entry limit.
Public key tokens identify metadata; no signature trust is implied.

This removes the requirement to have source for a compiler reference. It does
not implement assembly loading, a trusted execution manifest, or SDK build
callbacks. A future worker must separately admit the complete runtime dependency
closure and exact entry point. Reference-pack DLLs are not execution images.

### Build Required References From Imported Source

`SourceReferenceAssemblies` explicitly permits imported source to supply a
required DLL reference. For example, add `"Example.Provider"` to this array
when the imported packages contain an asmdef named `Example.Provider` and its
complete C# source. A consumer with `overrideReferences: true` and
`precompiledReferences: ["Example.Provider.dll"]` then gets a normal source
dependency edge. The provider compiles first, and the consumer references that
compiler output. Original package files are unchanged. This is an explicit host
source-build option, not a claim about Unity's precompiled-reference behavior.

Names and DLL filenames must match exactly, including case. The selection
does not rename assemblies, map types, generate contracts, or infer aliases.
Missing, excluded, and empty providers fail with `UNITYHOST2020`; mismatched
filenames fail with `UNITYHOST2022`. Duplicate selections, dependency cycles,
and conflicts with explicit or framework references also fail. No package DLL
is a fallback. An empty selection preserves the normal metadata requirement.
All configured providers must be available even if a selected root does not use
them. A valid unused selection does not add a root or compile unrelated source.

The provider uses the same inventory, source hashing, compilation order, and
artifact checks as every other source assembly. `UNITYHOST2021` records each
selected source edge. The graph records the full profile digest as
`ProfileSha256`; compilation rejects a graph built with different profile
settings even when its profile name is unchanged. This freshness check is not
an execution grant or an external graph-admission boundary.
Matching an assembly name does not establish API or runtime equivalence. The
compiler must still accept every required source member, and execution remains
subject to the supported authoring APIs and conversion limits.

Missing-reference diagnostics list at most three inventoried asmdef or DLL
filename candidates and report omitted candidates. A filename candidate does
not prove a binary's assembly identity. Candidate binaries are not loaded or
admitted implicitly. Select an exact imported metadata reference or provide
complete source and its dependencies.

Compilation creates a new directory outside the source project. It captures
selected C# files after checking their inventory hashes. It copies explicit
references and the selected .NET 10 reference pack into that directory. It uses
the installed SDK's `csc.dll` with host-written response files, `/noconfig`, and
explicit references. It does not evaluate package projects, targets, response
files, analyzers, generators, or editor configuration. The child environment is
filtered. Each compiler process has a two-minute timeout and is terminated on
cancellation. This compiler process is not a script-execution sandbox.

The output has `graph.json`, `result.json`, `conversion-coverage.json`, source/reference captures, response
files, compiler logs, and compiled assemblies. `ScriptsExecuted` is always false.
Results include input and artifact hashes. Compiler diagnostics name captured
source paths. Failed compilations return no accepted artifacts; partial files
can remain in the diagnostic output directory. Assembly signing is unsupported;
outputs with public keys are rejected. No compiler cache is enabled.

Profiles are limited to 1 MiB. Compilation captures at most 512 MiB of inputs,
with 128 MiB per source and 64 MiB per reference or output assembly. Each compiler
output stream retains at most 1,048,576 characters. Reports remain bounded to
64 MiB. Installed compiler files, selected references, symbols, source hashes,
runtime version, and normalized response files contribute to the input digest.
The host captures source bytes for compilation, but it does not provide the
immutable asset snapshot or trust admission needed for script execution.

### Source Diagnostics With Missing Dependencies

`diagnose` has the same arguments as `compile`. It captures the same verified
inputs, but asks the compiler for source diagnostics with available references
even when dependencies are missing. It continues after a failed source assembly
so that other selected assemblies can report their own errors. Logs can contain
errors caused by missing upstream assemblies; they are not a complete API usage
analysis.

Diagnostic mode always returns exit `3`, `DiagnosticsOnly: true`,
`Succeeded: false`, and an empty accepted artifact list. The output directory
can contain temporary compiler outputs. They are evidence only. Normal `compile`
still stops on unresolved graph or reference requirements. Neither mode runs
package code.

## Conversion Coverage Records

`UnityConversionCoverageReport` is shared data in the Contracts project. It has
its own schema version, an assessment stage, feature records, and suppression
indicators. Current producers always set `IsComplete` to false. An empty report
does not establish native support. Compile results and worker/import results
carry separate reports; the compiler also writes `conversion-coverage.json`.

Each feature records its identifier, available assembly/path/component/member
context, affected function, status, reason, native target if known, observed
loss, and any absent native capability. The statuses are `Converted`,
`MissingConverter`, `MissingNativeCapability`, `AuthoringBlocked`, and `Unknown`.
`Converted` describes a mapping observed at an importer branch, not verified
runtime parity or final publication. Related loss records must also be read.
`Unknown` can include an observed loss whose cause has not been classified.

Compilation errors and caught unsupported authoring calls produce
`AuthoringBlocked` records. They cannot claim native mappings or runtime loss.
The shared builder rejects such claims and requires native targets or missing
capabilities for the corresponding native statuses. It retains at most 4,096
records and 2,048 characters per text field, and reports suppressed records or
shortened text. These checks do not replace future worker-result validation.

The editor stores partial coverage in its existing import manifest sidecar.
Collider, physics-chain, constraint, and presentation branches record their
native mappings. Other records identify source collision-filter loss, unmapped
chain/constraint semantics, animation-layer conversion gaps, non-executable
expression data, and unknown scripts. Component GUID/local-file-ID context
keeps distinct source components separate. Other import paths and final-output
reconciliation still need coverage. Older manifests without this additive field
default to empty `NotAssessed` evidence; their existing format remains readable.
The editor references Contracts only, not the host or Unity/SDK API assemblies.

## Static API Usage

`api-usage` reads a pinned managed PE with `PEReader` and `MetadataReader`. It
does not load that assembly or its dependencies. It reports distinct external
method and field `MemberRef` entries, including constructors and attributes.
It is not source analysis, a call graph, an execution trace, or native conversion
assessment. Reflection, dynamic calls, type-only references, and constructed
generic method arguments outside MemberRef signatures remain outside its scope.

```powershell
dotnet run --no-build --project XREngine.UnityScriptHost/XREngine.UnityScriptHost.csproj -- api-usage --assembly "<compilation>/assemblies/Authoring.Tools.dll" --sha256 "<assembly-sha256>" --output "<report-directory>/api-usage.json" --catalog "<profile-directory>/api-catalog.json"
```

The optional catalog is a JSON object that maps exact `CatalogKey` strings from
the report to `Implemented`, `CompileOnly`, or `Unsupported`. Missing entries
report `Unsupported` with `CatalogEntryPresent: false`. This means no declaration
was supplied; it does not establish a missing engine feature. An explicit catalog
entry records a caller declaration, not independently verified behavior. Unresolved
declaring types or signature types cannot receive implemented status. Catalog
data does not authorize execution or publication.

Key version 1 includes the simple assembly name, declaring type, member name,
method calling convention/generic arity/parameter count, parameter and return
types, or field type. Array shapes, nested/generic types, modifiers, and function
pointers retain signature distinctions. Raw name tokens escape punctuation as
`%HHHH` UTF-16 values; numeric formatting is invariant. Copy keys from the report.
Assembly and catalog SHA-256 digests scope the evidence; simple assembly names
alone do not distinguish API versions. These analysis keys are separate from the
current unsupported-operation diagnostic strings.

Inputs are limited to 64 MiB per assembly and 1 MiB per catalog. Catalogs allow
4,096 unique keys of at most 4,096 characters. Analysis permits 100,000 MemberRef
rows, 16,384 distinct external members, and an 8,388,608-character output budget.
Each member and its nested type specifications share a 256-byte encoded signature
budget. Signature counts and depth are checked before decoding. Oversized or
unsupported metadata fails explicitly. Type-only and source usage analysis, a
qualified complete capability catalog, and worker admission remain separate work.

## Shared Compatibility Modules

`XREngine.UnityScriptHost.Compatibility/Runtime` owns the capability catalog,
failure state, and `UnityCompatibilityRuntime`. The host and Unity API modules
share this library. `BeginSession` permits one active authoring operation per
worker process. Unsupported calls record failure in that process-wide state
before throwing, including calls on threads without a flowing execution context.
Catching the exception or disposing the session does not clear the failure.
The future worker must join background work before it closes the session and
creates its terminal result. A session is not a sandbox or an execution grant.

Shared declarations use the module identities that imported binaries reference.
The aggregate assemblies contain type forwarders, not duplicate declarations:

| Assembly | Ownership | Profile role |
|---|---|---|
| `UnityEngine.CoreModule` | Object, component, transform, value, and core asset declarations | `UnityEngine` |
| `UnityEngine.AnimationModule` | Animation clip, animator, controller, motion, and humanoid bone declarations | `UnityEngine` |
| `UnityEngine.InputLegacyModule` | Guarded legacy input declarations | `UnityEngine` |
| `UnityEditor.CoreModule` | Editor metadata and guarded authoring declarations | `UnityEditor` |
| `UnityEngine` | Forwarders to the three engine-facing modules | `UnityEngine` |
| `UnityEditor` | Forwarders to the editor-facing module | `UnityEditor` |

These assemblies use version `0.0.0.0`, neutral culture, and no public key,
matching the inspected Unity 2022.3 module identities. Each type has one owner.
Modules depend on the shared failure state and required lower-level modules;
they do not depend on the aggregate forwarders. This preserves type identity
when one consumer references an aggregate and another references a module.

Add each required aggregate and module output to the profile with its explicit
path, current SHA-256, assembly name, and role. Referencing an aggregate does not
implicitly admit its dependencies. Rebuild consumers and refresh pins after a
declaration moves or changes. The normal `noEngineReferences` rules still apply.
The host does not load original Unity engine DLLs as execution substitutes.
This is a partial API surface, not complete binary compatibility.

The current surface includes:

- Serialization and inspector attributes that store metadata.
- Vector, quaternion, color, and column-major matrix field layouts, constructors,
  constants, and a small set of scalar/vector value operations.
- Compile-facing object, component, hierarchy, scene, renderer, and asset types.
- Editor initialization/menu attributes and compile-facing asset/lifecycle APIs.
- Selected build-target identifiers and editor pause/play-state enum values.
  These values do not add build backends or schedule lifecycle events.
- Runtime initialization and preservation attributes that store metadata only.
  They do not schedule methods or implement code stripping.
- Camera, display, render-texture, input, and world/local point declarations.
  Live operations record unsupported failures. No camera, input loop, render
  target, or transform service is created by these declarations.
- Vector negation and dot products implemented with scalar arithmetic. `Ray`
  retains two-vector value storage, origin access, and point arithmetic.
  Its constructor, direction changes, and string formatting remain guarded;
  source-compatible normalization and formatting are not qualified yet.

The object declarations include visibility/persistence flags, immediate deletion
with an asset flag, hierarchy root/ancestry/world-scale access, and skin bone
references. These member names follow the Unity 2022.3 public API, including
[Object.DestroyImmediate](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Object.DestroyImmediate.html),
[Transform](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Transform.html),
and [SkinnedMeshRenderer](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SkinnedMeshRenderer.html).

Object construction and operations that need an object store, asset workspace,
serialization engine, or scheduler record unsupported failures and throw.
They do not return empty objects or report successful writes. Object cloning,
Unity object identity, transforms, asset editing, lifecycle callbacks, and
controller execution remain unimplemented. The modules permit generic source
compilation; they do not establish VRCFury or arbitrary Unity script compatibility.

`XREngine.UnityScriptHost.Compatibility/Annotations` emits `JetBrains.Annotations` with the metadata-only
`CanBeNullAttribute` declaration. Add this output as an explicit `Platform`
reference when required. It does not implement annotation analysis.

`XREngine.UnityScriptHost.Compatibility/MethodPatching` emits the `0Harmony`
identity for explicit, hashed compiler references. Its declarations cover patch
requests and instruction metadata. Every declared constructor, patch operation,
and instruction query records an unsupported failure and throws. The module
does not install detours, change methods, or return fake instruction data.
Source that requires method patching remains blocked during authoring. This
block does not by itself identify a native avatar feature loss.

### Imported SDKs And Native Build Hooks

Users supply SDK source or managed DLLs through the same explicit import profile
as other authoring packages. The engine must not include
handwritten VRChat SDK types, SDK facade projects, or SDK project references.
No SDK-name condition selects graph rules or compiler behavior.

The host can translate shared Unity API operations into existing engine
capabilities. Package source owns its types, callbacks, ordering, and algorithms.
Generic explicit entry profiles can identify imported methods; they must not
encode SDK behavior. Do not synthesize SDK contracts to hide missing input.
Package binary execution is not implemented yet. No build or native compatibility
claim can rely on removed SDK declarations.

The intended hook boundary uses generic engine build stages. A pinned profile
identifies callback contracts from the imported assemblies and binds them to
those stages inside the worker. Imported code supplies SDK types and callback
implementations. The host supplies supported Unity-facing authoring operations
and exports data for existing native converters. The engine and player need no
SDK assembly or handwritten SDK interface. Generic callback invocation does not
by itself implement object storage, callback ordering, or API semantics.

The final native avatar should use engine-owned components and contain no SDK
code. Build-time SDK operations can still be required by an authoring package.
They must execute correctly or report an unsupported operation. They cannot
be replaced with successful no-ops because a similar native runtime feature
exists. Existing physics-chain and constraint mappings do not establish full
avatar animation, expression, interaction, or network compatibility.

## Report Contents And Limits

The tool reads `Assets`, `Packages`, and `ProjectSettings` when present. It does
not scan `Library`, `Temp`, or generated Unity build output. Registry dependencies
that exist only in Unity's package cache are not resolved. The report records:

- Sorted relative file and directory paths, file lengths, and SHA-256 values.
- A path-independent source digest that also includes entry/root selection.
- Source script counts, assembly definition/reference metadata, platform and
  define constraints, and declared precompiled references.
- Embedded package manifests and recognized loose package manifests, including
  dependency constraints. `IsEmbeddedPackage` distinguishes direct
  `Packages/<package>/package.json` manifests from loose content.
- The source Unity editor version when `ProjectSettings/ProjectVersion.txt`
  exists. An empty value means the version is unknown.
- Executable candidates found by extension or PE, ELF, and Mach-O signatures.
  Package assemblies are not loaded, even when renamed to an asset extension.

Every inventory report has `ScriptExecutionAvailable: false` and
`AssemblyGraphResolved: false`. Binary and precompiled-reference diagnostics
describe candidates across all inspected assemblies. Platform and symbol
selection in the separate graph command determines the selected dependencies. Loose
`package.json` data that cannot be interpreted stays fingerprinted with a
diagnostic. Invalid embedded package or active assembly metadata fails inspection.
Assembly metadata under ignored compilation directories stays hashed without
being parsed. Importing those files into an active source directory makes their
metadata subject to validation.

The limits are 100,000 entries, directory depth 64, 2 GiB per file, and 32 GiB
of source file bytes. Parsed metadata is limited to 4 MiB per file, 48 MiB for
required metadata, and a separate 16 MiB for optional loose manifests. Directory
traversal uses ordinal path order, so optional metadata selection is repeatable.
Metadata strings and relative paths are limited to 2,048 characters;
metadata arrays/maps have at most 4,096 items. The report retains at most 4,096
diagnostics and counts suppressed diagnostics. Report output is limited to
64 MiB and is published from a temporary file only after serialization succeeds.

Source and output paths cannot cross reparse points. On Windows, report
containment uses directory handles to resolve volume/path identity, including
extended and short-name aliases. Reports must remain outside the entire source
project. The tool checks each file for changes while reading it, but it does
not lock the whole project or create an immutable snapshot. Do not edit source
during inspection. A matching `verify` result is not a trust grant or proof
that a later build will read the same bytes.

## Code Ownership

`XREngine.UnityScriptHost.Contracts` contains only data contracts and .NET
framework types. It separates worker compilation/execution/export from the
parent's native conversion/publication outcome. Default result states are
`NotRun`. Request limits and graph/blob descriptors are data declarations;
future IPC and publication code must validate them before use.

`XREngine.UnityScriptHost` owns inspection, package source composition, graph resolution, and compilation. It has
no engine runtime, Unity, SDK, or third-party package reference. Windows path
identity uses operating-system APIs. No package compiler, generator, project
target, or install hook runs during inspection.

The shared runtime's `UnityCompatibilityCatalog` classifies exact declared member identifiers.
Unknown members default to `Unsupported`. It is not a static API usage analyzer
and has no built-in Unity API implementation. `RequireImplemented` records an
unsupported operation before it throws. `UnityBuildExecutionState` retains this
failure if the caller catches the exception. Its result helper clears output
on failure and requires a graph for success. The scheduler must stop work
before creating a final result; the helper is not a scheduler or a sandbox.

The host executable and shared API modules remain standalone tooling. The
editor references only the data/reporting Contracts library. Runtime and player
projects do not reference these projects. The
[downloadable content execution policy](../../architecture/runtime/downloadable-content-execution-policy.md)
still applies. Script worker supervision, immutable snapshots, trust admission,
executable object/asset compatibility, SDK callbacks, graph export, and conversion
of that processed graph into native assets remain unimplemented.

## Related Documentation

- [Unity Conversion](unity-conversion-integrations.md)
- [Runtime Project Organization](../../architecture/runtime/project-organization.md)
- [Agent Build Workflow](../ai/agent-editor-workflows.md#build-run-debug)
