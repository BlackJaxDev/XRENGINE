# XRMesh: Vertex Hierarchy & Remapper Optimizations

Status: implemented as of 2026-04-19.

This note was originally filed as follow-up work. Both primary items have since
landed, so this file now records the resulting state rather than an open todo.

---

## 1. Vertex hierarchy and per-instance list overhead

Implemented.

`Vertex` is now a direct `VertexData`-derived type and no longer inherits from
`VertexPrimitive`. The standalone vertex path therefore no longer allocates a
per-instance `List<Vertex>` just to contain itself. `VertexPrimitive` still owns
the shared `_vertices` list for actual multi-vertex primitives.

Current state:

- `Vertex` derives from `VertexData` and implements `IEnumerable<Vertex>` by
  yielding itself.
- `VertexPrimitive` remains the multi-vertex base and still owns
  `protected List<Vertex> _vertices = []`.
- Generic mesh constructors already separate single vertices from multi-vertex
  primitives, so the hierarchy split did not require an invasive constructor API
  rewrite.

Result:

- The original per-vertex list allocation problem is gone.
- The memory and GC overhead described in the original note no longer applies to
  the common standalone `Vertex` construction path.

---

## 2. Remapper deduplication and Vertex equality

Implemented.

`Vertex.Equals` and `Vertex.GetHashCode` are now structural, so independently
constructed vertices with equivalent data compare equal and hash identically.
The remapper path used by XRMesh now also uses a generic key wrapper instead of
the older `Dictionary<object, int>` implementation.

Current state:

- `Vertex.Equals` performs field-by-field structural comparison.
- `Vertex.GetHashCode` hashes the contents of texture coordinates, colors,
  weights, and blendshapes rather than their container references.
- `XREngine.Remapper` now deduplicates through `Dictionary<RemapKey<T>, int>`.
- The duplicate `System.Remapper` implementation was reduced to a compatibility
  shim over `XREngine.Remapper`, so there is only one maintained behavior.
- Unit coverage exists for structural vertex equality and remap deduplication.

Result:

- Vertices created independently but carrying identical data now remap to the
  same first-appearance entry.
- The XRMesh remap path no longer depends on reference-identity container hashes
  or an `object`-key dictionary to find duplicates.

---

## Optional follow-up

One suggestion from the original note remains optional rather than required:

- cache `Vertex` hash codes only if profiling shows `GetHashCode()` becoming a
  measurable hotspot in large remap/import workloads.

That is a performance tuning option, not an outstanding correctness issue.
