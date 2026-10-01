# Dependency Inventory

Generated: 2026-09-30T14:44:02-07:00
Commit: 00233106ac1abcae4eca70357051182da04ae47b

Best-effort inventory of dependencies referenced by the XRENGINE solution: NuGet packages, git submodules, vendored source snapshots, and native/managed binaries that are referenced or shipped.

Notes:
- `Owner` is derived from a GitHub repository URL when available, otherwise from the NuGet nuspec `authors` field (best-effort).
- This lists direct `PackageReference`s from solution projects, not all transitive dependencies.
- NVIDIA proprietary SDK binaries (DLSS/NGX, Reflex, Streamline) are **not redistributed** and are expected to be provided by end users via `ThirdParty/NVIDIA/SDK/win-x64/`.
- Manual unknown-license resolutions are loaded from `docs/dependency-license-overrides.json`.
- Browser Jolt source entries were reconciled on 2026-10-01 from `Tools/Dependencies/JoltBrowser.lock.json`; a full refresh was interrupted during remote metadata lookup, so unrelated prior metadata is retained.
- Playwright and Slang tool-only entries were reconciled on 2026-10-01 from the exact installed official packages and license files. A subsequent full generator run was blocked on an unintended Visual Studio telemetry connection; it is not recorded as a successful refresh.
- Prompt mode for unknown licenses: False (use -PromptForUnknownLicenses or -NoPromptForUnknownLicenses to override).

## Git submodules / vendored submodules
| Name | Path | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CoACD | Build/Submodules/CoACD | SarahWeiii | [MIT](licenses/submodules/CoACD-MIT.txt) | https://github.com/SarahWeiii/CoACD |
| monado | Build/Submodules/monado | BlackJaxDev | [LICENSE](licenses/submodules/monado-LICENSE.txt) | https://github.com/BlackJaxDev/Monado.git |
| OpenVR.NET | Build/Submodules/OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | https://github.com/BlackJaxDev/OpenVR.NET.git |
| OscCore-NET9 | Build/Submodules/OscCore-NET9 | stella3d + BlackJaxDev (modifications) | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | https://github.com/BlackJaxDev/OscCore-NET9.git |
| rive-sharp | Build/Submodules/rive-sharp | Rive (rive-app) | [MIT](licenses/fetched/rive-sharp-MIT.txt) | https://github.com/rive-app/rive-sharp.git |

## Nested / fetched / vendored-source dependencies
| Name | Used by | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CDT | CoACD | artem-ogre | [MPL-2.0](licenses/github/CDT-MPL-2.0.txt) | https://github.com/artem-ogre/CDT |
| fastgltf v0.9.0 | FastGltfBridge | Sean Apeler | [MIT](licenses/nested/fastgltf v0.9.0-MIT.md) | https://github.com/spnda/fastgltf/tree/v0.9.0 |
| Jolt v5.6.0 (browser static source) | Jolt browser native archives | Jorrit Rouwe | [MIT](licenses/nested/Jolt%20v5.6.0%20%28browser%20static%20source%29-MIT.txt) | https://github.com/jrouwe/JoltPhysics/tree/e77f175595e64cb44218cc9d9d56fc365ad0e36a |
| joltc (browser static source) | Jolt browser native archives | Amer Koleci and Contributors | [MIT](licenses/nested/joltc%20%28browser%20static%20source%29-MIT.txt) | https://github.com/amerkoleci/joltc/tree/886e088675bae3a086f8318c7803f8ee962c2f2c |
| JoltPhysicsSharp 2.22.0 (browser source) | Jolt browser managed binding | Amer Koleci and Contributors | [MIT](licenses/nested/JoltPhysicsSharp%202.22.0%20%28browser%20source%29-MIT.txt) | https://github.com/amerkoleci/JoltPhysicsSharp/tree/77a5be2dd30d587c1981dfcaf15851f18041b39c |
| KaTeX v0.18.4 (including fonts and mhchem) | LocalAgentBroker.Tray | Khan Academy and contributors | [MIT AND Apache-2.0](licenses/nested/KaTeX v0.18.4 (including fonts and mhchem)-MIT AND Apache-2.0.txt) | https://github.com/KaTeX/KaTeX/tree/v0.18.4 |
| markdown-it v15.0.0 (browser bundle) | LocalAgentBroker.Tray | Vitaly Puzrin, Alex Kocharin and contributors | [MIT AND BSD-2-Clause](licenses/nested/markdown-it v15.0.0 (browser bundle)-MIT AND BSD-2-Clause.txt) | https://github.com/markdown-it/markdown-it/tree/15.0.0 |
| Playwright 1.63.0 (browser validation tooling) | Tools/BrowserSmoke; development and CI only | Microsoft Corporation | [Apache-2.0](licenses/Playwright-1.63.0.txt) | https://github.com/microsoft/playwright/tree/v1.63.0 |
| Slang 2026.8 (shader compiler tooling) | Tools/ShaderCooker; cook time only | Slang project contributors | [Apache-2.0 WITH LLVM-exception](licenses/Slang-2026.8.txt) | https://github.com/shader-slang/slang/tree/v2026.8 |
| simdjson v3.12.3 | FastGltfBridge | simdjson authors | [Apache-2.0](licenses/nested/simdjson v3.12.3-Apache-2.0.txt) | https://github.com/simdjson/simdjson/tree/v3.12.3 |
| Vulkan Memory Allocator v3.3.0 | VulkanMemoryAllocatorBridge | Advanced Micro Devices, Inc. (GPUOpen) | [MIT](licenses/nested/Vulkan Memory Allocator v3.3.0-MIT.txt) | https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/tree/v3.3.0 |

## NuGet packages (direct)
| Package | Version(s) | Owner (best-effort) | License (best-effort) | Used by |
|---|---|---|---|---|
| AssimpNetter | 6.0.5 | Saalvage | [MIT](licenses/nuget/AssimpNetter-6.0.5-MIT.txt) | XREngine.Runtime.ModelAssetPipeline.csproj |
| BenchmarkDotNet | 0.15.8 | dotnet | [MIT](licenses/nuget/BenchmarkDotNet-0.15.8-MIT.txt) | XREngine.Benchmarks.csproj |
| BitsKit | 1.2.0 | barncastle | [MIT](licenses/nuget/BitsKit-1.2.0-MIT.txt) | XREngine.Runtime.Core.csproj |
| DotnetNoise | 1.0.0 | Mr9Madness | [MIT](licenses/nuget/DotnetNoise-1.0.0-MIT.txt) | XREngine.Runtime.Core.csproj |
| DXNET.XInput | 5.0.0 | lepoco | [MIT](licenses/nuget/DXNET.XInput-5.0.0-MIT.txt) | XREngine.Input.XInput.csproj |
| FFmpeg.AutoGen | 8.1.0 | Ruslan-B | [MIT](licenses/nuget/FFmpeg.AutoGen-8.1.0-MIT.txt) | XREngine.Runtime.Media.FFmpeg.csproj |
| ImGui.NET | 1.91.6.1 | mellinoe | [MIT](licenses/nuget/ImGui.NET-1.91.6.1-MIT.txt) | XREngine.Runtime.Rendering.ImGui.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| ImmediateReflection | 2.0.0 | KeRNeLith | [MIT](licenses/nuget/ImmediateReflection-2.0.0-MIT.txt) | XREngine.Animation.csproj |
| Jitter2 | 2.8.9 | notgiven688 | [MIT](licenses/nuget/Jitter2-2.8.9-MIT.txt) | XREngine.Runtime.Physics.Jitter.csproj |
| JoltPhysicsSharp | 2.22.0 | amerkoleci | [MIT](licenses/nuget/JoltPhysicsSharp-2.22.0-MIT.txt) | XREngine.Runtime.Physics.Jolt.csproj |
| K4os.Compression.LZ4 | 1.3.8 | MiloszKrajewski | [MIT](licenses/nuget/K4os.Compression.LZ4-1.3.8-MIT.txt) | XREngine.Data.csproj |
| LZMA-SDK | 22.1.1 | monemihir | [MIT](licenses/nuget/LZMA-SDK-22.1.1-MIT.txt) | XREngine.Data.csproj |
| Magick.NET-Q16-HDRI-x64 | 14.16.0 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET-Q16-HDRI-x64-14.16.0-Apache-2.0.txt) | XREngine.Runtime.Imaging.Magick.csproj, XREngine.UnitTests.csproj |
| MagicPhysX | 1.0.0 | Cysharp | [MIT](licenses/nuget/MagicPhysX-1.0.0-MIT.txt) | XREngine.Runtime.Physics.PhysX.csproj |
| MathNet.Numerics | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| MemoryPack | 1.21.4 | Cysharp | [MIT](licenses/nuget/MemoryPack-1.21.4-MIT.txt) | XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.Host.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Server.csproj |
| Meshoptimizer.NET | 1.0.7 | BoyBaykiller | [MIT](licenses/nuget/Meshoptimizer.NET-1.0.7-MIT.txt) | XREngine.Runtime.MeshProcessing.Meshoptimizer.csproj |
| MIConvexHull | 1.1.19.1019 | DesignEngrLab | [MIT](licenses/nuget/MIConvexHull-1.1.19.1019-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| Microsoft.Build | 18.8.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build-18.8.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Build.Framework | 18.8.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build.Framework-18.8.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.NET.Test.Sdk | 18.8.1 | microsoft | [MIT](licenses/nuget/Microsoft.NET.Test.Sdk-18.8.1-MIT.txt) | XREngine.UnitTests.csproj |
| Microsoft.Web.WebView2 | 1.0.4129.50 | Microsoft | [LICENSE.txt](licenses/nuget/Microsoft.Web.WebView2-1.0.4129.50-LICENSE.txt.txt) | LocalAgentBroker.Tray.csproj |
| NAudio | 2.3.0 | naudio | [MIT](licenses/nuget/NAudio-2.3.0-MIT.txt) | XREngine.Audio.NAudio.csproj |
| NAudio.Sdl2 | 2.2.6 | alextnull | [MIT](licenses/nuget/NAudio.Sdl2-2.2.6-MIT.txt) | XREngine.Audio.NAudio.csproj |
| NDILibDotNetCoreBase | 2024.7.22.1 | eliaspuurunen | [MIT](licenses/nuget/NDILibDotNetCoreBase-2024.7.22.1-MIT.txt) | XREngine.Editor.csproj, XREngine.VRClient.csproj |
| Newtonsoft.Json | 13.0.4 | JamesNK | [MIT](licenses/nuget/Newtonsoft.Json-13.0.4-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Bootstrap.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.Host.csproj, XREngine.Server.csproj |
| NUnit | 4.6.1 | nunit | [MIT](licenses/nuget/NUnit-4.6.1-MIT.txt) | XREngine.UnitTests.csproj |
| NUnit3TestAdapter | 6.2.0 | nunit | [MIT](licenses/nuget/NUnit3TestAdapter-6.2.0-MIT.txt) | XREngine.UnitTests.csproj |
| NVorbis | 0.10.5 | NVorbis | [MIT](licenses/nuget/NVorbis-0.10.5-MIT.txt) | XREngine.Data.csproj |
| SharpCompress | 0.50.3 | adamhathcock | [MIT](licenses/nuget/SharpCompress-0.50.3-MIT.txt) | XREngine.Editor.csproj |
| SharpFont.Dependencies | 2.6.0 | Robmaister | [FreeType License (FTL)](licenses/nuget/SharpFont.Dependencies-2.6.0-FreeType License (FTL).txt) | XREngine.Runtime.Text.FreeType.csproj |
| SharpFont.NetStandard | 1.0.5 | vonderborch | [MIT](licenses/nuget/SharpFont.NetStandard-1.0.5-MIT.txt) | XREngine.Runtime.Text.FreeType.csproj |
| SharpZipLib | 1.4.2 | icsharpcode | [MIT](licenses/nuget/SharpZipLib-1.4.2-MIT.txt) | XREngine.Data.csproj |
| Shouldly | 4.3.0 | shouldly | [BSD-3-Clause](licenses/nuget/Shouldly-4.3.0-BSD-3-Clause.txt) | XREngine.UnitTests.csproj |
| Silk.NET | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Core | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.IO.DirectStorage.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Core.Win32Extras | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core.Win32Extras-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.DirectStorage | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.DirectStorage-2.23.0-MIT.txt) | XREngine.Runtime.IO.DirectStorage.csproj |
| Silk.NET.DirectStorage.Native | 1.3.0 | microsoft | [LICENSE.txt](licenses/nuget/Silk.NET.DirectStorage.Native-1.3.0-LICENSE.txt.txt) | XREngine.Runtime.IO.DirectStorage.csproj |
| Silk.NET.GLFW | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.GLFW-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Input | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.Silk.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Platform.Desktop.csproj |
| Silk.NET.Input.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Common-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Input.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Extensions-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Input.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Glfw-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.Silk.csproj |
| Silk.NET.Input.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Sdl-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Platform.Desktop.csproj |
| Silk.NET.Maths | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Maths-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenAL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL-2.23.0-MIT.txt) | XREngine.Audio.OpenAL.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenAL.Extensions.Creative | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Creative-2.23.0-MIT.txt) | XREngine.Audio.OpenAL.csproj |
| Silk.NET.OpenAL.Extensions.Enumeration | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Enumeration-2.23.0-MIT.txt) | XREngine.Audio.OpenAL.csproj |
| Silk.NET.OpenAL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Audio.OpenAL.csproj |
| Silk.NET.OpenAL.Extensions.Soft | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Soft-2.23.0-MIT.txt) | XREngine.Audio.OpenAL.csproj |
| Silk.NET.OpenAL.Soft.Native | 1.23.1 | kcat | [LGPL-2.0-or-later](licenses/nuget/Silk.NET.OpenAL.Soft.Native-1.23.1-LGPL-2.0-or-later.txt) | XREngine.Audio.OpenAL.csproj |
| Silk.NET.OpenGL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL-2.23.0-MIT.txt) | XREngine.Benchmarks.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenGL.Extensions.AMD | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.AMD-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.ARB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ARB-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.ImGui | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ImGui-2.23.0-MIT.txt) | XREngine.Profiler.csproj, XREngine.Profiler.UI.csproj |
| Silk.NET.OpenGL.Extensions.INTEL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.INTEL-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.KHR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.NV-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.OVR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.OVR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGLES.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGLES.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.NV-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenXR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj, XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.HTC | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTC-2.23.0-MIT.txt) | XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.HTCX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTCX-2.23.0-MIT.txt) | XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.KHR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj, XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.MSFT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.MSFT-2.23.0-MIT.txt) | XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.OpenXR.Extensions.VALVE | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.VALVE-2.23.0-MIT.txt) | XREngine.Runtime.XR.OpenXR.csproj |
| Silk.NET.SDL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.SDL-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Shaderc | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Shaderc-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan-2.23.0-MIT.txt) | XREngine.Runtime.Platform.Desktop.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.AMD | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.AMD-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.ARM | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.ARM-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.FB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.FB-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.HUAWEI | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.HUAWEI-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.INTEL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.INTEL-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.KHR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NV-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.NVX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NVX-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.QNX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.QNX-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Extensions.VALVE | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.VALVE-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan.Loader.Native | 2025.9.12 | KhronosGroup | [Apache-2.0](licenses/nuget/Silk.NET.Vulkan.Loader.Native-2025.9.12-Apache-2.0.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.WGL.Extensions.ARB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.WGL.Extensions.ARB-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.Windowing | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing-2.23.0-MIT.txt) | XREngine.Benchmarks.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Platform.Desktop.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Windowing.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Common-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Windowing.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Extensions-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Windowing.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Glfw-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| Silk.NET.Windowing.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Sdl-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Platform.Desktop.csproj |
| Silk.NET.XInput | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.XInput-2.23.0-MIT.txt) | XREngine.Editor.csproj |
| SkiaSharp | 4.151.0 | Microsoft | [MIT](licenses/nuget/SkiaSharp-4.151.0-MIT.txt) | XREngine.Runtime.UI.Rive.csproj, XREngine.Runtime.UI.Skia.csproj |
| SPIRVCross.NET | 1.1.3 | FaberSanZ | [MIT](licenses/nuget/SPIRVCross.NET-1.1.3-MIT.txt) | XREngine.Editor.csproj |
| Steamworks.NET | 2024.8.0 | rlabrecque | [MIT](licenses/nuget/Steamworks.NET-2024.8.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Server.csproj |
| Svg.Skia | 5.1.1 | wieslawsoltes | [MIT](licenses/nuget/Svg.Skia-5.1.1-MIT.txt) | XREngine.Runtime.UI.Skia.csproj |
| System.IO.Hashing | 10.0.10 | dotnet | [MIT](licenses/nuget/System.IO.Hashing-10.0.10-MIT.txt) | XREngine.Data.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj |
| System.Management | 10.0.10 | dotnet | [MIT](licenses/nuget/System.Management-10.0.10-MIT.txt) | XREngine.Runtime.Diagnostics.Desktop.csproj |
| System.Security.Cryptography.ProtectedData | 10.0.10 | dotnet | [MIT](licenses/nuget/System.Security.Cryptography.ProtectedData-10.0.10-MIT.txt) | XREngine.ControlPlane.Service.csproj, XREngine.Editor.csproj |
| UltralightNet | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet-1.3.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.UI.Ultralight.csproj |
| UltralightNet.AppCore | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet.AppCore-1.3.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.UI.Ultralight.csproj |
| YamlDotNet | 18.1.0 | aaubry | [MIT](licenses/nuget/YamlDotNet-18.1.0-MIT.txt) | XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.Host.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj |
| ZstdSharp.Port | 0.8.8 | oleg-st | [MIT](licenses/nuget/ZstdSharp.Port-0.8.8-MIT.txt) | XREngine.Data.csproj |

## Explicit assembly references (`<Reference>` )
| Project | Reference | Owner (best-effort) | License (best-effort) | HintPath |
|---|---|---|---|---|
| XREngine.Runtime.UI.Rive.csproj | RiveSharp | Rive (rive-app) | [MIT](licenses/fetched/RiveSharp-MIT.txt) | $(RiveSharpManagedDll) |

## Referenced binaries via project items (dll/exe)
| Project | Path/Update | Owner (best-effort) | License (best-effort) | Link | CopyToOutputDirectory |
|---|---|---|---|---|---|
| OpenVR.NET.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [(unknown)](licenses/unknown/binary-item-OpenVR.NET.csproj-openvr_api.dll.txt) |  | Always |
| XREngine.Audio.Audio2Face.csproj | $(Audio2XBridgeNativeOutputDir)*.dll | (unknown) | [(unknown)](licenses/unknown/binary-item-XREngine.Audio.Audio2Face.csproj-$(Audio2XBridgeNativeOutputDir)_.dll.txt) | %(Filename)%(Extension) | PreserveNewest |
| XREngine.Audio.OVRLipSync.csproj | $(MetaOvrLipSyncWinX64Dir)OVRLipSync.dll | Meta Platforms, Inc. | [Proprietary (Oculus SDK License Agreement)](licenses/fetched/OVRLipSync-Proprietary (Oculus SDK License Agreement).txt) | OVRLipSync.dll | PreserveNewest |
| XREngine.Audio.SteamAudio.csproj | ..\XREngine.Audio\runtimes\win-x64\native\phonon.dll | Valve (Steam Audio) | [Apache-2.0](https://raw.githubusercontent.com/ValveSoftware/steam-audio/master/LICENSE.md) | phonon.dll | PreserveNewest |
| XREngine.Editor.csproj | %ProgramFiles(x86)%\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll | Khronos Group (OpenXR loader), distributed via Valve/SteamVR | [Apache-2.0](https://github.com/KhronosGroup/OpenXR-SDK-Source/blob/master/LICENSE) | openxr_loader.dll | PreserveNewest |
| XREngine.Gltf.csproj | runtimes\win-x64\native\FastGltfBridge.Native.dll | Sean Apeler (fastgltf) / simdjson authors | [MIT (fastgltf) + Apache-2.0 (simdjson)](licenses/notes/binary-item-XREngine.Gltf.csproj-FastGltfBridge.Native.dll.txt) |  | PreserveNewest |
| XREngine.Runtime.Media.FFmpeg.csproj | ..\Build\Dependencies\FFmpeg\HlsReference\win-x64\*.dll | FFmpeg Project | [LGPL-2.1-or-later](licenses/fetched/win-x64-LGPL-2.1-or-later.txt) | %(Filename)%(Extension) | PreserveNewest |
| XREngine.Runtime.Physics.Authoring.csproj | ../XREngine.Runtime.Core/runtimes/win-x64/native/lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |  |  |
| XREngine.Runtime.Physics.PhysX.csproj | runtimes/win-x64/native/libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](licenses/fetched/libmagicphysx-MIT (MagicPhysX) + NVIDIA PhysX 5 license.txt) |  |  |
| XREngine.Runtime.Rendering.OpenGL.csproj | $(NvidiaRtxgiWinX64Dir)RestirGI.Native.dll | NVIDIA Corporation | [Proprietary (NVIDIA RTXGI SDK License)](https://developer.nvidia.com/rtxgi) | RestirGI.Native.dll | Always |
| XREngine.Runtime.UI.Rive.csproj | ..\XREngine.Runtime.Rendering\runtimes\win-x64\native\rive.dll | Rive | [MIT](licenses/fetched/rive-MIT.txt) | runtimes\win-x64\native\rive.dll | PreserveNewest |
| XREngine.Runtime.XR.OpenVR.csproj | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](licenses/fetched/openvr_api-BSD-3-Clause.txt) | openvr_api.dll | PreserveNewest |
| XREngine.VRClient.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](licenses/fetched/openvr_api-BSD-3-Clause.txt) |  | PreserveNewest |

## Checked-in native/managed binaries (filesystem)
| Path | File | Likely upstream/owner | License (best-effort) |
|---|---|---|---|
| XREngine.Audio/runtimes/win-x64/native/phonon.dll | phonon.dll | (unknown) | [(unknown)](licenses/unknown/checked-binary-phonon.dll.txt) |
| XREngine.Gltf/runtimes/win-x64/native/FastGltfBridge.Native.dll | FastGltfBridge.Native.dll | Sean Apeler (fastgltf) / simdjson authors | [MIT (fastgltf) + Apache-2.0 (simdjson)](licenses/notes/binary-item-XREngine.Gltf.csproj-FastGltfBridge.Native.dll.txt) |
| XREngine.Runtime.Core/runtimes/win-x64/native/lib_coacd.dll | lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |
| XREngine.Runtime.Physics.PhysX/runtimes/win-x64/native/libmagicphysx.dll | libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](licenses/fetched/libmagicphysx-MIT (MagicPhysX) + NVIDIA PhysX 5 license.txt) |
| XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/VulkanMemoryAllocatorBridge.Native.dll | VulkanMemoryAllocatorBridge.Native.dll | Advanced Micro Devices, Inc. (GPUOpen) | [MIT (Vulkan Memory Allocator)](../Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/LICENSE.txt) |
| XREngine.Runtime.Rendering/runtimes/win-x64/native/VulkanMemoryAllocatorBridge.Native.dll | VulkanMemoryAllocatorBridge.Native.dll | Advanced Micro Devices, Inc. (GPUOpen) | [MIT (Vulkan Memory Allocator)](../Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/LICENSE.txt) |
