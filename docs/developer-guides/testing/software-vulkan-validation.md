# Software Vulkan correctness validation

The standalone `XREngine.UnitTests/SoftwareVulkan` executable exercises the production
`VulkanExplicitTargetRendererHost` without a window, display server, or XR runtime.
It is a correctness lane, not a CPU renderer implementation or a performance benchmark.

## Requirements and run

- .NET 10 SDK and the normal repository submodules/dependencies
- Vulkan 1.4 loader and an explicitly selected CPU ICD, such as Mesa lavapipe
- A shaderc native library that recognizes the Vulkan 1.4 target and SPIR-V 1.6
- Khronos validation layers with synchronization validation for a complete pass

From the repository root:

```sh
dotnet build XREngine.UnitTests/SoftwareVulkan/XREngine.SoftwareVulkanValidation.csproj \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:XREngineUseExistingNativeBridges=true
dotnet XREngine.UnitTests/SoftwareVulkan/bin/Debug/net10.0-windows7.0/XREngine.SoftwareVulkanValidation.dll \
  --icd /path/to/lvp_icd.json
```

The Vulkan leaf retains its existing Windows-targeted framework declaration, but this
presentationless executable runs on Linux with the listed native dependencies. This
is not general Linux editor support. A case-sensitive checkout must use the canonical
`XREngine.Runtime.Core` directory casing.

For locally extracted native packages, set `LD_LIBRARY_PATH` to their native library
directory before starting `dotnet`, and `VK_LAYER_PATH` to their `explicit_layer.d`
directory. For an execution-only compiler override, place a `libshaderc_shared.so`
symlink to the compatible `libshaderc.so.1` in that native directory. Do not replace
NuGet-cache binaries. The output records the shaderc module actually loaded.

The bundled Silk.NET.Shaderc.Native 2.23.0 Linux library failed the Vulkan 1.4 target
in the measured environment: its optimizer interpreted the target as Vulkan 1.0 and
rejected SPIR-V 1.6. A passing run used the official Debian `libshaderc1` 2025.2-1
execution-only override. The baseline dependency is therefore **not qualified** by
that pass. Do not lower the engine shader target to hide this failure.

`--icd` sets both loader selection variables before loading Vulkan, including the
native libc environment on Linux. The selected adapter must report Vulkan device
type `Cpu`; hardware adapters are rejected. No automatic hardware/software fallback
occurs. Only pass a trusted ICD manifest because it selects a native driver library.

## Coverage and results

Each run emits JSON check results, adapter identity, logical-device extensions,
shader compiler library paths, and native validation diagnostics.

- Explicit composition/reset/stale-scope disposal and unavailable-capability checks
- Five alternating clear/readback frames across two frame slots, checking every byte
- Three fullscreen shader frames using the existing RenderBench fixture, checking every pixel
- A real compute dispatch with sentinel initialization, storage-buffer synchronization,
  completion wait, coherent/non-coherent memory handling, and verification of all 64 words
- Requested standard and synchronization validation with an active debug messenger

Exit codes: `0` all checks passed; `1` failure; `2` invalid arguments;
`3` unavailable/skipped capability. Missing dynamic rendering, a non-compute submission
queue, or absent validation coverage must not count as a full pass. Shader compiler
failures remain failures. Validation snapshots cover initialization and submitted
work before host teardown; the suite does not claim teardown-message coverage.

This does not validate real-GPU performance, vendor extensions, desktop presentation,
OpenXR, the full scene pipeline, or visual quality across hardware drivers.

## Composition contract

Configure `RuntimeWorkScheduler` first, then use
`RuntimeRenderingHostServices.InstallPresentationless()` at the composition root.
The scope installs inactive desktop/XR policy and an adapter to the existing process
scheduler. It does not install asset IO, window factories, or an interactive
render-thread scheduler; those capabilities still fail fast.

Create/dispose scopes on the composition thread, outside renderer lifetime. Dispose
the Vulkan renderer before the scope and shut down the owned scheduler last. Nested
presentationless scopes and scoped composite-host installation while one is active
are rejected. `Reset()` removes the explicit capabilities; disposing a stale scope
cannot remove a newer installation. Direct composite-host replacement replaces the
presentationless installation rather than preserving a hidden nested host.

## Measured software configuration

The September 30, 2026 Linux run used Mesa `mesa-vulkan-drivers` 25.0.7-2+deb13u1
(lavapipe reporting llvmpipe, LLVM 19.1.7), `vulkan-validationlayers` 1.4.309.0-1,
and the explicit shaderc override above. All output checks passed with standard and
synchronization validation enabled, zero native validation errors and warnings.
These system-level validation dependencies were extracted only for the run; no
repository package version was changed.
