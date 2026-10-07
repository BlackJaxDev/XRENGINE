# USD Import And Export Design

[Work docs index](../../README.md)

Code todo: [USD Import And Export TODO](../../todo/assets/usd-import-export-todo.md)

## Goal

Import `.usda`, `.usdc`, `.usd`, and `.usdz` into the prefab and model workflow with deterministic hierarchy, material, and animation results. Export a deterministic subset through a managed USDA writer, and export USDC and USDZ through OpenUSD. Keep async mesh processing, scene publication, and material and texture remap seeding.

USD is a layered scene description system, not a mesh file format. The design must keep layers, specs, and composition explicit.

## Two-Path Architecture

1. A managed fast path reads USDA, indexes USDZ packages, and indexes and selectively decodes USDC.
2. An OpenUSD interop fallback handles unknown Crate versions, complex composition, broad schema and value coverage, and full-fidelity USDC and USDZ export.

An engine-neutral `XREngine.Usd` core owns layer and package parsing, stage resolution, value decode, and export documents. Engine integration (`SceneNode`, `XRMesh`, `XRMaterial`, import orchestration) stays in `ModelAssetImporter`.

## Container Rules

| Item | Rule |
|---|---|
| `.usda` | UTF-8 text with a `#usda <version>` header. |
| `.usdc` | Crate binary. Validate the bootstrap and TOC before any section table. |
| `.usd` | Sniff the contents. It can be USDA or USDC. |
| `.usdz` | A package. Resolve the default layer, then use the USDA or USDC parser. |
| Package-relative assets | Resolve deterministically for packaged and unpackaged layers. |

## USDA Rules

- Parse layer metadata, prim specs, property specs, dictionaries, arrays, and time samples for the supported subset.
- Keep asset-valued strings separate from ordinary strings. Write the asset delimiter syntax.
- Keep the tokenizer slice-based. Make strings only when a consumer needs them.
- Writer output is deterministic in order and format. Source style is not kept.

## USDC Crate Rules

| Item | Rule |
|---|---|
| Bootstrap | Validate the `PXR-USDC` ident, version bytes, and TOC offset first. |
| TOC | Check that section spans are in bounds and do not overlap. |
| Core sections | `TOKENS`, `STRINGS`, `FIELDS`, `FIELDSETS`, `PATHS`, `SPECS`. |
| Specs | Path index, field-set index, and spec type. |
| Fields | Field-name token index and `ValueRep`. |
| Paths | Rebuild paths lazily from the path-tree encoding. |
| Versions | Unknown or newer versions route to OpenUSD or fail closed. |
| Endianness | Little-endian fast path with explicit reads, not struct reinterpretation. |

`ValueRep` bits: 63 array, 62 inlined, 61 compressed, 60 array edit, 48 to 55 type tag, 0 to 47 payload. Validate payload offsets before use.

## USDZ Rules

- Reject compressed or encrypted entries on the fast path.
- Each file's data starts at a 64-byte multiple from the package start.
- The first file is the default layer.
- Parse embedded layers from the package slice. Do not extract to a temporary folder.

## Composition Rules

- Import code states whether it reads one layer or a composed stage.
- Default prim, sublayers, references, payloads, asset paths, variant selections, and time samples are first-class.
- Unknown metadata and unsupported values are kept in an intermediate form or sent to the OpenUSD fallback. They are not discarded silently.
- Composition is never partly applied without a recorded fallback or diagnostic.
- Strict and tolerant modes share the same bounds checks.

## Layer IR

The managed path builds tables, not a per-prim object graph: `TokenTable`, `StringTable`, `PathTable`, `SpecTable`, `FieldTable`, `FieldSetTable`, `LayerMetadataTable`, and later `CompositionArcTable` and `TimeSampleTable`. Paths and values decode lazily. The IR is the input for the USDA writer and the OpenUSD export bridge.

Pipeline: container detection, structural index, value decode and composition, engine bridge.

## Allocation Rules

- No per-spec, per-field, or per-token allocations in parser hot paths. No LINQ, boxing, or capturing delegates in scan and decode loops.
- Use `ReadOnlySpan<byte>`, `BinaryPrimitives`, `ArrayPool<T>`, and `ref struct` readers. Use memory-mapped reads for large Crate and package files.
- Keep per-file caches thread-local or striped for parallel imports.

## Validation Tools

| Tool | Use |
|---|---|
| `usdcat` | Convert encodings and compare round trips. |
| `usddumpcrate` | Inspect Crate structure. |
| `sdfdump` | Inspect layer and spec structure. |
| `usdzip` | Make and check reference packages. |
| `usdchecker` | Validate exported assets. |
| OpenUSD API | Differential oracle for composition, schemas, and writer output. |
