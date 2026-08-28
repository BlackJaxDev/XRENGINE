# Dependency Inventory

Generated: 2026-08-27T22:16:17-07:00
Commit: (not a git repo)

Best-effort inventory of dependencies referenced by the XRENGINE solution: NuGet packages, git submodules, vendored source snapshots, and native/managed binaries that are referenced or shipped.

Notes:
- `Owner` is derived from a GitHub repository URL when available, otherwise from the NuGet nuspec `authors` field (best-effort).
- This lists direct `PackageReference`s from solution projects, not all transitive dependencies.
- NVIDIA proprietary SDK binaries (DLSS/NGX, Reflex, Streamline) are **not redistributed** and are expected to be provided by end users via `ThirdParty/NVIDIA/SDK/win-x64/`.
- Manual unknown-license resolutions are loaded from `docs/dependency-license-overrides.json`.
- Prompt mode for unknown licenses: False (use -PromptForUnknownLicenses or -NoPromptForUnknownLicenses to override).

## Git submodules / vendored submodules
| Name | Path | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CoACD | Build/Submodules/CoACD | SarahWeiii | [MIT](licenses/submodules/CoACD-MIT.txt) | https://github.com/SarahWeiii/CoACD |
| Flyleaf | Build/Submodules/Flyleaf | (unknown) | [LGPL-3.0](licenses/submodules/Flyleaf-LGPL-3.0.txt) | (not detected) |
| monado | Build/Submodules/monado | BlackJaxDev | [LICENSE](licenses/submodules/monado-LICENSE.txt) | https://github.com/BlackJaxDev/Monado.git |
| OpenVR.NET | Build/Submodules/OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | https://github.com/BlackJaxDev/OpenVR.NET.git |
| OscCore-NET9 | Build/Submodules/OscCore-NET9 | stella3d + BlackJaxDev (modifications) | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | https://github.com/BlackJaxDev/OscCore-NET9.git |
| rive-sharp | Build/Submodules/rive-sharp | Rive (rive-app) | [MIT](licenses/fetched/rive-sharp-MIT.txt) | https://github.com/rive-app/rive-sharp.git |

## Nested / fetched / vendored-source dependencies
| Name | Used by | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CDT | CoACD | artem-ogre | [MPL-2.0](licenses/github/CDT-MPL-2.0.txt) | https://github.com/artem-ogre/CDT |
| fastgltf v0.9.0 | FastGltfBridge | Sean Apeler | [MIT](licenses/nested/fastgltf v0.9.0-MIT.md) | https://github.com/spnda/fastgltf/tree/v0.9.0 |
| simdjson v3.12.3 | FastGltfBridge | simdjson authors | [Apache-2.0](licenses/nested/simdjson v3.12.3-Apache-2.0.txt) | https://github.com/simdjson/simdjson/tree/v3.12.3 |
| Vulkan Memory Allocator v3.3.0 | VulkanMemoryAllocatorBridge | Advanced Micro Devices, Inc. (GPUOpen) | [MIT](licenses/nested/Vulkan Memory Allocator v3.3.0-MIT.txt) | https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/tree/v3.3.0 |

## NuGet packages (direct)
| Package | Version(s) | Owner (best-effort) | License (best-effort) | Used by |
|---|---|---|---|---|
| AssimpNetter | 6.0.5 | Saalvage | [MIT](licenses/nuget/AssimpNetter-6.0.5-MIT.txt) | XREngine.Runtime.ModelAssetPipeline.csproj |
| BenchmarkDotNet | 0.15.8 | dotnet | [MIT](licenses/nuget/BenchmarkDotNet-0.15.8-MIT.txt) | XREngine.Benchmarks.csproj |
| BitsKit | 1.2.0 | barncastle | [MIT](licenses/nuget/BitsKit-1.2.0-MIT.txt) | XREngine.Runtime.Core.csproj, XREngine.Runtime.Rendering.csproj |
| DotnetNoise | 1.0.0 | Mr9Madness | [MIT](licenses/nuget/DotnetNoise-1.0.0-MIT.txt) | XREngine.Runtime.Core.csproj, XREngine.Runtime.Rendering.csproj |
| DXNET.XInput | 5.0.0 | lepoco | [MIT](licenses/nuget/DXNET.XInput-5.0.0-MIT.txt) | XREngine.Input.csproj |
| FFmpeg.AutoGen | 8.1.0 | Ruslan-B | [MIT](licenses/nuget/FFmpeg.AutoGen-8.1.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Google.Cloud.Speech.V1 | 3.9.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.Speech.V1-3.9.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| Google.Cloud.TextToSpeech.V1 | 3.18.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.TextToSpeech.V1-3.18.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| GraphQL | 8.8.4 | graphql-dotnet | [MIT](licenses/nuget/GraphQL-8.8.4-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| ImGui.NET | 1.91.6.1 | mellinoe | [MIT](licenses/nuget/ImGui.NET-1.91.6.1-MIT.txt) | XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| ImmediateReflection | 2.0.0 | KeRNeLith | [MIT](licenses/nuget/ImmediateReflection-2.0.0-MIT.txt) | XREngine.Animation.csproj, XREngine.Runtime.Rendering.csproj |
| Jitter2 | 2.8.9 | notgiven688 | [MIT](licenses/nuget/Jitter2-2.8.9-MIT.txt) | XREngine.Runtime.Core.csproj |
| JoltPhysicsSharp | 2.22.0 | amerkoleci | [MIT](licenses/nuget/JoltPhysicsSharp-2.22.0-MIT.txt) | XREngine.Runtime.Core.csproj |
| K4os.Compression.LZ4 | 1.3.8 | MiloszKrajewski | [MIT](licenses/nuget/K4os.Compression.LZ4-1.3.8-MIT.txt) | XREngine.Data.csproj |
| LZMA-SDK | 22.1.1 | monemihir | [MIT](licenses/nuget/LZMA-SDK-22.1.1-MIT.txt) | XREngine.Data.csproj |
| Magick.NET-Q16-HDRI-x64 | 14.16.0 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET-Q16-HDRI-x64-14.16.0-Apache-2.0.txt) | XREngine.Runtime.Core.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj, XREngine.UnitTests.csproj |
| MagicPhysX | 1.0.0 | Cysharp | [MIT](licenses/nuget/MagicPhysX-1.0.0-MIT.txt) | XREngine.Runtime.Core.csproj |
| MathNet.Numerics | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| MathNet.Numerics.Providers.CUDA | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics.Providers.CUDA-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| MemoryPack | 1.21.4 | Cysharp | [MIT](licenses/nuget/MemoryPack-1.21.4-MIT.txt) | XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Server.csproj |
| Meshoptimizer.NET | 1.0.7 | BoyBaykiller | [MIT](licenses/nuget/Meshoptimizer.NET-1.0.7-MIT.txt) | XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj, XREngine.UnitTests.csproj |
| MIConvexHull | 1.1.19.1019 | DesignEngrLab | [MIT](licenses/nuget/MIConvexHull-1.1.19.1019-MIT.txt) | XREngine.Modeling.csproj, XREngine.Runtime.Rendering.csproj |
| Microsoft.Build | 18.8.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build-18.8.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Build.Framework | 18.8.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build.Framework-18.8.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Data.Sqlite.Core | 10.0.10 | dotnet | [MIT](licenses/nuget/Microsoft.Data.Sqlite.Core-10.0.10-MIT.txt) | XREngine.Server.csproj |
| Microsoft.NET.Test.Sdk | 18.8.1 | microsoft | [MIT](licenses/nuget/Microsoft.NET.Test.Sdk-18.8.1-MIT.txt) | XREngine.UnitTests.csproj |
| NAudio | 2.3.0 | naudio | [MIT](licenses/nuget/NAudio-2.3.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Data.csproj |
| NAudio.Lame | 2.1.0 | Corey-M | [MIT](licenses/nuget/NAudio.Lame-2.1.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Data.csproj |
| NAudio.Sdl2 | 2.2.6 | alextnull | [MIT](licenses/nuget/NAudio.Sdl2-2.2.6-MIT.txt) | XREngine.Audio.csproj |
| NAudio.Vorbis | 1.5.0 | naudio | [MIT](licenses/nuget/NAudio.Vorbis-1.5.0-MIT.txt) | XREngine.Audio.csproj, XREngine.Data.csproj |
| NDILibDotNetCoreBase | 2024.7.22.1 | eliaspuurunen | [MIT](licenses/nuget/NDILibDotNetCoreBase-2024.7.22.1-MIT.txt) | XREngine.Editor.csproj, XREngine.VRClient.csproj |
| Newtonsoft.Json | 13.0.4 | JamesNK | [MIT](licenses/nuget/Newtonsoft.Json-13.0.4-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Bootstrap.csproj, XREngine.Runtime.Core.csproj, XREngine.Server.csproj |
| NUnit | 4.6.1 | nunit | [MIT](licenses/nuget/NUnit-4.6.1-MIT.txt) | XREngine.UnitTests.csproj |
| NUnit3TestAdapter | 6.2.0 | nunit | [MIT](licenses/nuget/NUnit3TestAdapter-6.2.0-MIT.txt) | XREngine.UnitTests.csproj |
| NVorbis | 0.10.5 | NVorbis | [MIT](licenses/nuget/NVorbis-0.10.5-MIT.txt) | XREngine.Audio.csproj, XREngine.Data.csproj |
| Raylib-cs | 8.0.0 | raylib-cs | [Zlib](licenses/nuget/Raylib-cs-8.0.0-Zlib.txt) | XREngine.Runtime.Rendering.csproj |
| SharpCompress | 0.50.3 | adamhathcock | [MIT](licenses/nuget/SharpCompress-0.50.3-MIT.txt) | XREngine.Editor.csproj |
| SharpFont.Dependencies | 2.6.0 | Robmaister | [FreeType License (FTL)](licenses/nuget/SharpFont.Dependencies-2.6.0-FreeType License (FTL).txt) | XREngine.Runtime.Rendering.csproj |
| SharpFont.NetStandard | 1.0.5 | vonderborch | [MIT](licenses/nuget/SharpFont.NetStandard-1.0.5-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| SharpZipLib | 1.4.2 | icsharpcode | [MIT](licenses/nuget/SharpZipLib-1.4.2-MIT.txt) | XREngine.Data.csproj |
| Shouldly | 4.3.0 | shouldly | [BSD-3-Clause](licenses/nuget/Shouldly-4.3.0-BSD-3-Clause.txt) | XREngine.UnitTests.csproj |
| Silk.NET | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Core | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core-2.23.0-MIT.txt) | XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Core.Win32Extras | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core.Win32Extras-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Direct3D.Compilers | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D.Compilers-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| Silk.NET.Direct3D12 | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D12-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| Silk.NET.DirectStorage | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.DirectStorage-2.23.0-MIT.txt) | XREngine.Data.csproj, XREngine.Runtime.Core.csproj |
| Silk.NET.DirectStorage.Native | 1.3.0 | microsoft | [LICENSE.txt](licenses/nuget/Silk.NET.DirectStorage.Native-1.3.0-LICENSE.txt.txt) | XREngine.Data.csproj, XREngine.Runtime.Core.csproj |
| Silk.NET.GLFW | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.GLFW-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Input | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Input.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Common-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Input.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Extensions-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Input.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Glfw-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.Input.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Sdl-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Maths | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Maths-2.23.0-MIT.txt) | XREngine.UnitTests.csproj |
| Silk.NET.OpenAL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenAL.Extensions.Creative | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Creative-2.23.0-MIT.txt) | XREngine.Audio.csproj |
| Silk.NET.OpenAL.Extensions.Enumeration | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Enumeration-2.23.0-MIT.txt) | XREngine.Audio.csproj |
| Silk.NET.OpenAL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Audio.csproj |
| Silk.NET.OpenAL.Extensions.Soft | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Soft-2.23.0-MIT.txt) | XREngine.Audio.csproj |
| Silk.NET.OpenAL.Soft.Native | 1.23.1 | kcat | [LGPL-2.0-or-later](licenses/nuget/Silk.NET.OpenAL.Soft.Native-1.23.1-LGPL-2.0-or-later.txt) | XREngine.Audio.csproj |
| Silk.NET.OpenGL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL-2.23.0-MIT.txt) | XREngine.Benchmarks.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenGL.Extensions.AMD | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.AMD-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.ARB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ARB-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.ImGui | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ImGui-2.23.0-MIT.txt) | XREngine.Profiler.csproj, XREngine.Profiler.UI.csproj, XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.INTEL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.INTEL-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.KHR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.NV-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGL.Extensions.OVR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.OVR-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGLES.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Data.csproj, XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenGLES.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.NV-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.OpenXR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.OpenXR.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.OpenXR.Extensions.HTC | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTC-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.OpenXR.Extensions.HTCX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTCX-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.OpenXR.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.KHR-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.OpenXR.Extensions.MSFT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.MSFT-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.OpenXR.Extensions.VALVE | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.VALVE-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.SDL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.SDL-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Shaderc | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Shaderc-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
| Silk.NET.Vulkan | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan-2.23.0-MIT.txt) | XREngine.Runtime.Rendering.Vulkan.csproj |
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
| Silk.NET.Windowing | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing-2.23.0-MIT.txt) | XREngine.Benchmarks.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Windowing.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Common-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Windowing.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Extensions-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.Windowing.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Glfw-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj, XREngine.Runtime.Rendering.OpenGL.csproj |
| Silk.NET.Windowing.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Sdl-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| Silk.NET.XInput | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.XInput-2.23.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Runtime.Rendering.csproj |
| SkiaSharp | 4.151.0 | Microsoft | [MIT](licenses/nuget/SkiaSharp-4.151.0-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| SPIRVCross.NET | 1.1.3 | FaberSanZ | [MIT](licenses/nuget/SPIRVCross.NET-1.1.3-MIT.txt) | XREngine.Editor.csproj |
| Steamworks.NET | 2024.8.0 | rlabrecque | [MIT](licenses/nuget/Steamworks.NET-2024.8.0-MIT.txt) | XREngine.Editor.csproj, XREngine.Server.csproj |
| Svg.Skia | 5.1.1 | wieslawsoltes | [MIT](licenses/nuget/Svg.Skia-5.1.1-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| System.Drawing.Common | 10.0.10 | dotnet | [MIT](licenses/nuget/System.Drawing.Common-10.0.10-MIT.txt) | XREngine.Data.csproj |
| System.IdentityModel.Tokens.Jwt | 8.22.0 | AzureAD | [MIT](licenses/nuget/System.IdentityModel.Tokens.Jwt-8.22.0-MIT.txt) | XREngine.Server.csproj |
| System.IO.Hashing | 10.0.10 | dotnet | [MIT](licenses/nuget/System.IO.Hashing-10.0.10-MIT.txt) | XREngine.Data.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj |
| System.Management | 10.0.10 | dotnet | [MIT](licenses/nuget/System.Management-10.0.10-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| System.Security.Cryptography.ProtectedData | 10.0.10 | dotnet | [MIT](licenses/nuget/System.Security.Cryptography.ProtectedData-10.0.10-MIT.txt) | XREngine.Editor.csproj |
| UltralightNet | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet-1.3.0-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| UltralightNet.AppCore | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet.AppCore-1.3.0-MIT.txt) | XREngine.Runtime.Rendering.csproj |
| YamlDotNet | 18.1.0 | aaubry | [MIT](licenses/nuget/YamlDotNet-18.1.0-MIT.txt) | XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Runtime.Core.csproj, XREngine.Runtime.ModelAssetPipeline.csproj, XREngine.Runtime.Rendering.csproj |
| ZstdSharp.Port | 0.8.8 | oleg-st | [MIT](licenses/nuget/ZstdSharp.Port-0.8.8-MIT.txt) | XREngine.Data.csproj |

## Explicit assembly references (`<Reference>` )
| Project | Reference | Owner (best-effort) | License (best-effort) | HintPath |
|---|---|---|---|---|
| XREngine.Editor.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Input.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Runtime.Bootstrap.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Runtime.InputIntegration.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Runtime.Rendering.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Runtime.Rendering.csproj | RiveSharp | Rive (rive-app) | [MIT](licenses/fetched/RiveSharp-MIT.txt) | $(RiveSharpManagedDll) |
| XREngine.Runtime.Rendering.Vulkan.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.UnitTests.csproj | OpenVR.NET | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.VRClient.csproj | OpenVR.NET, Version=0.8.5.0, Culture=neutral, PublicKeyToken=null | Flutterish + BlackJaxDev (modifications) | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |

## Referenced binaries via project items (dll/exe)
| Project | Path/Update | Owner (best-effort) | License (best-effort) | Link | CopyToOutputDirectory |
|---|---|---|---|---|---|
| XREngine.Audio.csproj | runtimes\win-x64\native\phonon.dll | Valve (Steam Audio) | [Apache-2.0](https://raw.githubusercontent.com/ValveSoftware/steam-audio/master/LICENSE.md) | phonon.dll | PreserveNewest |
| XREngine.Editor.csproj | C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll | Khronos Group (OpenXR loader), distributed via Valve/SteamVR | [Apache-2.0](https://github.com/KhronosGroup/OpenXR-SDK-Source/blob/master/LICENSE) | openxr_loader.dll | PreserveNewest |
| XREngine.Gltf.csproj | runtimes\win-x64\native\FastGltfBridge.Native.dll | Sean Apeler (fastgltf) / simdjson authors | [MIT (fastgltf) + Apache-2.0 (simdjson)](licenses/notes/binary-item-XREngine.Gltf.csproj-FastGltfBridge.Native.dll.txt) |  | PreserveNewest |
| XREngine.Input.csproj | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](licenses/fetched/openvr_api-BSD-3-Clause.txt) | openvr_api.dll | PreserveNewest |
| XREngine.Runtime.AudioIntegration.csproj | $(MetaOvrLipSyncWinX64Dir)OVRLipSync.dll | Meta Platforms, Inc. | [Proprietary (Oculus SDK License Agreement)](licenses/fetched/OVRLipSync-Proprietary (Oculus SDK License Agreement).txt) | OVRLipSync.dll | PreserveNewest |
| XREngine.Runtime.Core.csproj | runtimes\win-x64\native\lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |  | PreserveNewest |
| XREngine.Runtime.Core.csproj | runtimes\win-x64\native\libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](licenses/fetched/libmagicphysx-MIT (MagicPhysX) + NVIDIA PhysX 5 license.txt) |  | PreserveNewest |
| XREngine.Runtime.Rendering.csproj | ..\Build\Dependencies\FFmpeg\HlsReference\win-x64\*.dll | FFmpeg Project | [LGPL-2.1-or-later](licenses/fetched/win-x64-LGPL-2.1-or-later.txt) | %(Filename)%(Extension) | PreserveNewest |
| XREngine.Runtime.Rendering.csproj | $(NvidiaRtxgiWinX64Dir)RestirGI.Native.dll | NVIDIA Corporation | [Proprietary (NVIDIA RTXGI SDK License)](https://developer.nvidia.com/rtxgi) | RestirGI.Native.dll | Always |
| XREngine.Runtime.Rendering.csproj | runtimes\win-x64\native\rive.dll | Rive | [MIT](licenses/fetched/rive-MIT.txt) |  | PreserveNewest |
| XREngine.Runtime.Rendering.Vulkan.csproj | runtimes\win-x64\native\VulkanMemoryAllocatorBridge.Native.dll | Advanced Micro Devices, Inc. (GPUOpen) | [MIT (Vulkan Memory Allocator)](../Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/LICENSE.txt) |  | PreserveNewest |
| XREngine.VRClient.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](licenses/fetched/openvr_api-BSD-3-Clause.txt) |  | PreserveNewest |

## Checked-in native/managed binaries (filesystem)
| Path | File | Likely upstream/owner | License (best-effort) |
|---|---|---|---|
| XREngine.Gltf/runtimes/win-x64/native/FastGltfBridge.Native.dll | FastGltfBridge.Native.dll | Sean Apeler (fastgltf) / simdjson authors | [MIT (fastgltf) + Apache-2.0 (simdjson)](licenses/notes/binary-item-XREngine.Gltf.csproj-FastGltfBridge.Native.dll.txt) |
| XREngine.Runtime.Core/runtimes/win-x64/native/lib_coacd.dll | lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |
| XREngine.Runtime.Core/runtimes/win-x64/native/libmagicphysx.dll | libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](licenses/fetched/libmagicphysx-MIT (MagicPhysX) + NVIDIA PhysX 5 license.txt) |
| XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/VulkanMemoryAllocatorBridge.Native.dll | VulkanMemoryAllocatorBridge.Native.dll | Advanced Micro Devices, Inc. (GPUOpen) | [MIT (Vulkan Memory Allocator)](../Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/LICENSE.txt) |
| XREngine.Runtime.Rendering/runtimes/win-x64/native/VulkanMemoryAllocatorBridge.Native.dll | VulkanMemoryAllocatorBridge.Native.dll | Advanced Micro Devices, Inc. (GPUOpen) | [MIT (Vulkan Memory Allocator)](../Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/LICENSE.txt) |
