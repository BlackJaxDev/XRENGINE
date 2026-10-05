# RiveSharp Build Patch

`reuse-scene-pointer.patch` fixes two unused-variable errors in
`native/RiveSharpInterop.cpp`. Each guarded return uses the `scene` pointer
that its condition has already checked. Compiler warnings remain enabled.

The patch applies to upstream commit
`89f4e0df357c7431edb8ab3f80b9b9dd3d3904b2`. The upstream source retains its
[MIT license](../../../docs/licenses/fetched/rive-sharp-MIT.txt).

`Tools/Build-Submodules.bat` checks and applies this patch before the native
Rive build. It accepts an already applied patch. If neither check succeeds,
the build stops for manual review. It does not reset local changes or change
the submodule revision. The patched source remains modified in the submodule
working tree.

Review this patch when you update the RiveSharp submodule. Remove the patch
and its build step when upstream contains the fix.
