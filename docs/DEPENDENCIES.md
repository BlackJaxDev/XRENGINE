# Dependency Inventory

Generated: 2026-01-02T11:01:16-08:00
Commit: (not a git repo)

Best-effort inventory of dependencies referenced by the XRENGINE solution: NuGet packages, git submodules, and native/managed binaries that are referenced or shipped.

Notes:
- `Owner` is derived from a GitHub repository URL when available, otherwise from the NuGet nuspec `authors` field (best-effort).
- This lists direct `PackageReference`s from solution projects, not all transitive dependencies.
- NVIDIA proprietary SDK binaries (DLSS/NGX, Reflex, Streamline) are **not redistributed** and are expected to be provided by end users via `ThirdParty/NVIDIA/SDK/win-x64/`.

## Git submodules / vendored submodules
| Name | Path | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CoACD | Build/Submodules/CoACD | SarahWeiii | [MIT](licenses/submodules/CoACD-MIT.txt) | https://github.com/SarahWeiii/CoACD |
| Flyleaf | Build/Submodules/Flyleaf | BlackJaxDev | [LGPL-3.0](licenses/submodules/Flyleaf-LGPL-3.0.txt) | https://github.com/BlackJaxDev/Flyleaf.git |
| OpenVR.NET | Build/Submodules/OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | https://github.com/BlackJaxDev/OpenVR.NET.git |
| OscCore-NET9 | Build/Submodules/OscCore-NET9 | BlackJaxDev | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | https://github.com/BlackJaxDev/OscCore-NET9.git |
| rive-sharp | Build/Submodules/rive-sharp | rive-app | [(unknown)](licenses/unknown/submodules-rive-sharp.txt) | https://github.com/rive-app/rive-sharp.git |

## Nested / fetched dependencies (build scripts)
| Name | Used by | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CDT | CoACD | artem-ogre | [(unknown)](licenses/unknown/nested-CDT.txt) | https://github.com/artem-ogre/CDT |
| fastgltf | Build-FastGltf.ps1 | spnda | [MIT](licenses/submodules/fastgltf-MIT.txt) | https://github.com/spnda/fastgltf |
| simdjson | fastgltf | simdjson | [Apache-2.0](licenses/unknown/nested-simdjson-Apache-2.0.txt) | https://github.com/simdjson/simdjson |

## NuGet packages (direct)
| Package | Version(s) | Owner (best-effort) | License (best-effort) | Used by |
|---|---|---|---|---|
| AssimpNetter | 6.0.2.1 | Saalvage | [MIT](licenses/nuget/AssimpNetter-6.0.2.1-MIT.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj, XREngine.Server.csproj, XREngine.VRClient.csproj |
| BitsKit | 1.2.0 | barncastle | [MIT](licenses/nuget/BitsKit-1.2.0-MIT.txt) | XREngine.csproj |
| DotnetNoise | 1.0.0 | Mr9Madness | [MIT](licenses/nuget/DotnetNoise-1.0.0-MIT.txt) | XREngine.csproj |
| DXNET.XInput | 5.0.0 | lepoco | [MIT](licenses/nuget/DXNET.XInput-5.0.0-MIT.txt) | XREngine.Input.csproj |
| FFmpeg.AutoGen | 8.0.0 | Ruslan-B | [MIT](licenses/nuget/FFmpeg.AutoGen-8.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Google.Cloud.Speech.V1 | 3.9.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.Speech.V1-3.9.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| Google.Cloud.TextToSpeech.V1 | 3.17.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.TextToSpeech.V1-3.17.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| GraphQL | 8.8.2 | graphql-dotnet | [MIT](licenses/nuget/GraphQL-8.8.2-MIT.txt) | XREngine.csproj |
| ImmediateReflection | 2.0.0 | KeRNeLith | [MIT](licenses/nuget/ImmediateReflection-2.0.0-MIT.txt) | XREngine.Animation.csproj, XREngine.csproj |
| Jitter2 | 2.7.6 | notgiven688 | [MIT](licenses/nuget/Jitter2-2.7.6-MIT.txt) | XREngine.csproj |
| JoltPhysicsSharp | 2.19.5 | amerkoleci | [MIT](licenses/nuget/JoltPhysicsSharp-2.19.5-MIT.txt) | XREngine.csproj |
| LZMA-SDK | 22.1.1 | monemihir | [MIT](licenses/nuget/LZMA-SDK-22.1.1-MIT.txt) | XREngine.csproj, XREngine.Data.csproj |
| Magick.NET.Core | 14.10.1 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET.Core-14.10.1-Apache-2.0.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Modeling.csproj |
| Magick.NET.SystemDrawing | 8.0.14 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET.SystemDrawing-8.0.14-Apache-2.0.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Modeling.csproj |
| Magick.NET-Q16-HDRI-AnyCPU | 14.10.1 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET-Q16-HDRI-AnyCPU-14.10.1-Apache-2.0.txt) | XREngine.Animation.csproj, XREngine.Extensions.csproj, XREngine.Server.csproj, XREngine.UnitTests.csproj, XREngine.VRClient.csproj |
| MagicPhysX | 1.0.0 | Cysharp | [MIT](licenses/nuget/MagicPhysX-1.0.0-MIT.txt) | XREngine.csproj |
| MathNet.Numerics | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| MathNet.Numerics.Providers.CUDA | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics.Providers.CUDA-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| MemoryPack | 1.21.4 | Cysharp | [MIT](licenses/nuget/MemoryPack-1.21.4-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.Server.csproj |
| Meshoptimizer.NET | 1.0.7 | BoyBaykiller | [MIT](licenses/nuget/Meshoptimizer.NET-1.0.7-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.UnitTests.csproj |
| MIConvexHull | 1.1.19.1019 | DesignEngrLab | [MIT](licenses/nuget/MIConvexHull-1.1.19.1019-MIT.txt) | XREngine.csproj, XREngine.Modeling.csproj |
| Microsoft.AspNetCore.Http | 2.3.0 | dotnet | [Apache-2.0](licenses/nuget/Microsoft.AspNetCore.Http-2.3.0-Apache-2.0.txt) | XREngine.Server.csproj |
| Microsoft.Build | 18.0.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build-18.0.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Build.Framework | 18.0.2 | dotnet | [MIT](licenses/nuget/Microsoft.Build.Framework-18.0.2-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Data.Sqlite.Core | 10.0.1 | dotnet | [MIT](licenses/nuget/Microsoft.Data.Sqlite.Core-10.0.1-MIT.txt) | XREngine.Server.csproj |
| Microsoft.NET.Test.Sdk | 18.0.1 | microsoft | [MIT](licenses/nuget/Microsoft.NET.Test.Sdk-18.0.1-MIT.txt) | XREngine.UnitTests.csproj |
| NAudio | 2.2.1 | naudio | [MIT](licenses/nuget/NAudio-2.2.1-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NAudio.Lame | 2.1.0 | Corey-M | [MIT](licenses/nuget/NAudio.Lame-2.1.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NAudio.Sdl2 | 2.2.6 | alextnull | [MIT](licenses/nuget/NAudio.Sdl2-2.2.6-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj |
| NAudio.Vorbis | 1.5.0 | naudio | [MIT](licenses/nuget/NAudio.Vorbis-1.5.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NDILibDotNetCoreBase | 2024.7.22.1 | eliaspuurunen | [MIT](licenses/nuget/NDILibDotNetCoreBase-2024.7.22.1-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.VRClient.csproj |
| Newtonsoft.Json | 13.0.4 | JamesNK | [MIT](licenses/nuget/Newtonsoft.Json-13.0.4-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Server.csproj |
| NUnit | 4.4.0 | nunit | [MIT](licenses/nuget/NUnit-4.4.0-MIT.txt) | XREngine.UnitTests.csproj |
| NUnit3TestAdapter | 6.0.1 | nunit | [MIT](licenses/nuget/NUnit3TestAdapter-6.0.1-MIT.txt) | XREngine.UnitTests.csproj |
| NVorbis | 0.10.5 | NVorbis | [MIT](licenses/nuget/NVorbis-0.10.5-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| Raylib-cs | 7.0.2 | raylib-cs | [Zlib](licenses/nuget/Raylib-cs-7.0.2-Zlib.txt) | XREngine.csproj |
| RestSharp | 113.0.0 | restsharp | [Apache-2.0](licenses/nuget/RestSharp-113.0.0-Apache-2.0.txt) | XREngine.csproj |
| SharpCompress | 0.42.1 | adamhathcock | [MIT](licenses/nuget/SharpCompress-0.42.1-MIT.txt) | XREngine.Editor.csproj |
| SharpFont | 4.0.1 | Robmaister | [https://github.com/Robmaister/SharpFont/blob/master/LICENSE](licenses/nuget/SharpFont-4.0.1-https___github.com_Robmaister_SharpFont_blob_master_LICENSE.txt) | XREngine.Data.csproj |
| SharpFont.Dependencies | 2.6.0 | Robmaister | [https://github.com/Robmaister/SharpFont.Dependencies/blob/master/LICENSE](https://raw.githubusercontent.com/Robmaister/SharpFont.Dependencies/master/LICENSE) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Server.csproj, XREngine.UnitTests.csproj, XREngine.VRClient.csproj |
| SharpFont.NetStandard | 1.0.5 | vonderborch | [MIT](licenses/nuget/SharpFont.NetStandard-1.0.5-MIT.txt) | XREngine.csproj, XREngine.Data.csproj |
| SharpZipLib | 1.4.2 | icsharpcode | [MIT](licenses/nuget/SharpZipLib-1.4.2-MIT.txt) | XREngine.Data.csproj |
| Shouldly | 4.3.0 | shouldly | [BSD-3-Clause](licenses/nuget/Shouldly-4.3.0-BSD-3-Clause.txt) | XREngine.UnitTests.csproj |
| Silk.NET | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Core | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Core.Win32Extras | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core.Win32Extras-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Direct3D.Compilers | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D.Compilers-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.Direct3D12 | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D12-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.DirectStorage | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.DirectStorage-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.DirectStorage.Native | 1.2.3 | microsoft | [LICENSE.txt](licenses/nuget/Silk.NET.DirectStorage.Native-1.2.3-LICENSE.txt.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.GLFW | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.GLFW-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Input | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Common | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Common-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Extensions | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Extensions-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Glfw | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Glfw-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Sdl | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Sdl-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Maths | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Maths-2.22.0-MIT.txt) | XREngine.UnitTests.csproj |
| Silk.NET.OpenAL | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL-2.22.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.Creative | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Creative-2.22.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.Enumeration | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Enumeration-2.22.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.EXT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.EXT-2.22.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.Soft | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Soft-2.22.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Soft.Native | 1.23.1 | kcat | [LGPL-2.0-or-later](licenses/nuget/Silk.NET.OpenAL.Soft.Native-1.23.1-LGPL-2.0-or-later.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenGL.Extensions.AMD | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.AMD-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGL.Extensions.ARB | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ARB-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.EXT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.EXT-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.ImGui | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ImGui-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.INTEL | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.INTEL-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.KHR | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.KHR-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.NV | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.NV-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGL.Extensions.OVR | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.OVR-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGLES.Extensions.EXT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.EXT-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGLES.Extensions.NV | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.NV-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenXR | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.EXT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.EXT-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.HTC | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTC-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.HTCX | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTCX-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.KHR | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.KHR-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.MSFT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.MSFT-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.VALVE | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.VALVE-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.SDL | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.SDL-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Shaderc | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Shaderc-2.22.0-MIT.txt) | XREngine.csproj |
| Silk.NET.Vulkan | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.AMD | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.AMD-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.ARM | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.ARM-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.EXT | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.EXT-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.FB | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.FB-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.HUAWEI | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.HUAWEI-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.INTEL | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.INTEL-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.KHR | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.KHR-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.NV | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NV-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.NVX | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NVX-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.QNX | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.QNX-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.VALVE | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.VALVE-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Loader.Native | 2024.10.25 | KhronosGroup | [Apache-2.0](licenses/nuget/Silk.NET.Vulkan.Loader.Native-2024.10.25-Apache-2.0.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.WGL.Extensions.ARB | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.WGL.Extensions.ARB-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Windowing.Common | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Common-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Extensions | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Extensions-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Glfw | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Glfw-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Sdl | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Sdl-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.XInput | 2.22.0 | dotnet | [MIT](licenses/nuget/Silk.NET.XInput-2.22.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| SixLabors.ImageSharp | 3.1.12 | SixLabors | [Apache-2.0](licenses/nuget/SixLabors.ImageSharp-3.1.12-Apache-2.0.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj, XREngine.Server.csproj, XREngine.VRClient.csproj |
| SkiaSharp | 2.88.9 | mono | [MIT](licenses/nuget/SkiaSharp-2.88.9-MIT.txt) | XREngine.csproj |
| Steamworks.NET | 2024.8.0 | rlabrecque | [MIT](licenses/nuget/Steamworks.NET-2024.8.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Server.csproj |
| StirlingLabs.assimp.native.win-x64 | 5.2.5.4 | assimp Team, packaged by Stirling Labs | [BSD-3-Clause](licenses/nuget/StirlingLabs.assimp.native.win-x64-5.2.5.4-BSD-3-Clause.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj |
| Svg.Skia | 3.2.1 | wieslawsoltes | [MIT](licenses/nuget/Svg.Skia-3.2.1-MIT.txt) | XREngine.csproj |
| System.Drawing.Common | 10.0.1 | dotnet | [MIT](licenses/nuget/System.Drawing.Common-10.0.1-MIT.txt) | XREngine.Data.csproj |
| System.IdentityModel.Tokens.Jwt | 8.15.0 | AzureAD | [MIT](licenses/nuget/System.IdentityModel.Tokens.Jwt-8.15.0-MIT.txt) | XREngine.csproj, XREngine.Server.csproj |
| System.Management | 10.0.1 | dotnet | [MIT](licenses/nuget/System.Management-10.0.1-MIT.txt) | XREngine.csproj |
| System.Text.Json | 10.0.1 | dotnet | [MIT](licenses/nuget/System.Text.Json-10.0.1-MIT.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj |
| UltralightNet | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet-1.3.0-MIT.txt) | XREngine.csproj |
| Vecc.YamlDotNet.Analyzers.StaticGenerator | 16.3.0 | aaubry | [MIT](licenses/nuget/Vecc.YamlDotNet.Analyzers.StaticGenerator-16.3.0-MIT.txt) | XREngine.csproj |
| YamlDotNet | 16.3.0 | aaubry | [MIT](licenses/nuget/YamlDotNet-16.3.0-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |

## Explicit assembly references (`<Reference>` )
| Project | Reference | Owner (best-effort) | License (best-effort) | HintPath |
|---|---|---|---|---|
| XREngine.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.csproj | OscCore | BlackJaxDev | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | ..\Build\Submodules\OscCore-NET9\bin\$(Configuration)\net9.0\OscCore.dll |
| XREngine.csproj | RiveSharp | rive-app | [(unknown)](licenses/unknown/reference-XREngine.csproj-RiveSharp.txt) | ..\Build\Submodules\rive-sharp\RiveSharp\bin\$(Configuration)\netstandard2.0\RiveSharp.dll |
| XREngine.Editor.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Input.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.VRClient.csproj | OpenVR.NET, Version=0.8.5.0, Culture=neutral, PublicKeyToken=null | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |

## Referenced binaries via project items (dll/exe)
| Project | Path/Update | Owner (best-effort) | License (best-effort) | Link | CopyToOutputDirectory |
|---|---|---|---|---|---|
| XREngine.csproj | avcodec-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-avcodec-61.dll.txt) |  | PreserveNewest |
| XREngine.csproj | avdevice-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-avdevice-61.dll.txt) |  | PreserveNewest |
| XREngine.csproj | avfilter-10.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-avfilter-10.dll.txt) |  | PreserveNewest |
| XREngine.csproj | avformat-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-avformat-61.dll.txt) |  | PreserveNewest |
| XREngine.csproj | avutil-59.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-avutil-59.dll.txt) |  | PreserveNewest |
| XREngine.csproj | C:\Users\dnedd\.nuget\packages\naudio.lame\2.1.0\build\libmp3lame.32.dll | LAME / NAudio.Lame (Corey-M) packaging | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-C__Users_dnedd_.nuget_packages_naudio.lame_2.1.0_build_libmp3lame.32.dll.txt) |  |  |
| XREngine.csproj | C:\Users\dnedd\.nuget\packages\naudio.lame\2.1.0\build\libmp3lame.64.dll | LAME / NAudio.Lame (Corey-M) packaging | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-C__Users_dnedd_.nuget_packages_naudio.lame_2.1.0_build_libmp3lame.64.dll.txt) |  |  |
| XREngine.csproj | ffmpeg.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-ffmpeg.exe.txt) |  | PreserveNewest |
| XREngine.csproj | ffplay.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-ffplay.exe.txt) |  | PreserveNewest |
| XREngine.csproj | ffprobe.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-ffprobe.exe.txt) |  | PreserveNewest |
| XREngine.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-openvr_api.dll.txt) |  | PreserveNewest |
| XREngine.csproj | OVRLipSync.dll | Meta/Oculus (OVR LipSync) | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-OVRLipSync.dll.txt) |  | PreserveNewest |
| XREngine.csproj | RestirGI.Native.dll | (unknown) | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-RestirGI.Native.dll.txt) |  | Always |
| XREngine.csproj | runtimes\win-x64\native\avcodec.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_avcodec.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\avdevice.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_avdevice.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\avfilter.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_avfilter.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\avformat.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_avformat.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\avutil.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_avutil.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\lib_coacd.dll | SarahWeiii (CoACD) | [MIT](licenses/submodules/CoACD-MIT.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\libmagicphysx.dll | MagicPhysX | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_libmagicphysx.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\postproc.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_postproc.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\rive.dll | (unknown) | [(unknown)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_rive.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\swresample.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_swresample.dll.txt) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\swscale.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-runtimes_win-x64_native_swscale.dll.txt) |  | PreserveNewest |
| XREngine.csproj | swresample-5.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-swresample-5.dll.txt) |  | PreserveNewest |
| XREngine.csproj | swscale-8.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/binary-item-XREngine.csproj-swscale-8.dll.txt) |  | PreserveNewest |
| XREngine.Editor.csproj | C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll | Valve (SteamVR) / Khronos (OpenXR loader) | [(unknown)](licenses/unknown/binary-item-XREngine.Editor.csproj-C__Program Files (x86)_Steam_steamapps_common_SteamVR_bin_win64_openxr_loader.dll.txt) | openxr_loader.dll | PreserveNewest |
| XREngine.VRClient.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [(unknown)](licenses/unknown/binary-item-XREngine.VRClient.csproj-openvr_api.dll.txt) |  | PreserveNewest |

## Checked-in native/managed binaries (filesystem)
| Path | File | Likely upstream/owner | License (best-effort) |
|---|---|---|---|
| XRENGINE/avcodec-61.dll | avcodec-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-avcodec-61.dll.txt) |
| XRENGINE/avdevice-61.dll | avdevice-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-avdevice-61.dll.txt) |
| XRENGINE/avfilter-10.dll | avfilter-10.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-avfilter-10.dll.txt) |
| XRENGINE/avformat-61.dll | avformat-61.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-avformat-61.dll.txt) |
| XRENGINE/avutil-59.dll | avutil-59.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-avutil-59.dll.txt) |
| XRENGINE/ffmpeg.exe | ffmpeg.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-ffmpeg.exe.txt) |
| XRENGINE/ffplay.exe | ffplay.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-ffplay.exe.txt) |
| XRENGINE/ffprobe.exe | ffprobe.exe | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-ffprobe.exe.txt) |
| XRENGINE/openvr_api.dll | openvr_api.dll | Valve (OpenVR/SteamVR) | [(unknown)](licenses/unknown/checked-binary-openvr_api.dll.txt) |
| XRENGINE/OVRLipSync.dll | OVRLipSync.dll | Meta/Oculus (OVR LipSync) | [(unknown)](licenses/unknown/checked-binary-OVRLipSync.dll.txt) |
| XRENGINE/postproc-58.dll | postproc-58.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-postproc-58.dll.txt) |
| XRENGINE/runtimes/win-x64/native/lib_coacd.dll | lib_coacd.dll | SarahWeiii (CoACD) | [MIT](licenses/submodules/CoACD-MIT.txt) |
| XRENGINE/runtimes/win-x64/native/libmagicphysx.dll | libmagicphysx.dll | MagicPhysX | [(unknown)](licenses/unknown/checked-binary-libmagicphysx.dll.txt) |
| XRENGINE/swresample-5.dll | swresample-5.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-swresample-5.dll.txt) |
| XRENGINE/swscale-8.dll | swscale-8.dll | FFmpeg project | [(unknown - depends on FFmpeg build config)](licenses/unknown/checked-binary-swscale-8.dll.txt) |
