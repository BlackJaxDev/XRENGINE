# Dependency Inventory

Generated: 2026-02-26T16:15:01-08:00
Commit: (not a git repo)

Best-effort inventory of dependencies referenced by the XRENGINE solution: NuGet packages, git submodules, and native/managed binaries that are referenced or shipped.

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
| OpenVR.NET | Build/Submodules/OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | https://github.com/BlackJaxDev/OpenVR.NET.git |
| OscCore-NET9 | Build/Submodules/OscCore-NET9 | BlackJaxDev | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | https://github.com/BlackJaxDev/OscCore-NET9.git |
| rive-sharp | Build/Submodules/rive-sharp | Rive (rive-app) | [MIT](https://github.com/rive-app/rive-cpp/blob/master/LICENSE) | https://github.com/rive-app/rive-sharp.git |

## Nested / fetched dependencies (build scripts)
| Name | Used by | Owner | License (best-effort) | URL |
|---|---|---|---|---|
| CDT | CoACD | artem-ogre | [MPL-2.0](https://github.com/artem-ogre/CDT/blob/main/LICENSE) | https://github.com/artem-ogre/CDT |

## NuGet packages (direct)
| Package | Version(s) | Owner (best-effort) | License (best-effort) | Used by |
|---|---|---|---|---|
| AssimpNetter | 6.0.2.1 | Saalvage | [MIT](licenses/nuget/AssimpNetter-6.0.2.1-MIT.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj, XREngine.Server.csproj, XREngine.VRClient.csproj |
| BitsKit | 1.2.0 | barncastle | [MIT](licenses/nuget/BitsKit-1.2.0-MIT.txt) | XREngine.csproj |
| DotnetNoise | 1.0.0 | Mr9Madness | [MIT](licenses/nuget/DotnetNoise-1.0.0-MIT.txt) | XREngine.csproj |
| DXNET.XInput | 5.0.0 | lepoco | [MIT](licenses/nuget/DXNET.XInput-5.0.0-MIT.txt) | XREngine.Input.csproj |
| FFmpeg.AutoGen | 7.1.1 | Ruslan-B | [LGPL-3.0](licenses/nuget/FFmpeg.AutoGen-7.1.1-LGPL-3.0.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Google.Cloud.Speech.V1 | 3.9.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.Speech.V1-3.9.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| Google.Cloud.TextToSpeech.V1 | 3.17.0 | googleapis | [Apache-2.0](licenses/nuget/Google.Cloud.TextToSpeech.V1-3.17.0-Apache-2.0.txt) | XREngine.Audio.csproj, XREngine.Editor.csproj |
| GraphQL | 8.8.3 | graphql-dotnet | [MIT](licenses/nuget/GraphQL-8.8.3-MIT.txt) | XREngine.csproj |
| ImmediateReflection | 2.0.0 | KeRNeLith | [MIT](licenses/nuget/ImmediateReflection-2.0.0-MIT.txt) | XREngine.Animation.csproj, XREngine.csproj |
| Jitter2 | 2.7.9 | notgiven688 | [MIT](licenses/nuget/Jitter2-2.7.9-MIT.txt) | XREngine.csproj |
| JoltPhysicsSharp | 2.20.1 | amerkoleci | [MIT](licenses/nuget/JoltPhysicsSharp-2.20.1-MIT.txt) | XREngine.csproj |
| K4os.Compression.LZ4 | 1.3.8 | MiloszKrajewski | [MIT](licenses/nuget/K4os.Compression.LZ4-1.3.8-MIT.txt) | XREngine.Data.csproj |
| LZMA-SDK | 22.1.1 | monemihir | [MIT](licenses/nuget/LZMA-SDK-22.1.1-MIT.txt) | XREngine.csproj, XREngine.Data.csproj |
| Magick.NET.Core | 14.10.3 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET.Core-14.10.3-Apache-2.0.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Modeling.csproj |
| Magick.NET.SystemDrawing | 8.0.16 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET.SystemDrawing-8.0.16-Apache-2.0.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Modeling.csproj |
| Magick.NET-Q16-HDRI-AnyCPU | 14.10.3 | dlemstra | [Apache-2.0](licenses/nuget/Magick.NET-Q16-HDRI-AnyCPU-14.10.3-Apache-2.0.txt) | XREngine.Animation.csproj, XREngine.Extensions.csproj, XREngine.Server.csproj, XREngine.UnitTests.csproj, XREngine.VRClient.csproj |
| MagicPhysX | 1.0.0 | Cysharp | [MIT](licenses/nuget/MagicPhysX-1.0.0-MIT.txt) | XREngine.csproj |
| MathNet.Numerics | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| MathNet.Numerics.Providers.CUDA | 5.0.0 | mathnet | [MIT](licenses/nuget/MathNet.Numerics.Providers.CUDA-5.0.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| MemoryPack | 1.21.4 | Cysharp | [MIT](licenses/nuget/MemoryPack-1.21.4-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.Profiler.csproj, XREngine.Server.csproj |
| Meshoptimizer.NET | 1.0.7 | BoyBaykiller | [MIT](licenses/nuget/Meshoptimizer.NET-1.0.7-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj, XREngine.Modeling.csproj, XREngine.UnitTests.csproj |
| MIConvexHull | 1.1.19.1019 | DesignEngrLab | [MIT](licenses/nuget/MIConvexHull-1.1.19.1019-MIT.txt) | XREngine.csproj, XREngine.Modeling.csproj |
| Microsoft.AspNetCore.Http | 2.3.9 | dotnet | [Apache-2.0](licenses/nuget/Microsoft.AspNetCore.Http-2.3.9-Apache-2.0.txt) | XREngine.Server.csproj |
| Microsoft.Build | 18.3.3 | dotnet | [MIT](licenses/nuget/Microsoft.Build-18.3.3-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Build.Framework | 18.3.3 | dotnet | [MIT](licenses/nuget/Microsoft.Build.Framework-18.3.3-MIT.txt) | XREngine.Editor.csproj |
| Microsoft.Data.Sqlite.Core | 10.0.3 | dotnet | [MIT](licenses/nuget/Microsoft.Data.Sqlite.Core-10.0.3-MIT.txt) | XREngine.Server.csproj |
| Microsoft.NET.Test.Sdk | 18.3.0 | microsoft | [MIT](licenses/nuget/Microsoft.NET.Test.Sdk-18.3.0-MIT.txt) | XREngine.UnitTests.csproj |
| NAudio | 2.2.1 | naudio | [MIT](licenses/nuget/NAudio-2.2.1-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NAudio.Lame | 2.1.0 | Corey-M | [MIT](licenses/nuget/NAudio.Lame-2.1.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NAudio.Sdl2 | 2.2.6 | alextnull | [MIT](licenses/nuget/NAudio.Sdl2-2.2.6-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj |
| NAudio.Vorbis | 1.5.0 | naudio | [MIT](licenses/nuget/NAudio.Vorbis-1.5.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| NDILibDotNetCoreBase | 2024.7.22.1 | eliaspuurunen | [MIT](licenses/nuget/NDILibDotNetCoreBase-2024.7.22.1-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.VRClient.csproj |
| Newtonsoft.Json | 13.0.4 | JamesNK | [MIT](licenses/nuget/Newtonsoft.Json-13.0.4-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Server.csproj |
| NUnit | 4.5.0 | nunit | [MIT](licenses/nuget/NUnit-4.5.0-MIT.txt) | XREngine.UnitTests.csproj |
| NUnit3TestAdapter | 6.1.0 | nunit | [MIT](licenses/nuget/NUnit3TestAdapter-6.1.0-MIT.txt) | XREngine.UnitTests.csproj |
| NVorbis | 0.10.5 | NVorbis | [MIT](licenses/nuget/NVorbis-0.10.5-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj |
| Raylib-cs | 7.0.2 | raylib-cs | [Zlib](licenses/nuget/Raylib-cs-7.0.2-Zlib.txt) | XREngine.csproj |
| RestSharp | 113.1.0 | restsharp | [Apache-2.0](licenses/nuget/RestSharp-113.1.0-Apache-2.0.txt) | XREngine.csproj |
| SharpCompress | 0.46.2 | adamhathcock | [MIT](licenses/nuget/SharpCompress-0.46.2-MIT.txt) | XREngine.Editor.csproj |
| SharpFont | 4.0.1 | Robmaister | [MIT](licenses/nuget/SharpFont-4.0.1-MIT.txt) | XREngine.Data.csproj |
| SharpFont.Dependencies | 2.6.0 | Robmaister | [https://github.com/Robmaister/SharpFont.Dependencies/blob/master/LICENSE](https://raw.githubusercontent.com/Robmaister/SharpFont.Dependencies/master/LICENSE) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Server.csproj, XREngine.UnitTests.csproj, XREngine.VRClient.csproj |
| SharpFont.NetStandard | 1.0.5 | vonderborch | [MIT](licenses/nuget/SharpFont.NetStandard-1.0.5-MIT.txt) | XREngine.csproj, XREngine.Data.csproj |
| SharpZipLib | 1.4.2 | icsharpcode | [MIT](licenses/nuget/SharpZipLib-1.4.2-MIT.txt) | XREngine.Data.csproj |
| Shouldly | 4.3.0 | shouldly | [BSD-3-Clause](licenses/nuget/Shouldly-4.3.0-BSD-3-Clause.txt) | XREngine.UnitTests.csproj |
| Silk.NET | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Core | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Core.Win32Extras | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Core.Win32Extras-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Direct3D.Compilers | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D.Compilers-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.Direct3D12 | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Direct3D12-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.DirectStorage | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.DirectStorage-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |
| Silk.NET.DirectStorage.Native | 1.3.0 | microsoft | [LICENSE.txt](licenses/nuget/Silk.NET.DirectStorage.Native-1.3.0-LICENSE.txt.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |
| Silk.NET.GLFW | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.GLFW-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Input | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Profiler.csproj |
| Silk.NET.Input.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Common-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Extensions-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Glfw-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Input.csproj |
| Silk.NET.Input.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Input.Sdl-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Maths | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Maths-2.23.0-MIT.txt) | XREngine.UnitTests.csproj |
| Silk.NET.OpenAL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenAL.Extensions.Creative | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Creative-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.Enumeration | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Enumeration-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Extensions.Soft | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenAL.Extensions.Soft-2.23.0-MIT.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenAL.Soft.Native | 1.23.1 | kcat | [LGPL-2.0-or-later](licenses/nuget/Silk.NET.OpenAL.Soft.Native-1.23.1-LGPL-2.0-or-later.txt) | XREngine.Audio.csproj, XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.UnitTests.csproj |
| Silk.NET.OpenGL.Extensions.AMD | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.AMD-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGL.Extensions.ARB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ARB-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.EXT-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.ImGui | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.ImGui-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.Profiler.UI.csproj |
| Silk.NET.OpenGL.Extensions.INTEL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.INTEL-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.KHR-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGL.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.NV-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGL.Extensions.OVR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGL.Extensions.OVR-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenGLES.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.EXT-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenGLES.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenGLES.Extensions.NV-2.23.0-MIT.txt) | XREngine.csproj |
| Silk.NET.OpenXR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.EXT-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.HTC | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTC-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.HTCX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.HTCX-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.KHR-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.MSFT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.MSFT-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.OpenXR.Extensions.VALVE | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.OpenXR.Extensions.VALVE-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.SDL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.SDL-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Shaderc | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Shaderc-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.AMD | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.AMD-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.ARM | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.ARM-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.EXT | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.EXT-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.FB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.FB-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.HUAWEI | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.HUAWEI-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.INTEL | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.INTEL-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.KHR | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.KHR-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.NV | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NV-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.NVX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.NVX-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.QNX | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.QNX-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Extensions.VALVE | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Vulkan.Extensions.VALVE-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Vulkan.Loader.Native | 2025.9.12 | KhronosGroup | [Apache-2.0](licenses/nuget/Silk.NET.Vulkan.Loader.Native-2025.9.12-Apache-2.0.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.WGL.Extensions.ARB | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.WGL.Extensions.ARB-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Profiler.csproj, XREngine.UnitTests.csproj |
| Silk.NET.Windowing.Common | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Common-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Extensions | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Extensions-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Glfw | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Glfw-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.Windowing.Sdl | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.Windowing.Sdl-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| Silk.NET.XInput | 2.23.0 | dotnet | [MIT](licenses/nuget/Silk.NET.XInput-2.23.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj |
| SixLabors.ImageSharp | 3.1.12 | SixLabors | [Apache-2.0](licenses/nuget/SixLabors.ImageSharp-3.1.12-Apache-2.0.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj, XREngine.Server.csproj, XREngine.VRClient.csproj |
| SkiaSharp | 3.119.2 | Microsoft | [MIT](licenses/nuget/SkiaSharp-3.119.2-MIT.txt) | XREngine.csproj |
| SPIRVCross.NET | 1.1.3 | FaberSanZ | [MIT](licenses/nuget/SPIRVCross.NET-1.1.3-MIT.txt) | XREngine.Editor.csproj |
| Steamworks.NET | 2024.8.0 | rlabrecque | [MIT](licenses/nuget/Steamworks.NET-2024.8.0-MIT.txt) | XREngine.csproj, XREngine.Editor.csproj, XREngine.Server.csproj |
| StirlingLabs.assimp.native.win-x64 | 5.2.5.4 | Stirling Labs / Assimp contributors | [BSD-3-Clause](https://github.com/assimp/assimp/blob/master/LICENSE) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj, XREngine.Extensions.csproj |
| Svg.Skia | 3.4.1 | wieslawsoltes | [MIT](licenses/nuget/Svg.Skia-3.4.1-MIT.txt) | XREngine.csproj |
| System.Drawing.Common | 10.0.3 | dotnet | [MIT](licenses/nuget/System.Drawing.Common-10.0.3-MIT.txt) | XREngine.Data.csproj |
| System.IdentityModel.Tokens.Jwt | 8.16.0 | AzureAD | [MIT](licenses/nuget/System.IdentityModel.Tokens.Jwt-8.16.0-MIT.txt) | XREngine.csproj, XREngine.Server.csproj |
| System.IO.Hashing | 10.0.3 | dotnet | [MIT](licenses/nuget/System.IO.Hashing-10.0.3-MIT.txt) | XREngine.csproj |
| System.Management | 10.0.3 | dotnet | [MIT](licenses/nuget/System.Management-10.0.3-MIT.txt) | XREngine.csproj |
| System.Text.Json | 10.0.3 | dotnet | [MIT](licenses/nuget/System.Text.Json-10.0.3-MIT.txt) | XREngine.Animation.csproj, XREngine.Audio.csproj, XREngine.csproj, XREngine.Data.csproj, XREngine.Input.csproj, XREngine.Modeling.csproj |
| UltralightNet | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet-1.3.0-MIT.txt) | XREngine.csproj |
| UltralightNet.AppCore | 1.3.0 | SupinePandora43 | [MIT](licenses/nuget/UltralightNet.AppCore-1.3.0-MIT.txt) | XREngine.csproj |
| Vecc.YamlDotNet.Analyzers.StaticGenerator | 16.3.0 | aaubry | [MIT](licenses/nuget/Vecc.YamlDotNet.Analyzers.StaticGenerator-16.3.0-MIT.txt) | XREngine.csproj |
| YamlDotNet | 16.3.0 | aaubry | [MIT](licenses/nuget/YamlDotNet-16.3.0-MIT.txt) | XREngine.csproj, XREngine.Data.csproj, XREngine.Editor.csproj |
| ZstdSharp.Port | 0.8.7 | oleg-st | [MIT](licenses/nuget/ZstdSharp.Port-0.8.7-MIT.txt) | XREngine.Data.csproj |

## Explicit assembly references (`<Reference>` )
| Project | Reference | Owner (best-effort) | License (best-effort) | HintPath |
|---|---|---|---|---|
| XREngine.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.csproj | OscCore | BlackJaxDev | [MIT](licenses/submodules/OscCore-NET9-MIT.txt) | ..\Build\Submodules\OscCore-NET9\bin\$(Configuration)\net9.0\OscCore.dll |
| XREngine.csproj | RiveSharp | Rive (rive-app) | [MIT](https://github.com/rive-app/rive-cpp/blob/master/LICENSE) | ..\Build\Submodules\rive-sharp\RiveSharp\bin\$(Configuration)\netstandard2.0\RiveSharp.dll |
| XREngine.Editor.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.Input.csproj | OpenVR.NET | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |
| XREngine.VRClient.csproj | OpenVR.NET, Version=0.8.5.0, Culture=neutral, PublicKeyToken=null | BlackJaxDev | [MIT](licenses/submodules/OpenVR.NET-MIT.txt) | ..\Build\Submodules\OpenVR.NET\OpenVR.NET\bin\$(Configuration)\net6.0\OpenVR.NET.dll |

## Referenced binaries via project items (dll/exe)
| Project | Path/Update | Owner (best-effort) | License (best-effort) | Link | CopyToOutputDirectory |
|---|---|---|---|---|---|
| XREngine.Audio.csproj | runtimes\win-x64\native\phonon.dll | Valve (Steam Audio) | [Apache-2.0](https://raw.githubusercontent.com/ValveSoftware/steam-audio/master/LICENSE.md) | phonon.dll | PreserveNewest |
| XREngine.csproj | $(MetaOvrLipSyncWinX64Dir)OVRLipSync.dll | Meta Platforms, Inc. | [Proprietary (Oculus SDK License Agreement)](https://developers.meta.com/horizon/licenses/oculussdk/) | OVRLipSync.dll | PreserveNewest |
| XREngine.csproj | $(NvidiaRtxgiWinX64Dir)RestirGI.Native.dll | NVIDIA Corporation | [Proprietary (NVIDIA RTXGI SDK License)](https://developer.nvidia.com/rtxgi) | RestirGI.Native.dll | Always |
| XREngine.csproj | ..\Build\Dependencies\FFmpeg\HlsReference\win-x64\*.dll | FFmpeg Project | [LGPL-2.1-or-later](https://www.ffmpeg.org/legal.html) | %(Filename)%(Extension) | PreserveNewest |
| XREngine.csproj | C:\Users\<user>\.nuget\packages\naudio.lame\2.1.0\build\libmp3lame.32.dll | LAME Project (packaged via NAudio.Lame / Corey-M) | [LGPL-2.0-or-later (LAME)](http://lame.sourceforge.net/license.txt) |  |  |
| XREngine.csproj | C:\Users\<user>\.nuget\packages\naudio.lame\2.1.0\build\libmp3lame.64.dll | LAME Project (packaged via NAudio.Lame / Corey-M) | [LGPL-2.0-or-later (LAME)](http://lame.sourceforge.net/license.txt) |  |  |
| XREngine.csproj | ffmpeg.exe | FFmpeg Project | [LGPL-2.1-or-later](https://www.ffmpeg.org/legal.html) |  | PreserveNewest |
| XREngine.csproj | ffplay.exe | FFmpeg Project | [LGPL-2.1-or-later](https://www.ffmpeg.org/legal.html) |  | PreserveNewest |
| XREngine.csproj | ffprobe.exe | FFmpeg Project | [LGPL-2.1-or-later](https://www.ffmpeg.org/legal.html) |  | PreserveNewest |
| XREngine.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](https://github.com/ValveSoftware/openvr/blob/master/LICENSE) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](https://github.com/Cysharp/MagicPhysX/blob/main/LICENSE) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\postproc.dll | FFmpeg Project | [LGPL-2.1-or-later](https://www.ffmpeg.org/legal.html) |  | PreserveNewest |
| XREngine.csproj | runtimes\win-x64\native\rive.dll | Rive | [MIT](https://github.com/rive-app/rive-cpp/blob/master/LICENSE) |  | PreserveNewest |
| XREngine.Editor.csproj | C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll | Khronos Group (OpenXR loader), distributed via Valve/SteamVR | [Apache-2.0](https://github.com/KhronosGroup/OpenXR-SDK-Source/blob/master/LICENSE) | openxr_loader.dll | PreserveNewest |
| XREngine.VRClient.csproj | openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](https://github.com/ValveSoftware/openvr/blob/master/LICENSE) |  | PreserveNewest |

## Checked-in native/managed binaries (filesystem)
| Path | File | Likely upstream/owner | License (best-effort) |
|---|---|---|---|
| XRENGINE/openvr_api.dll | openvr_api.dll | Valve (OpenVR/SteamVR) | [BSD-3-Clause](https://github.com/ValveSoftware/openvr/blob/master/LICENSE) |
| XRENGINE/runtimes/win-x64/native/lib_coacd.dll | lib_coacd.dll | SarahWeiii (CoACD) | [MIT (see Build/Submodules/CoACD/LICENSE)](../Build/Submodules/CoACD/LICENSE) |
| XRENGINE/runtimes/win-x64/native/libmagicphysx.dll | libmagicphysx.dll | Cysharp (MagicPhysX) / NVIDIA (PhysX 5) | [MIT (MagicPhysX) + NVIDIA PhysX 5 license](https://github.com/Cysharp/MagicPhysX/blob/main/LICENSE) |
