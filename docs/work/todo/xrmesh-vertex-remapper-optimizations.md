# XRMesh: Vertex Hierarchy & Remapper Optimizations

Two structural issues in the mesh construction pipeline that are worth addressing
as follow-up work. Neither is a regression — both are pre-existing design choices
that become cost centres at scale.

---

## 1. Vertex inherits VertexPrimitive — per-instance list overhead

### Problem

`Vertex` extends `VertexData` → `VertexPrimitive` → `XRBase`.
`VertexPrimitive` declares `protected List<Vertex> _vertices = []`, and
every `Vertex()` constructor calls `_vertices.Add(this)` (Vertex.cs line 35).

When a mesh is built from standalone `Vertex` objects (the common path for
both the triangle constructor and the Assimp importer), each vertex allocates a
`List<Vertex>` containing only itself. For a 100k-vertex mesh this means 100k
small lists that are never read during construction — only the `Vertices`
property on multi-vertex primitives like `VertexTriangle` actually uses the list
meaningfully.

### Impact

- **Memory:** Each `List<Vertex>` is ~56 bytes (object header + internal array +
  count + version). 100k vertices ≈ 5.6 MB of wasted list overhead.
- **GC pressure:** 100k tiny Gen-0 objects per mesh import, all promotable.
- **Construction time:** `List.Add` with initial-capacity-0 growth pattern means
  one allocation + one array copy per vertex.

### Suggested approach

Split the hierarchy so standalone `Vertex` is not a `VertexPrimitive`:

```
VertexData (Position, Normal, Tangent, UVs, Colors)
├── Vertex          ← no longer inherits VertexPrimitive, no _vertices list
└── VertexPrimitive ← keeps _vertices for multi-vertex shapes
    ├── VertexLine
    ├── VertexTriangle
    ├── VertexPolygon
    └── VertexLineStrip / VertexLinePrimitive
```

Both `Vertex` and `VertexPrimitive` can share `VertexData` as a common base, and
the mesh constructors already separate single-vertex (`case Vertex v:`) from
multi-vertex primitives in their switch statements, so the API impact is minimal.

The `IEnumerable<Vertex>` interface currently on `VertexPrimitive` should move to
multi-vertex primitives only, or `Vertex` can implement it by yielding itself.

### Files likely affected

- `XRENGINE/Rendering/Vertex/Vertex.cs`
- `XRENGINE/Rendering/Vertex/VertexData.cs`
- `XRENGINE/Rendering/Vertex/VertexPrimitive.cs`
- `XRENGINE/Rendering/Vertex/VertexTriangle.cs` (and other multi-vertex types)
- `XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Constructors.cs`
- Any call site that does `foreach (var v in vertex)` on a single Vertex

### Risk

Medium — `Vertex` is used everywhere. A clean split needs a type-check audit
across all mesh, modeling, and animation code. Pre-ship status means the API
break is acceptable.

---

## 2. Remapper uses Dictionary<object, int> with broken Vertex equality

### Problem

`Remapper.Remap<T>` (Core/Tools/Remapper.cs) uses `Dictionary<object, int>` to
deduplicate vertices. This relies on `Vertex.GetHashCode()` and
`Vertex.Equals()`.

`Vertex.GetHashCode()` hashes:
- `Position` (Vector3 — value-based, correct)
- `Normal`, `Tangent` (nullable Vector3 — value-based, correct)
- `TextureCoordinateSets` (List<Vector2> — **reference hash**, wrong)
- `ColorSets` (List<Vector4> — **reference hash**, wrong)
- `Weights` (Dictionary — **reference hash**, wrong)
- `Blendshapes` (List — **reference hash**, wrong)

`Vertex.Equals()` delegates to `GetHashCode() == GetHashCode()` — it *only*
compares hash codes, never field values.

This means:
- Two vertices with identical data in separate `List<Vector2>` instances get
  different hash codes and are **not deduplicated** by the remapper.
- Hash collisions between genuinely different vertices cause false-positive
  equality (since `Equals` only compares the hash).

### Impact

- **Correctness:** The remapper almost never deduplicates vertices that were
  created independently (e.g. two `VertexTriangle` objects sharing a corner
  position). This inflates index buffers and vertex counts.
- **Performance:** The `Dictionary<object, int>` lookup is doing useless work —
  it hashes and compares but never actually finds duplicates that should match.

### Suggested approach

1. **Fix `GetHashCode`** to hash the *contents* of lists/dictionaries, not their
   references. For `TextureCoordinateSets`, hash each `Vector2` element; for
   `ColorSets`, each `Vector4`; etc.

2. **Fix `Equals`** to perform field-by-field structural comparison instead of
   hash-only comparison. Hash equality is a necessary but not sufficient
   condition.

3. **Consider making `Remapper` generic** with `Dictionary<T, int>` instead of
   `Dictionary<object, int>` to avoid the cast to `object` and use the correct
   `IEqualityComparer<T>`.

4. **Cache the hash code** on `Vertex` if it becomes expensive to recompute
   (mark dirty on mutation via `SetField`).

### Files likely affected

- `XRENGINE/Rendering/Vertex/Vertex.cs` (GetHashCode, Equals)
- `XRENGINE/Core/Tools/Remapper.cs` (generic Dictionary)
- Possibly `XRENGINE/Rendering/Vertex/VertexData.cs` if equality moves there

### Risk

Low-medium — fixing equality semantics is straightforward, but if any code path
relies on reference-distinct vertices comparing as *not equal*, behavior changes.
A test that remaps a known mesh and asserts expected deduplication count would
catch regressions.

---

## Priority

Both items should be tackled together since the Vertex hierarchy change (#1)
will touch the same files as the equality fix (#2). Recommend doing #2 first
(smaller, higher correctness value) then #1 (larger, primarily a memory/perf
win).
