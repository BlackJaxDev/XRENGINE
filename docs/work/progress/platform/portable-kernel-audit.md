# Portable kernel boundary audit

Current rules: [Portable Project Rules](../../../developer-guides/runtime/portable-projects.md). Project ownership: [Runtime Project Organization](../../../architecture/runtime/project-organization.md). This audit records review evidence; it does not certify the deferred integration checks.

The browser compile closure is listed in `Build/Portable/PortableProjects.tsv`. Each listed project targets `net10.0` with its normal source and package set. The MSBuild guard checks every evaluated `Compile` item, rejects project-level source filtering and references outside that closure, and checks direct package name and version against `Build/Portable/PortablePackages.tsv`. Native file references, copy items, and resolved native runtime assets are rejected; the browser host may use the .NET SDK's own `browser-wasm` runtime pack. The CI lane compiles each listed project for `browser-wasm` without launching it. Only browser-wasm trim/AOT is rejected by this lane; desktop native AOT remains independent. Trimming and browser AOT need separate metadata, serializer, and native binding qualification.

## Reviewed managed packages

| Package | Active use | Browser boundary |
| --- | --- | --- |
| `BitsKit` 1.2.0 | Humanoid pose bitstream IO in Runtime.Core | Managed package with framework assemblies; retained in Core. |
| `DotnetNoise` 1.0.0 | Procedural noise transform in Runtime.Core | Managed `netstandard2.0` assembly; retained in Core. |
| `ImmediateReflection` 2.0.0 | Animation member access in Animation | Managed `netstandard2.1` assembly; retained in Animation. Reflection metadata and trimming need separate qualification. |
| `MIConvexHull` 1.1.19.1019 | Light-probe topology in Rendering | Managed `netstandard` assemblies; retained in Rendering. |
| `Newtonsoft.Json` 13.0.4 | JSON assets and state payloads in Runtime.Core | Managed `net6.0`/`netstandard` assemblies; retained in Core. Reflection-based serialization needs separate trim/AOT qualification. |
| `K4os.Compression.LZ4`, `LZMA-SDK`, `NVorbis`, `SharpZipLib`, `ZstdSharp.Port` | Data compression, archive extraction, and Vorbis decode | Managed package assets found in the local cache; retained in Data for serialized asset compatibility. |
| `MemoryPack`, `YamlDotNet` | Asset serialization across portable projects | Managed packages; retained where source uses them. Generated metadata and polymorphic types need separate trim/AOT qualification. |
| `MathNet.Numerics` 5.0.0 | FFT in AudioBuffer | Managed package; retained in Audio. |
| `System.IO.Hashing` 10.0.10 | Asset, shader, and resource hashes | Managed package; retained in Data, Core, and Rendering. |
| `Silk.NET.Maths` 2.23.0 | Window extent and resize value types in Rendering | Its `net5.0` asset has no dependencies or native files. Native Silk window/input/context APIs remain desktop-owned. |

Direct `BitsKit`, `DotnetNoise`, `ImmediateReflection`, and `MathNet.Numerics` references in Rendering had no source or configuration use and were removed. Direct `Silk.NET.Core` references in Data and Core likewise had no source use and were removed. The listed packages are reviewed for browser compilation and native-asset absence, not for trimming or native AOT.
Direct `MemoryPack` in Extensions and direct `MemoryPack` and `MIConvexHull` in Modeling also had no source or configuration use and were removed.

Polymorphic YAML resolves published metadata or, in development, already-loaded managed types in Data. Desktop composition installs the optional output-directory assembly loader used for legacy development types; a browser host without that capability receives an explicit unresolved-type diagnostic. The collectible game-project loader resides in the Desktop assembly under its existing public namespace.

## Reflection and dynamic construction review

`Build/Portable/SourceApiPolicy.tsv` records each allowed source file and reflection-like symbol with its reviewed purpose. This is a lexical source audit, not proof that metadata survives trimming. It distinguishes `JsonElement.GetProperty`, YamlDotNet descriptor methods, and engine material lookup methods from `System.Type` reflection. It also records managed serializer, authored animation, shader, camera, and prefab metadata access precisely rather than exempting a whole assembly or namespace.

The current published runtime mode is NativeAOT and reads persisted type metadata. Type redirects, replication properties, transform selectors, and persisted cooked or asset type names now use that table without a loaded-assembly scan; missing registered replication types, properties, and transforms fail with named diagnostics. Development builds retain loaded-assembly discovery for authoring. Published YAML assets are unsupported, so YAML converter discovery is development-only. Built-in render commands are generated into a Rendering-local module initializer, including their script names and legacy type aliases; caller-registered factories remain available. The portable source guard permits only that exact generated `Compile` item under Rendering's intermediate output and scans its source too. Source review cannot establish that generated registrations cover every command or that published metadata contains every serialized type. `Assembly.Load*`, `AssemblyLoadContext`, Reflection.Emit, dynamic IL compilation, direct vendor APIs, unmanaged calls, sockets, native assets, and native project references remain denied. Optional desktop assembly loading is installed only by the desktop composition root.

## Deferred qualification

- Native window, font, socket, and process code has moved behind leaf-owned implementations or neutral capabilities. The source boundary is ready for compile qualification; no browser build has been run in this source-only pass.
- Rendering's generated command registry may be emitted under an isolated intermediate output directory. The guard compares the evaluated generated path with the actual Compile item, rejects other source substitutions, and still scans that generated source.
- `OperatingSystem.IsBrowser`, platform identity diagnostics, and managed `System.Drawing.Primitives` value types are allowed. They are ordinary managed decisions and values, while the guard rejects calls that acquire desktop services or native vendor objects.
- Browser compilation, resolved native asset inspection, trim/AOT behavior, physics runtime creation, and live feature validation remain unexecuted in this source-only pass.

No browser build, test, or runtime run was executed during this source-only integration pass. Dependency and license documentation was regenerated from project declarations and existing local dependency metadata.

SkiaSharp provides official browser packages, including `SkiaSharp.Views.Blazor` and WebAssembly native assets, so it remains a candidate for a separately qualified browser UI leaf. This split preserves the current desktop Skia supply. See the [upstream package matrix](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md).
