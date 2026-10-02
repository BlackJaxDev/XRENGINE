# Browser game assembly reference audit

The editor checks the compiled authored-game DLL immediately after it is built and loaded, before the authored world is cooked. `CodeManager.PublishBrowserApplication` repeats the check before generating browser registrations or starting the browser publish. Both gates compare the loaded module ID with the file being audited. A rejected game reports up to 32 sorted, distinct, length-bounded findings with assembly, type, and member names, plus a notice if additional findings were omitted. The diagnostics direct platform implementation into the desktop host rather than requiring a different gameplay assembly.

## Coverage

- Managed metadata `AssemblyRef`, `TypeRef`, and `MemberRef` rows identify direct references, including types appearing in generic arguments, signatures, inheritance, and nested type owners. `TypeSpec` member parents are decoded so calls on constructed generic types retain their declaring type and member names.
- The explicit desktop-leaf inventory covers native/editor/cook-host engine assemblies outside the portable browser project set and known desktop framework APIs that do not provide useful platform annotations. A blocked assembly with no identifiable type reference still has an assembly-level diagnostic.
- For resolved adjacent managed assemblies and .NET reference-pack declarations, `SupportedOSPlatform` and browser-prefixed `UnsupportedOSPlatform` annotations are read at assembly, enclosing type, method, field, property, and event level. Type forwarders are followed and adjacent assemblies must match the referenced version, culture, and signing identity before their declarations are trusted. The editor reads metadata only; it does not load or execute game or referenced assemblies. Framework reference assemblies are used instead of host runtime implementations, whose OS-specific annotations can falsely reject browser-safe references. If the SDK reference pack is unavailable, the audit fails rather than silently omitting framework annotation checks.
- Game-defined P/Invoke methods are reported by declaring type and member name. Diagnostics are deterministic and bounded in count and rendered length.

## Limits

This is a direct-reference admission check, not a reachability, trimming, or runtime compatibility proof. It does not discover targets assembled through reflection, dynamic dispatch or generated code, nor does it recursively validate transitive dependencies. Unresolved third-party declarations are checked against the explicit leaf/API policy but cannot be checked for platform annotations; some valid game-build outputs do not copy all package binaries beside the game DLL. A referenced overload is rejected from its annotation only when its declaration signature can be matched unambiguously; declarations absent from the available build output are not guessed. Browser execution and cooked-world capability checks remain separate gates.

## Validation

- `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU --no-restore` against the existing isolated desktop build graph: succeeded with zero warnings and errors.
- Direct invocation against the existing compiled `RollingBall.dll` and `RenderingParity.dll`: both accepted. An existing desktop-platform DLL was rejected with named API references.
- Ignored metadata fixture: a browser-safe method on a constructed generic type and a portable generic-argument overload were accepted; Windows-only direct and nested types, a browser-unsupported method on a constructed generic type, and the annotated counterpart overload were rejected with their precise type/member names.
