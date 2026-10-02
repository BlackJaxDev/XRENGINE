# Downloadable Content Execution Policy

[Architecture index](../README.md)

Status: **Current policy enforced.** Target direction recorded; no VM work
has started.

Owner: Runtime / Networking / Control plane

This document records how downloaded worlds and avatars may influence the
behavior of a running XRENGINE player, and what the package pipeline enforces
today. It is the authority that package verification diagnostics link to.

## 1. Current Policy: Data Only

Downloaded worlds and avatars carry **data only**. Behavior comes from
engine-compiled, statically registered components that already exist in the
player binary. A package may configure those components through serialized
asset data, but it cannot introduce new executable code of any kind.

Concretely, a world package (`WorldPackageManifest` plus its files) may
contain:

- the `.asset` world entry point and dependent `.asset` files;
- cooked archives, textures, meshes, audio, animation, and other engine
  asset payloads;
- the `world-package.json` descriptor written by staging.

A world package may **not** contain:

- Windows PE images (`MZ` header), with or without a CLI header. This covers
  native DLLs, managed assemblies, and executables regardless of extension.
- ELF images (`0x7F 'E' 'L' 'F'`).
- Mach-O images, including 32-bit and 64-bit fat binaries.
- Files with these extensions, regardless of content: `.dll`, `.exe`, `.so`,
  `.dylib`, `.cs`, `.csproj`, `.ps1`, `.bat`, `.cmd`, `.sh`.

Rejection checks both extension and content signature; unreadable files fail closed. A PE file
renamed to `.bin` is rejected by its signature. An empty `.dll` is rejected by
its extension.

## 2. Where The Policy Is Enforced

`WorldPackageManifestBuilder` applies the checks at every boundary where a
package is created, verified, or published:

| Boundary | Behavior |
|---|---|
| `CreateFromDirectory` | Throws `InvalidOperationException` naming the first offending file and its detected kind. A package containing executable content cannot be authored. |
| `VerifyManifest` | Rejects manifests that declare a forbidden extension without touching the filesystem. The result reports the paths in `ExecutablePayloads`. |
| `Verify` | Reads each declared file's leading bytes and classifies it. Offending files are reported in `ExecutablePayloads`, and `Success` is false. |
| `StageVerified` and `Mirror` | Call `Verify` on the source before copying and on the staged copy before publishing, so an executable payload never reaches a client cache directory. |
| `LocalPackageCatalog.Load` | Runs the data-only check against the catalog root and rejects executable content, unsafe paths and missing files before any endpoint serves the manifest or a file. |
| `ManagedContentEndpoints` | Serves only packages that pass `LocalPackageCatalog.Load`, so a non-compliant package is never downloadable. |

Each diagnostic names the file, the detected kind, and this document.

## 3. Game Bootstrap Identity

`WorldPackageManifest.GameBootstrapId` selects which compiled-in bootstrap
runs a package-authored world. It is a **lookup key into the player binary**,
never a reference to package content.

- The only bootstrap registered today is `world-v1`
  (`WorldPackageBootstrapIds.BuiltInWorldV1`). It configures a `CustomGameMode`
  with no default pawn and lets realtime admission create remote pawns.
- `ManagedServerWorker`, `ManagedClientWorldLoader`, and the control plane
  registry reject any other value before loading content.
- Adding a bootstrap means adding a compiled-in registration and a new
  constant; it never means reading code or type names from the package.

## 4. Managed Assembly Loading

`GameCSProjLoader` is the only runtime path that loads managed assemblies
after startup. It exists for editor and development hot reload of the game
project's own source.

- NativeAOT players cannot load new assemblies, and the loader throws before
  attempting to.
- Published CoreCLR players also refuse runtime assembly loading. The guard
  covers both `LoadFromPath` and `LoadFromStream`.
- Content roots registered through `GameCSProjLoader.ProtectContentRoot` are
  refused even in development builds. `ManagedClientWorldLoader` registers the
  client cache root and the staged package root before loading the world, so a
  development editor that joins a managed session cannot be tricked into
  loading an assembly that arrived as "content".

`AssemblyLoadContext` provides unloading, not security isolation. .NET has no
in-process sandbox, so untrusted IL loaded into the player runs with the
player's privileges. "CoreCLR player plus downloaded DLLs" is therefore not an
acceptable configuration for untrusted content, which is why the published
guard is unconditional.

## 5. Target Direction

When creator-authored behavior is required, the intended path is:

1. Author a **restricted C# subset**.
2. Compile it to IL with the normal compiler.
3. **Verify** the IL against an allowlist of permitted types, members, and
   instructions.
4. Translate the verified IL into a compact engine **bytecode**.
5. Execute that bytecode in a **metered engine VM** with explicit budgets for
   time, memory, and host API access.

Visual node graphs target the same VM, sharing its verifier and host API.
This mirrors the shape of compiling C# to an assembly and then translating it
into a constrained target, used here for sandboxing and AOT compatibility
rather than for speed. The closest external precedent is UdonSharp.

## 6. Rejected For Now

| Option | Reason |
|---|---|
| Embedded WASM runtime with an engine host API | Requires a new native dependency and license review, which the current approval does not cover. C# through WASI is heavyweight for the component sizes involved, and the host API surface would duplicate the VM's. |
| Out-of-process script host per world | Provides an OS-level sandbox, but adds IPC latency on every component tick and significant operational complexity: process lifetime, crash recovery, and per-world resource accounting. |
| CoreCLR player with downloaded DLLs | Not a sandbox. See section 4. |

## 7. Constraints

- NativeAOT cannot load new assemblies.
- `AssemblyLoadContext` is not a security boundary.
- Any future VM, verifier, or bytecode format needs its own design document,
  its own tracker, and a separate dependency approval if it introduces native
  code.

## 8. Open Question For v1

What creator behavior must v1 support, and must the VM exist before v1?

This is undecided. If the answer is that v1 ships with creator-authored
behavior, the VM design and tracker must open before the world-package format
or control plane accepts anything other than data. Until then the data-only
policy stands, and the package pipeline enforces it.

## Related Documents

- [Networking Design](../../work/design/networking/networking.md)
- [Source-Backed C# Script Components](../../work/design/scripting/source-backed-csharp-script-components.md)
- [Control Plane Runtime Architecture](control-plane.md)
- [Runtime Data Layout And Generated Contracts](../../work/design/runtime/runtime-data-layout-and-generated-contracts-design.md)
