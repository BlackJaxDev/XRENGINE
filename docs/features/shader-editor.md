# ImGui Shader Editor

The ImGui editor includes a unified shader editor under **Tools > Shader Editor**. Shader assets expose **Open Shader Editor...** in their asset context menu, and the `XRShader` inspector includes an **Open in Shader Editor** button plus a preset dropdown for common engine shaders such as colored deferred and lit forward shaders. Editing, diagnostics, optimization/locking, analysis, preview instrumentation, variant generation, include/snippet preview, and cross-compilation all live in this one panel.

## Current Capabilities

- Opens and saves GLSL shader source files, including `.frag`, `.vert`, `.comp`, `.mesh`, `.task`, and related aliases.
- Resolves XRENGINE shader includes/snippets before compiling so diagnostics match the engine shader pipeline as closely as possible.
- Runs debounced compiler checks while typing through the existing `ShaderCrossCompiler`/shaderc path.
- Records source edits and panel control changes through the editor undo/redo system. Asset-backed source edits are bound to the shader `TextFile`; editor-only control state uses undo snapshots.
- Displays compiler diagnostics in a table and a read-only annotated source view with underlined diagnostic lines.
- Estimates shader operation cost in the **Analysis** tab, with source selection for original, resolved, generated variant, or preview-instrumented source.
- Builds local completion suggestions from GLSL keywords, builtin functions/types, uniforms, inputs, outputs, functions, and local declarations.
- Requests OpenAI completions from the Responses API using the same OpenAI key/model preferences as the MCP assistant, with `OPENAI_API_KEY` as a fallback.
- Generates preview instrumentation source that writes an expression at a selected line to a fragment output variable.
- Selects engine snippets, relative include files, and absolute include files in the **Includes** tab. The tab shows the exact directive to insert and a disabled read-only preview of the code that will be loaded.
- Generates non-destructive variants in the **Variants** tab. Presets include shadow caster, depth-normal pre-pass, weighted blended OIT, per-pixel linked-list OIT, depth peeling, custom define injection, and optimized locked variants.
- Cross-compiles the original, resolved, generated variant, or preview-instrumented source to SPIR-V, HLSL, and GLSL in the **Cross-Compile** tab.

## Inspector Presets

The `XRShader` ImGui inspector can open the selected asset directly in the unified shader editor. The adjacent **Load Preset** dropdown replaces the shader source with a cloned editable `TextFile` loaded from an engine preset, so preset loading does not mutate the shared engine preset asset. The operation is undoable.

## Includes And Snippets

The **Includes** tab has three source modes:

- **Engine Snippet** lists snippets available through `ShaderSnippets` and inserts `#pragma snippet "Name"`.
- **Relative Include** lists shader-like files next to the current source and under the engine shader roots, then inserts `#include "relative/path"`.
- **Absolute Include** accepts an absolute path and inserts a normalized absolute `#include`.

The preview box is intentionally disabled and read-only. It reflects the text that the editor can currently resolve from the selected snippet/include.

## Variant Generation

Generated variants are written to a separate read-only generated source buffer. The original shader source and file path are not changed by variant generation.

Use **Save** in the **Variants** tab, or **File > Save Generated Variant...**, to write the generated source to a new file. Variant file names default to the source shader name plus a variant suffix, such as `_shadow_caster` or `_depth_normal_pre_pass`.

The optimized locked variant embeds the old shader locking flow into the unified panel. It can mark uniforms as runtime-animated or locked constants, preserve animated name patterns, and run the existing dead-code, constant-evaluation, unused-uniform, and single-use-constant passes before export.

## OpenAI Configuration

The shader editor uses `Engine.EditorPreferences.McpAssistantOpenAiApiKey` and `Engine.EditorPreferences.McpAssistantOpenAiModel`. If the preference key is empty, it reads `OPENAI_API_KEY` from the process environment.

## Preview Notes

The preview tab currently builds and compiles instrumented shader source for expression inspection. It does not yet own a dedicated render target/material preview scene. The intended next step is to route the compiled final or instrumented shader into a small isolated preview pass so the tab can display the rendered result directly.
