# Unity Shader Translation

Last updated: 2026-11-19

The Unity shader translator converts Unity built-in pipeline `.shader` files into engine GLSL. It parses ShaderLab, assembles each pass as HLSL, compiles the HLSL to SPIR-V with Slang, and converts the SPIR-V to GLSL 4.60 with SPIRV-Cross. A post-processor then changes the GLSL to use engine uniform names, sampler names, storage buffers, and vertex attribute locations.

The translator replaces hand-written ports of specific Unity shaders. Do not add per-shader GLSL ports for imported content. Fix the translator or the compatibility includes instead.

The translator is in an early state. It is a library, a command-line tool, and an opt-in path of the Unity material importer. See [Material Import](#material-import).

## Pipeline

1. `ShaderLabParser` reads the `.shader` text into a `ShaderLabDocument`: properties, subshaders, passes, tags, render state commands, and program blocks.
2. `ShaderTranslator` selects the first subshader that has a normal pass with a program. It reads the `#pragma` lines of each pass with `ShaderProgramPragmas`.
3. `ShaderVariantSelector` selects one keyword from each `multi_compile` and `shader_feature` set. The first enabled keyword wins. If no keyword in a set is enabled, the selector uses the empty option, or the first keyword when the set has no empty option.
4. `ShaderPassAssembler` writes one HLSL unit for each stage. The unit has the Unity defines, the selected keywords, the legacy conversion include, the `CGINCLUDE` or `HLSLINCLUDE` blocks, and the pass program. `#line` directives keep compiler messages on the source lines of the `.shader` file.
5. `ShaderIncludeExpander` replaces each `#include` with the file text, so that the next step can see all declarations. `#line` directives keep the original file names and line numbers. When a file includes a guarded file that is still being expanded higher in the include chain, the expander leaves that include out, because the guard is already defined.
6. `SwitchSelectorRewriter` converts each `switch` selector to `int`. `StaticConstRewriter` removes `const` from a `static const` variable whose initializer calls a function. `GlobalWriteRewriter` gives each global variable that the code writes to a private copy, and removes initializers of shader parameters. See [Legacy HLSL Rules](#legacy-hlsl-rules).
7. `SlangSpirvCompiler` runs `slangc` with row-major matrix layout and writes SPIR-V and reflection JSON. It turns off the warnings for code that FXC accepts without a warning. If the compile fails, the errors come before the warnings in the diagnostic text.
8. `SpirvCrossGlslEmitter` runs `spirv-cross` and writes desktop GLSL 4.60.
9. `GlslPostProcessor` changes the GLSL for the engine:
   - It changes the default uniform block into loose uniforms.
   - It gives combined samplers the name of their texture. A texture that a shader samples with more than one sampler gets the name `<texture>__<sampler>`, and the translator reports a `UNITYSHADER0012` warning.
   - It removes sampler bindings, so that the engine binds samplers by name.
   - It changes the known light buffers to the engine storage blocks, for example `ForwardDirectionalLightsBuffer`, with `XRENGINE_FORWARD_DESCRIPTOR_SET`.
   - It moves vertex inputs to the engine attribute locations and names.
   - It gives the shader parameters of rewritten globals their original names again.

## Legacy HLSL Rules

Unity compiles HLSL with the legacy FXC rules. Slang is stricter, so the translator adds two compatibility steps:

- **Implicit truncation.** FXC converts a larger vector or matrix to a smaller one without a cast, for example `float2 uv = worldPos;` or `float x = tex2D(...);`. FXC also accepts mixed sizes in operators, for example `float2 + float4`, and uses the leading components. `UnityCompat/HLSLLegacyConversions.cginc` declares these conversions and operators for Slang. The assembler includes it in every program.
- **Mixed sizes in functions and products.** The built-in functions such as `min`, `max`, `pow`, `dot`, and `distance` are generic over one vector type, so the conversions do not apply to them. The same file declares overloads for arguments of different sizes, and for one vector and one scalar argument. The overloads are generic over the component type, as the built-in functions are, and have a low overload rank, so that the built-in function wins when it also matches. It also declares `lerp` with end points of different sizes, and `mul` for a matrix and a smaller or larger vector.
- **Component type conversions.** The file declares implicit constructors between vectors with the same size and a different component type, for example `int2(uv)`. Without them, the scalar truncations make Slang resolve `int2(uv)` in a generic call to an invalid vector of vectors.
- **Float switch selectors.** FXC accepts `switch (_Mode)` on a `float`. Slang requires an integer, so `SwitchSelectorRewriter` changes the selector to `int(_Mode)`.
- **Writes to globals.** FXC lets a function write to a global variable, for example `_Time.y += 10;`. The write changes a private copy for the current invocation. `GlobalWriteRewriter` renames the shader parameter to `name_xrUniform` and declares `static T name = name_xrUniform;`. The post-processor gives the parameter its original name in the GLSL, so material binding does not change. The engine values in the compatibility includes, such as `_Time` and `_ScreenParams`, are static copies for the same reason.

- **Initializers of shader parameters.** FXC accepts `uniform float x = 0.0;` and stores the value as a default that Unity does not use. Slang rejects it. `GlobalWriteRewriter` removes the initializer of each numeric shader parameter. Use `static const` for a constant.

The file has no conversion from an `int` vector to a `uint` scalar or vector. With that conversion, Slang resolves `uint2 + int2` to an invalid type. `IsGammaSpace()` is a macro, so that `static const` initializers that use it stay compile-time constants.

FXC also evaluates function calls in `static const` initializers at compile time, for example `GammaToLinearSpace(float3(0.5, 0.5, 0.5))`. Slang requires a constant expression, so `StaticConstRewriter` removes `const` from these variables. Each invocation then computes the same value.

## Pass Mapping

| Unity `LightMode` | Engine pass kind | Engine keywords |
| --- | --- | --- |
| `ForwardBase` | `ForwardBase` | `DIRECTIONAL`, `LIGHTPROBE_SH` |
| `ForwardAdd` | `ForwardAdd` | `POINT` |
| `ShadowCaster` | `ShadowCaster` | `SHADOWS_DEPTH` |
| None or `Always` | `Unlit` | None |
| `Meta`, `Deferred`, `PrepassBase`, `PrepassFinal`, `MotionVectors`, and other modes | Skipped | Not applicable |

`GrabPass` and `UsePass` are skipped with a warning. Surface shaders (`#pragma surface`) are not supported yet.

## Material Import

Set `XRE_UNITY_SHADER_TRANSLATION=1` to let the Unity material importer translate shaders. The importer uses the translation for a material that is not a Poiyomi or lilToon material and whose shader is a `.shader` file in the source project. When the translation fails, the importer adds a warning and uses the generic conversion. Without the variable, the importer does not change.

The importer builds the material as follows:

1. It translates the shader with the material keywords (`m_ShaderKeywords` and `m_ValidKeywords`). It keeps each result by shader path, file write time, and keyword set, so materials that share a variant translate one time.
2. It uses the first `ForwardBase` pass, or the first unlit pass. The pass must have vertex and fragment stages. The translated stages become the material shaders.
3. It binds one texture for each `sampler2D` uniform that is a texture property. A missing texture uses the Unity default color of the property (`white`, `black`, `gray`, `bump`, `red`). A `sampler2D` that is not a material texture, for example the AudioLink `_AudioTexture` that a Unity script sets, gets a 1x1 black texture and a warning. The importer binds it because the OpenGL and Vulkan fallback textures are not the same color. Samplers of other types cause a warning and are not bound.
4. It sets each uniform that the material found. `_X_ST` takes the scale and offset of texture `_X`. Other uniforms take the material value, then the property default. In a Linear project, color properties use the sRGB-to-linear transfer, with the same `HDR` and `Gamma` rules as the Poiyomi conversion. Engine uniforms are not changed.
5. It maps `Cull`, `ZWrite`, `ZTest`, `Blend`, `BlendOp`, `ColorMask`, and `AlphaToMask`. A `[_Property]` argument takes the material value, then the property default. Numeric values use the Unity enums. `Stencil` blocks and per-target blend state cause a warning.
6. It selects the engine pass from the render queue (the material custom queue, then the `Queue` tag): below 2450 is opaque forward, 2450 to 2500 is masked forward, and above 2500 or any blending is transparent forward. `AlphaToMask On` selects alpha to coverage. `_Cutoff` sets the alpha cutoff.

The translated vertex stage replaces the engine vertex shader. It can read material parameters, for example `_MainTex_ST`. In OpenGL shader pipeline mode, the mesh renderer therefore sets the material parameters and the engine uniforms that the vertex program uses on the separable vertex program too (`GLMaterial.SetVertexProgramUniforms`). Skinned meshes therefore do not skin, and the engine stereo paths do not apply. Use static meshes to compare translated materials with Unity.

## Vertex Attributes

The mesh renderers bind each vertex buffer to the shader input with the same name. The post-processor therefore gives each vertex input the engine location and the engine buffer name.

| Unity semantic | Engine location | Engine input name |
| --- | --- | --- |
| `POSITION` | 0 | `Position` |
| `NORMAL` | 1 | `Normal` |
| `TANGENT` | 2 | `Tangent` |
| `TEXCOORD0` to `TEXCOORD3` | 3 to 6 | `TexCoord0` to `TexCoord3` |
| `COLOR` | 7 | `Color0` |

Engine-generated vertex shaders pack their inputs, so for a mesh without tangents `TexCoord0` is at location 2 there. One OpenGL VAO serves all passes of a mesh renderer, so the OpenGL mesh renderer binds the VAO again when a pass uses a vertex program with other attribute locations.

Another identifier in the stage that already has one of these names gets the `_xrUser` suffix.

Unity gives every mesh tangents, and many Unity shaders normalize the tangent. A zero tangent gives NaN values, which show as black or cut surfaces. When the translated vertex stage reads `Tangent`, the importer sets `XRMaterial.GenerateMissingMeshTangents`. A mesh renderer with such a material adds UV0 tangents (`XRMesh.GenerateTangentsFromUv0`) to a mesh that has triangles, normals, and UV0 but no tangents. Imported FBX meshes already get tangents at import. Other vertex inputs cause a `UNITYSHADER0007` error.

## Compatibility Includes

`Build/CommonAssets/Shaders/UnityCompat/` contains engine-written replacements for the Unity include files, such as `UnityCG.cginc`, `Lighting.cginc`, and `UnityPBSLighting.cginc`. These files contain no Unity source. They map Unity names to engine values:

- `unity_ObjectToWorld`, `UNITY_MATRIX_V`, `UNITY_MATRIX_P`, and the other matrices use the engine matrix uniforms. The macros transpose the engine matrices, because Slang compiles with row-major layout.
- `_Time`, `_ScreenParams`, `_ZBufferParams`, and `_WorldSpaceCameraPos` use the engine time, screen, and camera uniforms.
- `_WorldSpaceLightPos0` and `_LightColor0` use the first engine directional light.
- Spherical harmonics use `GlobalAmbient`.
- `VFACE` becomes `SV_IsFrontFace`. The assembler changes a `VFACE` parameter of the fragment entry point into a signed float, as Unity does.
- `unity_CameraToWorld` and `unity_WorldToCamera` use the engine view matrices with the Z axis changed, because Unity camera space looks along +Z.
- `UnityShaderVariables.cginc` includes `HLSLSupport.cginc` and `UnityShaderUtilities.cginc`, so that `HLSLPROGRAM` blocks and programs with no includes get the same names as in Unity.
- `UnityStandardShadow.cginc` gives `vertShadowCaster` and `fragShadowCaster`. The blended modes clip at an alpha of 0.5, because the engine shadow pass has no dither pattern.
- `UnityUI.cginc` gives the UI clipping helpers. `UnityMetaPass.cginc` gives declarations only, because the engine skips `Meta` passes.

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

- The material import is opt-in. It uses one color pass. It does not use `ForwardAdd` or `ShadowCaster` passes, `Stencil` blocks, or `_X_TexelSize` values.
- The forward add light loop and received shadows are not complete.
- Stereo rendering and compute skinning of translated vertex stages are not supported.
- Include files from `Library/PackageCache` are not mapped.
- `UnityStandardCoreForward.cginc`, `Tessellation.cginc`, and the URP shader library have no compatibility version yet.
- A `Category` block is supported. Fixed-function passes and `UsePass` are not.
- Matrix properties of a shader need a transposed upload.

## Tests

`XREngine.UnitTests/Rendering/ShaderTranslation/` tests the parser, the pragma reader, the variant selector, the assembler, the include expander, the switch, static const, and global-write rewriters, the diagnostic ordering, and the post-processor. `ShaderTranslatorToolTests` runs the full path on the shaders in `XREngine.UnitTests/TestData/UnityShaders/`. `UnityShaderTranslationMaterialImportTests` imports a `.mat` with the translation enabled and checks the stages, render state, and parameter values. These tests are ignored when `slangc` or `spirv-cross` is not available.

## Key Files

- `XREngine.ShaderTranslation/ShaderLab/ShaderLabParser.cs`
- `XREngine.ShaderTranslation/Translation/ShaderTranslator.cs`
- `XREngine.ShaderTranslation/Translation/ShaderPassAssembler.cs`
- `XREngine.ShaderTranslation/Preprocessing/ShaderIncludeExpander.cs`
- `XREngine.ShaderTranslation/Preprocessing/GlobalWriteRewriter.cs`
- `XREngine.ShaderTranslation/Preprocessing/SwitchSelectorRewriter.cs`
- `XREngine.ShaderTranslation/Preprocessing/StaticConstRewriter.cs`
- `XREngine.ShaderTranslation/Translation/ShaderVariantSelector.cs`
- `XREngine.ShaderTranslation/Compilation/SlangSpirvCompiler.cs`
- `XREngine.ShaderTranslation/Compilation/SpirvCrossGlslEmitter.cs`
- `XREngine.ShaderTranslation/Compilation/GlslPostProcessor.cs`
- `XREngine.Editor/Importers/SerializedMaterialImporter.UnityShaderTranslation.cs`
- `XREngine.Editor/Importers/UnityShaderTranslation/UnityShaderTranslationCache.cs`
- `XREngine.Editor/Importers/UnityShaderTranslation/ShaderLabRenderStateMapper.cs`
- `Build/CommonAssets/Shaders/UnityCompat/`
- `Tools/UnityShaderTranslator/Program.cs`

## Related Documentation

- [Poiyomi Toon Material Conversion](poiyomi-toon-material-conversion.md)
- [Uber Shader Materials](uber-shader-materials.md)
- [Unity Conversion Integrations](../assets/unity-conversion-integrations.md)
