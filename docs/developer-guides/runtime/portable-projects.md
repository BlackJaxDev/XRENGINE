# Portable Project Rules

[Project organization](../../architecture/runtime/project-organization.md) · [Developer guides](../README.md)

Portable projects target `net10.0` and compile their normal, complete source set. Desktop and browser consumers reference the same project identities. There is no alternate source-subset profile, portable assembly variant, or `XREnginePortableRuntime` build switch.

## Policy files

| File | Responsibility |
| --- | --- |
| [PortableProjects.tsv](../../../Build/Portable/PortableProjects.tsv) | Names the complete project closure admitted to browser compilation. |
| [PortablePackages.tsv](../../../Build/Portable/PortablePackages.tsv) | Allows reviewed direct package names and exact versions. |
| [SourceApiPolicy.tsv](../../../Build/Portable/SourceApiPolicy.tsv) | Records source API restrictions and narrow file/symbol allowances. |
| [PortableNativeAssets.tsv](../../../Build/Portable/PortableNativeAssets.tsv) | Admits exact native archive paths per project/runtime without disabling native checks. |
| [PortableRuntime.targets](../../../Build/Portable/PortableRuntime.targets) | Enforces evaluated project, package, compile-item, and native-asset boundaries. |
| [PortableSourceApiGuard.cs](../../../Build/Portable/PortableSourceApiGuard.cs) | Implements the SDK-hosted lexical source check. |

The guard rejects references outside the portable closure, unreviewed direct packages, native file references and runtime assets, and project-level source filtering. The browser host can use the SDK's own `browser-wasm` runtime pack. SDK-injected `Microsoft.NET.ILLink.Tasks` and `Microsoft.DotNet.ILCompiler` build packages are not engine dependencies; this exception requires `IsImplicitlyDefined=true`, and explicitly authored references still require review. `Directory.Build.props` defaults portable `browser-wasm` builds to `PublishTrimmed=false`, because the SDK otherwise defaults browser builds to trimmed publishing, which the guard rejects. The exact generated Rendering command registry, Host factory registry, and Browser manifest registry are admitted at their declared intermediate-output paths and scanned too; this does not allow arbitrary generated source substitution.

Native/vendor APIs, sockets, dynamic assembly loading, dynamic IL generation, and unmanaged calls belong behind services supplied by other projects. Deny rules match API use rather than bare names, because a deny match cannot be allowlisted. For example, `OpenVR.NET`/`Valve.VR` is denied but an engine enum member named `OpenVR` is not; the Win32 registry roots are denied but an engine property named `Registry` is not. Managed `System.Drawing.Primitives` values and the reviewed `Silk.NET.Maths` package remain allowed. Windows drawing, Silk window/input/context ownership, filesystem watching/mapping, process execution, and native hardware discovery belong to desktop modules.

## Reflection and registration

The approved browser Jolt supply is a narrow exception: the Jolt leaf and the
browser host's transitive project closure may reference only the repository's
hash-verified `JoltPhysicsSharp.Browser.csproj`. The browser host may link only
its staged `joltc.a` and the pinned archive directory's `libJolt.a`; other native
items are rejected. Source, managed patch, compiler pins and notices are checked
before linking. Desktop Jolt retains its NuGet package. Set
`-p:XREngineJoltBrowser=true` globally for browser restore/build/publish so NuGet
and compilation select the same reviewed source graph. See the
[native supply record](../../work/design/platform/jolt-browser-native-supply.md).

Allowances identify source files and symbols, with a reviewed purpose. They distinguish ordinary methods such as `JsonElement.GetProperty` from type reflection. The check is lexical and cannot prove runtime reachability or metadata preservation.

Published NativeAOT runtimes resolve types from cooked metadata or explicit generated registries. Built-in render commands register through a Rendering-local module initializer. Development builds retain authoring discovery over loaded assemblies; Desktop installs optional output-directory assembly loading. Missing published types, properties, transforms, or services must report a named diagnostic.

The [semantic factory generator](generated-runtime-factories.md) resolves factory types from the portable compilation and referenced assemblies. Host emits its accessible portable factories; desktop Bootstrap emits factories from its desktop integration closure. Browser manifest generation preserves its bridge schema and identifiers through the remaining PowerShell template tool. Generated C# stays under `obj`.

Review reflection-heavy serializers, authored animation access, and polymorphic metadata separately for trimming/AOT. Package admission means reviewed managed assets and compilation suitability, not that every API works in a browser. The [package and source audit](../../work/progress/platform/portable-kernel-audit.md) records the review evidence.

## Compile lane and qualification

The repository's `global.json` selects SDK 10.0.401 without roll-forward and
workload set 10.0.401.1. Install `wasm-tools` from the repository root so the
workload command honors that set; the recorded browser runtime pack is 10.0.12
with Emscripten 3.1.56. Core source paths use `XREngine.Runtime.Core` consistently
in the Git index so a case-sensitive checkout includes the full project.

[Test-PortableBrowserCompile.ps1](../../../Tools/Test-PortableBrowserCompile.ps1)
builds the manifest projects for `browser-wasm` using the approved source flag.
Prepare Jolt using the commands in [browser publishing](../../work/progress/rendering/browser-project-publishing.md),
or pass `-JoltBrowserManagedSourceDirectory` and `-JoltBrowserArchiveDirectory`
for explicitly staged outputs. The script also accepts `-ArtifactsPath`.
[The CI lane](../../../.github/workflows/portable-browser-compile.yml) builds and
publishes the host and native diagnostic, cooks pinned Slang depth shaders, and
runs [real browser smoke qualification](../../../Tools/BrowserSmoke/README.md).
Its explicit software WebGPU mode does not establish device or performance
acceptance. Browser trimming/AOT remains rejected until separately qualified;
desktop NativeAOT has its own [cooked-launcher workflow](aot-final-game-builds.md).

When changing a shared contract, retain dependency direction, update its callers and narrow boundary checks, and qualify the affected desktop and browser paths. A successful source review cannot replace evaluated graph/native-asset inspection or application execution. Current outstanding qualification is tracked in the [integration checklist](../../work/todo/platform/native-subsystem-project-split-todo.md).
