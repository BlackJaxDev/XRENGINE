# Unity Shader Translation

Last updated: 2026-11-18

The Unity shader translator converts Unity built-in pipeline `.shader` files into engine GLSL. It parses ShaderLab, assembles each pass as HLSL, compiles the HLSL to SPIR-V with Slang, and converts the SPIR-V to GLSL 4.60 with SPIRV-Cross. A post-processor then changes the GLSL to use engine uniform names, sampler names, storage buffers, and vertex attribute locations.

The translator replaces hand-written ports of specific Unity shaders. Do not add per-shader GLSL ports for imported content. Fix the translator or the compatibility includes instead.

The translator is in an early state. It is a library and a command-line tool. It is not connected to `XRMaterial` or to the model importer yet.

## Pipeline

1. `ShaderLabParser` reads the `.shader` text into a `ShaderLabDocument`: properties, subshaders, passes, tags, render state commands, and program blocks.
2. `ShaderTranslator` selects the first subshader that has a normal pass with a program. It reads the `#pragma` lines of each pass with `ShaderProgramPragmas`.
3. `ShaderVariantSelector` selects one keyword from each `multi_compile` and `shader_feature` set. The first enabled keyword wins. If no keyword in a set is enabled, the selector uses the empty option, or the first keyword when the set has no empty option.
4. `ShaderPassAssembler` writes one HLSL unit for each stage. The unit has the Unity defines, the selected keywords, the `CGINCLUDE` or `HLSLINCLUDE` blocks, and the pass program. `#line` directives keep compiler messages on the source lines of the `.shader` file.
5. `SlangSpirvCompiler` runs `slangc` with row-major matrix layout and writes SPIR-V and reflection JSON.
6. `SpirvCrossGlslEmitter` runs `spirv-cross` and writes desktop GLSL 4.60.
7. `GlslPostProcessor` changes the GLSL for the engine:
   - It changes the default uniform block into loose uniforms.
   - It gives combined samplers the name of their texture. A texture that a shader samples with more than one sampler gets the name `<texture>__<sampler>`, and the translator reports a `UNITYSHADER0012` warning.
   - It removes sampler bindings, so that the engine binds samplers by name.
   - It changes the known light buffers to the engine storage blocks, for example `ForwardDirectionalLightsBuffer`, with `XRENGINE_FORWARD_DESCRIPTOR_SET`.
   - It moves vertex inputs to the engine attribute locations.

## Pass Mapping

| Unity `LightMode` | Engine pass kind | Engine keywords |
| --- | --- | --- |
| `ForwardBase` | `ForwardBase` | `DIRECTIONAL`, `LIGHTPROBE_SH` |
| `ForwardAdd` | `ForwardAdd` | `POINT` |
| `ShadowCaster` | `ShadowCaster` | `SHADOWS_DEPTH` |
| None or `Always` | `Unlit` | None |
| `Meta`, `Deferred`, `PrepassBase`, `PrepassFinal`, `MotionVectors`, and other modes | Skipped | Not applicable |

`GrabPass` and `UsePass` are skipped with a warning. Surface shaders (`#pragma surface`) are not supported yet.

## Vertex Attributes

| Unity semantic | Engine location |
| --- | --- |
| `POSITION` | 0 |
| `NORMAL` | 1 |
| `TANGENT` | 2 |
| `TEXCOORD0` to `TEXCOORD3` | 3 to 6 |
| `COLOR` | 7 |

Other vertex inputs cause a `UNITYSHADER0007` error.

## Compatibility Includes

`Build/CommonAssets/Shaders/UnityCompat/` contains engine-written replacements for the Unity include files, such as `UnityCG.cginc`, `Lighting.cginc`, and `UnityPBSLighting.cginc`. These files contain no Unity source. They map Unity names to engine values:

- `unity_ObjectToWorld`, `UNITY_MATRIX_V`, `UNITY_MATRIX_P`, and the other matrices use the engine matrix uniforms. The macros transpose the engine matrices, because Slang compiles with row-major layout.
- `_Time`, `_ScreenParams`, `_ZBufferParams`, and `_WorldSpaceCameraPos` use the engine time, screen, and camera uniforms.
- `_WorldSpaceLightPos0` and `_LightColor0` use the first engine directional light.
- Spherical harmonics use `GlobalAmbient`.
- `VFACE` becomes `SV_IsFrontFace`. The assembler changes a `VFACE` parameter of the fragment entry point into a signed float, as Unity does.

Shadow attenuation is 1 for now. Add a missing Unity name to the correct include file, not to the translator.

## Command-Line Tool

`Tools/UnityShaderTranslator` translates files or folders and writes the GLSL, HLSL, and SPIR-V of each pass, plus `report.md`.

```powershell
dotnet run --project Tools/UnityShaderTranslator -- --scan <unity-project>/Assets/Shaders --out Build/_AgentValidation/<run>/reports/shaders --project <unity-project> --keywords _NORMALMAP
```

| Option | Description |
| --- | --- |
| `--shader <file>` | Translate one `.shader` file. Repeat it for more files. |
| `--scan <folder>` | Translate all `.shader` files in the folder and its subfolders. |
| `--out <folder>` | Write the output to this folder. |
| `--project <folder>` | Add the Unity project root to the include path. |
| `--compat <folder>` | Use a different compatibility include folder. |
| `--keywords A,B` | Enable these material keywords. |
| `--keep` | Keep the intermediate compiler files. |

## Tool Setup

The translator needs `slangc` and `spirv-cross`. It finds each tool in this order:

1. The `XRE_SLANGC` or `XRE_SPIRV_CROSS` environment variable.
2. The `Bin` folder of `VULKAN_SDK`.
3. `PATH`.

The Vulkan SDK contains both tools. If a tool is missing, the translator reports `UNITYSHADER0010`.

## Diagnostics

| Code | Meaning |
| --- | --- |
| `UNITYSHADER0001` | The ShaderLab text is not valid. |
| `UNITYSHADER0002` | No subshader has a pass that the translator can use. |
| `UNITYSHADER0003` | Slang or SPIRV-Cross failed. |
| `UNITYSHADER0004` | The shader is a surface shader. |
| `UNITYSHADER0005` | The pass uses a `GLSLPROGRAM` block. |
| `UNITYSHADER0006` | The translator skipped a pass. |
| `UNITYSHADER0007` | A vertex input has no engine attribute location. |
| `UNITYSHADER0008` | The pass is a `GrabPass` or a `UsePass`. |
| `UNITYSHADER0009` | An entry point pragma is missing. |
| `UNITYSHADER0010` | `slangc` or `spirv-cross` is not available. |
| `UNITYSHADER0011` | The pass has a geometry, hull, or domain stage. |
| `UNITYSHADER0012` | The post-processor changed a binding in a way that needs attention. |

## Limitations

- The translator is not connected to `XRMaterial`, render state, or the model importer.
- The forward add light loop and received shadows are not complete.
- Stereo rendering and compute skinning of translated vertex stages are not supported.
- Include files from `Library/PackageCache` are not mapped.
- Matrix properties of a shader need a transposed upload.

## Tests

`XREngine.UnitTests/Rendering/ShaderTranslation/` tests the parser, the pragma reader, the variant selector, the assembler, and the post-processor. `ShaderTranslatorToolTests` runs the full path on the shaders in `XREngine.UnitTests/TestData/UnityShaders/`. These tests are ignored when `slangc` or `spirv-cross` is not available.

## Key Files

- `XREngine.ShaderTranslation/ShaderLab/ShaderLabParser.cs`
- `XREngine.ShaderTranslation/Translation/ShaderTranslator.cs`
- `XREngine.ShaderTranslation/Translation/ShaderPassAssembler.cs`
- `XREngine.ShaderTranslation/Translation/ShaderVariantSelector.cs`
- `XREngine.ShaderTranslation/Compilation/SlangSpirvCompiler.cs`
- `XREngine.ShaderTranslation/Compilation/SpirvCrossGlslEmitter.cs`
- `XREngine.ShaderTranslation/Compilation/GlslPostProcessor.cs`
- `Build/CommonAssets/Shaders/UnityCompat/`
- `Tools/UnityShaderTranslator/Program.cs`

## Related Documentation

- [Poiyomi Toon Material Conversion](poiyomi-toon-material-conversion.md)
- [Uber Shader Materials](uber-shader-materials.md)
- [Unity Conversion Integrations](../assets/unity-conversion-integrations.md)
