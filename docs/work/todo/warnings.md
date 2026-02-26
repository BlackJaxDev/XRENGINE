# XREngine Warning Remediation Checklist

_Last refreshed: 2026-01-25_

## Usage
- Check off items as warnings are eliminated; keep counts updated after each build.
- Within every project, tasks are sorted High → Medium → Low priority; types within the same priority share similar fixes.
- "Owner hints" point to the main subsystems/files to inspect first.

---

## Shared / Cross-Cutting
### High Priority
- [x] `NU1602` – SharpFont.Dependencies lower bounds missing in **Audio, Data, Input, Modeling, Animation, Editor, Server, UnitTests, VRClient** (36 hits). Add explicit `<PackageReference Include="SharpFont.Dependencies" Version="2.5.5" />` or upgrade SharpFont so downstream projects inherit a bounded version. _Resolved 2025-11-22 by adding `SharpFont.Dependencies` to XREngine.Data, which propagates the bounded range to all dependent projects._
- [x] `IL20xx` / `IL30xx` reflection trimming hazards raised by **AssetManager, XRDataBuffer, DelegateBuilder, XRComponent, JsonAsset, UI Video**. Centralize source generators / `DynamicallyAccessedMembers` attributes and move to trimming-friendly APIs. _Resolved 2026-01-25: Consolidated trimming annotations and replaced reflection-heavy paths with generator-backed helpers._

### Medium Priority
- [x] `CA1416` platform guards where Windows-only APIs (GDI+, WMI, Bitmap) leak into cross-platform assemblies. Add `OperatingSystem.IsWindows()` checks or move code into Windows-specific partials. _Resolved 2026-01-25: Windows-only calls isolated in platform-specific partials with guards in shared entry points._

---

## Project: XREngine (Core)
> 0 compiler warnings remaining (2026-01-25 rebuild). Major cleanup of nullability, obsolete API usage, and member hiding warnings.

### High Priority
- [x] `CS8618` (≈70) – non-nullable members in physics, rendering, tools. _Resolved 2026-01-25: Added initializers or made fields nullable in `TReplicate`, `Remapper`, `GPUPhysicsChainComponent`, `ShaderArray`, `TriStripper`, `UITreeTransform`._
- [x] `CS8602` / `CS8604` (≈36) – null derefs in IK solvers, GPU physics chain, XR materials. _Resolved 2026-01-25: Added null guards and comprehensive null checks in buffer bindings, STT providers, spline components._
- [x] `CS8601` / `CS8625` – null reference assignments and null literal issues. _Resolved 2026-01-25: Fixed `TEnumDef`, `OpenXRAPI.State`, `ShaderBVector3`, post-processing collections, Vulkan resource handlers._

### Medium Priority
- [x] `CS0108` – explicit `new` keywords for `FlyingCameraPawnComponent.CameraComponent` and `Transform.TransformRotation/InverseTransformRotation`. _Resolved 2026-01-25._
- [x] `CS0618` – obsolete method calls to `SetParent`/`AddChild`. _Resolved 2026-01-25: Updated to use `EParentAssignmentMode.Immediate` in SceneNode, SceneCaptureComponent, XRCubeFrameBuffer, SceneNodePrefabUtility._
- [x] `CS0618` – obsolete `ContextFlagMask.ContextFlagDebugBit`. _Resolved 2026-01-25: Changed to `DebugBit` in OpenGLRenderer._
- [x] `CS0219` / `CS0168` – unused variables. _Resolved 2026-01-25: Removed `memberType` from UnityConverter, `ex` from XRAssetYamlTypeConverter, `hasDepthAttachment` from FrameBufferRenderPasses, `timeout` from UIVideoComponent._
- [x] `CS0105` – duplicate using directives. _Resolved 2026-01-25: Removed duplicates in XRMesh.Core.cs._

### Low Priority
- [ ] `CA1416` – platform guards for Windows-only APIs (not compiler warnings, analyzer-only).
- [ ] `SYSLIB0050` – obsolete serialization patterns (not currently triggering).

---

## Project: XREngine.Data
> 0 warnings remain (2026-01-25 rebuild). Medium/low priority analyzer items cleared and legacy lowercase numeric structs addressed.

### High Priority
- [x] `CS8601` (6) – nullable assignments when initializing `XREventGroup` handlers and `ConsistentIndexList._dictionary`/`_node`. _Resolved 2026-01-25: XREventGroup already declared nullable fields; ConsistentIndexList refactored to use `EqualityComparer<T?>` and pattern matching in enumerators._
- [x] `CS8603` (1) – `Miniball.MinimumSphere` returns null for degenerate sets. _Resolved: Method no longer exists in codebase._
- [x] `CS8619` (1) – `Deque.Tester` casts `object?[]` to `object[]`. _Resolved: Signatures already use `object?[]` correctly._

### Medium Priority
- [x] `CA2022` (3) – fix partial reads in `DataSource.Read(Span<byte>)` and `AudioData` buffer loaders. _Resolved 2026-01-25: Read loops now guarantee full buffers or throw on EOF._
- [x] `IL2091` / `IL3050` (~4) – annotate `DataSource.ToStruct<T>` and switch BVH enum iteration to `Enum.GetValues<T>()` to appease trimming. _Resolved 2026-01-25: Added `DynamicallyAccessedMembers` and updated enum iteration._
- [x] `IL3050` (1) – swap `UnityAnimationClip` to `StaticDeserializerBuilder` to avoid YamlDotNet reflection requirement. _Resolved 2026-01-25: Deserialization now uses static builder._

### Low Priority
- [x] `CS8981` (8) – rename lowercase numeric wrappers (`bfloat`, etc.) or suppress with justification. _Resolved 2026-01-25: Types renamed and aliases added for compatibility._
- [x] `CS0414` / `CS0067` – remove unused members (`UserSettings._windowState`, `EventArray.ItemChanged`). _Resolved 2026-01-25: Removed unused fields/events or wired them to active usage._

---

## Project: XREngine.Animation
> 0 compiler warnings remaining (2026-01-25 rebuild). Property animation pipeline cleanup complete.

### High Priority
- [x] `CS8602` (78) – null derefs across `PropAnim*` and vector keyframes. _Resolved 2026-01-25: Implemented missing abstract methods in Vector2Keyframe, Vector3Keyframe, Vector4Keyframe with proper null guards._
- [x] `CS8600` (28) – conversions to non-null types when keyframes missing. _Resolved: All keyframe interpolation methods now check for null and return fallback values._
- [x] `CS8765` (20) – override signatures accepting nullable `next` to match base definitions. _Resolved: Removed orphaned `#pragma warning restore` directives; signatures already match._

### Medium Priority
- [x] `CS8618` (12) – initialize delegate fields inside `VectorKeyframe`. _Resolved: Delegates have inline initializers._
- [x] `CS8603` (8) – guard `PropAnimLerpable` return paths. _Resolved: Removed stale TODO comment; return paths already guarded._

### Low Priority
- [x] `CS0693` – rename inner generic parameter on `PropAnimKeyframed<T>`. _Resolved: No conflict exists; class uses `TKeyframe` not `T`._
- [x] `CS8321` – remove unused local functions in `AnimationClip`. _Resolved: No unused local functions found; `Lerp` and `PopulateVMDAnimation` are both used._

---

## Project: XREngine.Extensions
> 0 compiler warnings remaining (2026-01-25 rebuild). Reflection and marshal helpers annotated.

### High Priority
- [x] `IL2075` / `IL2070` (8/6) – annotate `TypeExtension` reflection helpers with `DynamicallyAccessedMembers`. _Resolved 2026-01-25: Methods already annotated; added missing attribute to `FitsConstraints`._
- [x] `IL2067` (8) – ensure `Activator.CreateInstance` targets declare necessary member requirements. _Resolved: All `CreateInstance` extension methods have appropriate attributes._
- [x] `IL2091` (4) – switch to generic `Marshal.PtrToStructure<T>` overloads. _Resolved 2026-01-25: Changed `MarshalExtension.AllocArrayCoTaskMem<T>` to use `Marshal.SizeOf<T>()`._

### Medium Priority
- [x] `CS8625` (4) – null literals inside `ReaderWriterLockSlim` helpers. _Resolved: Lock token fields already declared nullable; no warnings present._
- [x] `CA2022` – fix partial reads in `StreamExtension`. _Resolved: `ReadExactly` and `ReadExactlyAsync` already loop until all bytes read._

---

## Project: XREngine.Editor
> 0 compiler warnings remaining (2026-01-25 rebuild).

### High Priority
- [x] `CS8601` (4) – nullable assignments in unit test helpers and asset explorer. _Resolved: No warnings found in current codebase._

### Medium Priority
- [x] `CS8603` (2) – null returns in test pawns. _Resolved: Methods properly handle nullable return types._

### Low Priority
- [x] `CS0169` – unused `_inspector` field (UIEditorComponent). _Resolved: Field no longer exists or is used._
- [x] `NU1602` – SharpFont dependency bound.

---

## Project: XREngine.Audio
> 0 compiler warnings remaining (2026-01-25 rebuild).

### High Priority
- [x] `NU1602` (4) – match SharpFont dependency range.

### Medium Priority
- [x] `CS0414` (2) – remove unused `_gainScale` field. _Resolved: Field is actually used by `GainScale` property._

---

## Project: XREngine.VRClient
> 0 compiler warnings remaining (2026-01-25 rebuild).

### High Priority
- [x] `CS8600` (2) – null-to-non-null conversions at startup. _Resolved: No warnings present in current codebase._

### Medium Priority
- [x] `NU1602` (4) – SharpFont dependency.
- [x] `CA1416` (10) – gate WMI/GPU-detection via Windows-only partial classes (analyzer warning, not compiler). _Resolved 2026-01-25: Moved to Windows-only partials with runtime guards._

---

## Project: XREngine.Input / Modeling / Server / UnitTests

### High Priority
- [x] `NU1602` (4 each) – inherit SharpFont lower bound.

---

## Next Steps
1. ~~Finish XREngine.Data cleanup (XREventGroup/ConsistentIndexList assignments, Miniball return value)~~ ✅ Done 2026-01-25
2. ~~Sweep XREngine.Animation property pipelines for nullable overrides (`next` parameters) and guard missing keyframes.~~ ✅ Done 2026-01-25
3. ~~Address IL-trimming warnings in Extensions (Marshal/Type annotations).~~ ✅ Done 2026-01-25
4. ~~Fix XREngine.Core nullability, obsolete APIs, and member hiding warnings.~~ ✅ Done 2026-01-25
5. ~~Fix XREngine.VRClient warnings.~~ ✅ Done 2026-01-25
6. ~~Address CA1416 platform guard warnings if cross-platform builds are needed.~~ ✅ Done 2026-01-25
7. ~~Update this checklist after each build to keep progress visible.~~ ✅ Done 2026-01-25
