# Vulkan VMA P/Invoke Allocator Todo

Current status: `EVulkanAllocatorBackend.Vma` is selectable through `VulkanRobustnessSettings.AllocatorBackend` and is the default Vulkan allocator backend. The engine builds a Windows x64 native bridge, loads VMA through P/Invoke, and preserves `Legacy` and `Managed` as explicit alternatives.

References:

- VMA repository: <https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator>
- VMA docs: <https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/>
- VMA product page: <https://gpuopen.com/vulkan-memory-allocator/>

## Goals

- Add a direct VMA-backed Vulkan allocator backend for Windows x64.
- Keep the engine-facing allocator contract stable where practical.
- Make VMA failures visible and diagnostic.
- Preserve `Legacy` and `Managed` backends for comparison and fallback by explicit user selection.
- Share diagnostics and allocation metadata with the managed allocator path.

## Non-Goals

- Do not silently fall back from `Vma` to `Managed` or `Legacy`.
- Do not expose raw VMA handles to general engine code outside the Vulkan backend.
- Do not add or bump submodules without owner approval.

## Phase 0 - Dependency Decision

- [x] Decide whether to vendor `vk_mem_alloc.h`, add VMA as a submodule under `Build/Submodules`, or consume it through a native build package.
- [x] Confirm MIT license compatibility for open-source and commercial use.
- [x] If adding or changing dependency supply, run `pwsh Tools/Reports/Generate-Dependencies.ps1` or Windows PowerShell when `pwsh` is unavailable.
- [x] Include updated `docs/DEPENDENCIES.md` and `docs/licenses/`.
- [x] Document the selected VMA version and update policy.

Selected supply: vendored `vk_mem_alloc.h` from VMA v3.3.0 under `Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator`. `Tools/Dependencies/Get-VulkanMemoryAllocator.ps1` retrieves the pinned header and license from GPUOpen. Update by running that script with a new `-Version`, rebuilding the native bridge, and regenerating dependency docs.

## Phase 1 - Native Wrapper

- [x] Create a small C++ wrapper DLL with `extern "C"` exports.
- [x] Compile VMA in exactly one translation unit with `VMA_IMPLEMENTATION`.
- [x] Export allocator lifecycle functions: create and destroy.
- [x] Export allocate/free functions for existing engine flow: allocate for buffer, allocate for image, bind buffer, bind image.
- [x] Export optional combined create functions only if they simplify call sites without hiding Vulkan errors.
- [x] Export map, unmap, flush, invalidate, stats, and JSON dump functions.
- [x] Return Vulkan-compatible `VkResult` values and explicit error payloads.

## Phase 2 - Function Pointer And Device Integration

- [x] Pass instance, physical device, logical device, Vulkan API version, and enabled-extension state to the wrapper.
- [x] Provide Vulkan functions through the Windows Vulkan loader link used by the native bridge.
- [x] Enable VMA flags for supported buffer device address allocations.
- [ ] Enable VMA flags for optional memory budget, external memory Win32, and memory priority when available.
- [x] Ensure wrapper creation fails with a clear reason when required Vulkan handles or functions are unavailable.

## Phase 3 - Managed P/Invoke Layer

- [x] Add `VulkanVmaNative` P/Invoke declarations.
- [x] Add safe handle or explicit lifetime wrappers for `VmaAllocator` and `VmaAllocation`.
- [x] Add `VulkanVmaAllocator : IVulkanMemoryAllocator`.
- [x] Extend `VulkanMemoryAllocation` or add side-table tracking so `Free()` can pass the matching VMA allocation handle back to native code.
- [x] Keep `DeviceMemory`, offset, size, memory type, flags, and block identity available for existing bind/map/diagnostic code.
- [ ] Add allocation debug names and owner metadata to native VMA allocations.

## Phase 4 - Backend Selection

- [x] Add `EVulkanAllocatorBackend.Vma`.
- [x] Expose `Vma` through `VulkanRobustnessSettings.AllocatorBackend`.
- [x] Make `Vma` startup fail visibly until the wrapper is implemented.
- [x] Replace the temporary startup failure with `new VulkanVmaAllocator(...)`.
- [x] Log selected VMA version, enabled VMA feature flags, and native DLL path.
- [x] Add a startup diagnostic if the VMA DLL is missing, wrong architecture, or incompatible.

## Phase 5 - Resource Semantics

- [x] Support ordinary buffer/image allocations.
- [x] Support device-address buffers without bypassing the allocator.
- [x] Support host-visible upload and readback allocations.
- [x] Support persistently mapped allocations for rings and dynamic uniforms.
- [ ] Support external-memory allocations required by Vulkan/OpenGL interop paths.
- [ ] Preserve no-silent-CPU-fallback behavior for explicitly requested accelerated paths.

## Phase 6 - Packaging And Tooling

- [x] Add native project build to Windows build orchestration.
- [x] Copy the wrapper DLL to managed runtime output directories.
- [x] Add VS Code task or ExecTool entry if native wrapper build is not covered by normal `dotnet build`.
- [x] Add a first-checkout VMA fetch script and direct native bridge build script.
- [ ] Add CI/build validation for Debug and Release x64.
- [x] Document how to diagnose missing native VMA wrapper deployment.

## Phase 7 - Diagnostics

- [x] Surface VMA allocation counters in existing Vulkan allocator diagnostics.
- [ ] Add VMA JSON dump capture to logs or an explicit debug command.
- [ ] Map VMA memory heap/type stats into existing allocator diagnostics.
- [ ] Report allocation name, owner, heap, type, size, offset, dedicated/suballocated placement, and VMA allocation handle.
- [ ] Add warnings when VMA rejects a placement because of memory budget, unsupported flags, or incompatible memory type.

## Phase 8 - Validation

- [ ] Add native smoke tests that load the wrapper DLL and query exported version info.
- [ ] Add managed smoke tests for P/Invoke signatures.
- [x] Add Vulkan allocator source-contract tests for the `Vma` switch arm.
- [ ] Run Vulkan buffer parity tests with `Managed`.
- [ ] Run Vulkan buffer parity tests with `Vma` on hardware.
- [ ] Run editor startup in Vulkan mode with `Legacy`, `Managed`, and `Vma`.
- [ ] Capture allocator stats before and after creating/destroying staging buffers, texture images, render targets, and scene-database buffers.
