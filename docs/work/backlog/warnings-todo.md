# XREngine Warning Remediation Checklist

_Last refreshed: 2026-01-19_

## Usage
- Check off items as warnings are eliminated; keep counts updated after each build.
- Within every project, tasks are sorted High → Medium → Low priority; types within the same priority share similar fixes.
- "Owner hints" point to the main subsystems/files to inspect first.

---

## Shared / Cross-Cutting
### High Priority
- [x] `NU1602` – SharpFont.Dependencies lower bounds missing in **Audio, Data, Input, Modeling, Animation, Editor, Server, UnitTests, VRClient** (36 hits). Add explicit `<PackageReference Include="SharpFont.Dependencies" Version="2.5.5" />` or upgrade SharpFont so downstream projects inherit a bounded version. _Resolved 2025-11-22 by adding `SharpFont.Dependencies` to XREngine.Data, which propagates the bounded range to all dependent projects._
- [ ] `IL20xx` / `IL30xx` reflection trimming hazards raised by **AssetManager, XRDataBuffer, DelegateBuilder, XRComponent, JsonAsset, UI Video**. Centralize source generators / `DynamicallyAccessedMembers` attributes and move to trimming-friendly APIs.

### Medium Priority
- [ ] `CA1416` platform guards where Windows-only APIs (GDI+, WMI, Bitmap) leak into cross-platform assemblies. Add `OperatingSystem.IsWindows()` checks or move code into Windows-specific partials.

---

## Project: XREngine (Core)
> ~228 warnings; focus on trimming, nullability, and rendering/platform code.

### High Priority
- [ ] `CS8618` (≈70) – non-nullable members in physics (`PhysicsDriver`, `Trigger`), rendering (`Lights3DCollection`, `RenderCommandViewport`), tools (`Remapper`, `GLSLParser`). Initialize in constructors or declare `required`.
- [ ] `IL3050` / `IL2026` (≈48/46) – `AssetManager`, `JsonAsset`, `XRWindow`, VR state, STT providers. Replace reflection-heavy serialization with source generators or annotate safe entry points.
- [ ] `CS8602` / `CS8604` (≈36/??) – null derefs in `Trigger`, IK solvers, GPU physics chain, XR materials. Add null guards or make parameters nullable.

### Medium Priority
- [ ] `CA1416` (≈36) – graphics/windowing utilities calling Windows-only APIs inside cross-targeted assemblies.
- [ ] `CS0414` & `CS0169` (~30) – unused private fields and unreachable code in VR state, render programs, timers. Remove or wrap with feature flags.
- [ ] `CS0649` (~24) – never-assigned struct fields (GPUScene, OctreeGPU). Either initialize or convert to properties with defaults.

### Low Priority
- [ ] `CS0108`/`CS0114` – explicit `new`/`override` keywords for `CharacterMovement3DComponent` and `Transform` hierarchies.
- [ ] `SYSLIB0050` – replace obsolete serialization patterns in `XRMesh.BufferCollection`.

---

## Project: XREngine.Data
> 23 warnings (2025-11-22 rebuild) remain, now concentrated in small utility types (`XREventGroup`, `ConsistentIndexList`, `Miniball`) plus analyzer noise (CA2022/IL2091) and the legacy lowercase numeric structs. BVH and Quadtree nullability paths are clean, as are FileMap/Deque/SimplePriorityQueue implementations.

### High Priority
- [ ] `CS8601` (6) – nullable assignments when initializing `XREventGroup` handlers and `ConsistentIndexList._dictionary`/`_node`. Initialize upfront or mark members nullable.
- [ ] `CS8603` (1) – `Miniball.MinimumSphere` returns null for degenerate sets; return `Sphere.Empty` or mark return type nullable.
- [ ] `CS8619` (1) – `Deque.Tester` casts `object?[]` to `object[]`; tighten the test helper signatures.

### Medium Priority
- [ ] `CA2022` (3) – fix partial reads in `DataSource.Read(Span<byte>)` and `AudioData` buffer loaders.
- [ ] `IL2091` / `IL3050` (~4) – annotate `DataSource.ToStruct<T>` and switch BVH enum iteration to `Enum.GetValues<T>()` to appease trimming.
- [ ] `IL3050` (1) – swap `UnityAnimationClip` to `StaticDeserializerBuilder` to avoid YamlDotNet reflection requirement.

### Low Priority
- [ ] `CS8981` (8) – rename lowercase numeric wrappers (`bfloat`, etc.) or suppress with justification.
- [ ] `CS0414` / `CS0067` – remove unused members (`UserSettings._windowState`, `EventArray.ItemChanged`).

---

## Project: XREngine.Animation
> 79 warnings; property animation pipeline is the hotspot.

### High Priority
- [ ] `CS8602` (78) – null derefs across `PropAnim*` and vector keyframes. Enforce non-null keyframe chaining or make `next` nullable.
- [ ] `CS8600` (28) – conversions to non-null types when keyframes missing. Add fallback values.
- [ ] `CS8765` (20) – override signatures accepting nullable `next` to match base definitions.

### Medium Priority
- [ ] `CS8618` (12) – initialize delegate fields inside `VectorKeyframe`.
- [ ] `CS8603` (8) – guard `PropAnimLerpable` return paths.

### Low Priority
- [ ] `CS0693` – rename inner generic parameter on `PropAnimKeyframed<T>`.
- [ ] `CS8321` – remove unused local functions in `AnimationClip`.

---

## Project: XREngine.Extensions
> 19 warnings centered on reflection helpers.

### High Priority
- [ ] `IL2075` / `IL2070` (8/6) – annotate `TypeExtension` reflection helpers with `DynamicallyAccessedMembers`.
- [ ] `IL2067` (8) – ensure `Activator.CreateInstance` targets declare necessary member requirements.
- [ ] `IL2091` (4) – switch to generic `Marshal.PtrToStructure<T>` overloads with properly annotated structs.

### Medium Priority
- [ ] `CS8625` (4) – null literals inside `ReaderWriterLockSlim` helpers.
- [ ] `CA2022` – fix partial reads in `StreamExtension`.

---

## Project: XREngine.Editor

### High Priority
- [ ] `CS8601` (4) – nullable assignments in unit test helpers and asset explorer.

### Medium Priority
- [ ] `CS8603` (2) – null returns in test pawns.

### Low Priority
- [ ] `CS0169` – unused `_inspector` field (UIEditorComponent).
- [x] `NU1602` – SharpFont dependency bound.

---

## Project: XREngine.Audio

### High Priority
- [x] `NU1602` (4) – match SharpFont dependency range.

### Medium Priority
- [ ] `CS0414` (2) – remove unused `_gainScale` field.

---

## Project: XREngine.VRClient

### High Priority
- [ ] `CA1416` (10) – gate WMI/GPU-detection via Windows-only partial classes.

### Medium Priority
- [x] `NU1602` (4) – SharpFont dependency.
- [ ] `CS8600` (2) – null-to-non-null conversions at startup.

---

## Project: XREngine.Input / Modeling / Server / UnitTests

### High Priority
- [x] `NU1602` (4 each) – inherit SharpFont lower bound.

---

## Next Steps
1. Finish XREngine.Data cleanup (XREventGroup/ConsistentIndexList assignments, Miniball return value) before moving on to CA/IL analyzers.
2. Sweep XREngine.Animation property pipelines for nullable overrides (`next` parameters) and guard missing keyframes.
3. Address IL-trimming warnings in Core and Extensions before enabling AOT pipeline.
4. Update this checklist after each build to keep progress visible.
