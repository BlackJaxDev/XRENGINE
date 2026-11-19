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
- Requests completions through the selected assistant connection. API mode uses OpenAI Responses; subscription mode uses the selected installed client and its account sign-in.
- Generates preview instrumentation source that writes an expression at a selected line to a fragment output variable.
- Selects engine snippets, relative include files, and absolute include files in the **Includes** tab. The tab shows the exact directive to insert and a disabled read-only preview of the code that will be loaded.
- Generates non-destructive variants in the **Variants** tab. Presets include shadow caster, depth-normal pre-pass, weighted blended OIT, per-pixel linked-list OIT, depth peeling, custom define injection, and optimized locked variants.
- Cross-compiles the original, resolved, generated variant, or preview-instrumented source to SPIR-V, HLSL, and GLSL in the **Cross-Compile** tab.

## Inspector Presets

Use `.comp` for compute shaders discovered by automatic asset import. The `.cs`
suffix is also used by C# gameplay scripts, so automatic import does not claim
it as a shader. Explicitly typed `XRShader` loads still accept that alias.

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

## Completion Connection

The shader editor uses the assistant connection. API mode remains the default
for existing preferences. It uses
`Engine.EditorPreferences.McpAssistantOpenAiApiKey` and
`Engine.EditorPreferences.McpAssistantOpenAiModel`. If the preference key is
empty, it reads `OPENAI_API_KEY` from the process environment.

Select **Subscription** access and a brand to use Codex, Claude, Gemini, or Copilot. The model menu uses that client's account catalog. Open **Manage account** for authentication. Advanced contains only the executable picker.

Gemini uses Google's Antigravity client. The old Gemini CLI no longer supports personal Google plans. Antigravity confirms the selected model but does not report the model that generated the reply. Each shader request sends the current shader and instructions as a fresh session.

Codex uses the installed Codex CLI with a
ChatGPT account. Use **Sign in to ChatGPT** in the editor to start the native
sign-in flow, or **Cancel** to stop it. XRENGINE keeps this sign-in under
`%LOCALAPPDATA%/XREngine/AgentRuntimes/Codex/profile`. The broker and editor
share it for the same Windows user. It is separate from a normal Codex CLI
login. A normal `codex login` command uses the dedicated profile only if that
terminal first sets `CODEX_HOME` to this directory. Do not copy account
tokens. The current pinned Codex CLI version is `0.167.0-alpha.3.2`.

The shader editor shows a model menu for the signed-in account. Select a model
from that menu; the editor accepts only models that support its response setting.
The shader editor uses the same per-brand connections as **Tools > Assistant**, and
can keep a separate model selection for shader completions.

Subscription mode uses the exact model offered by the signed-in account's
runtime catalog. It does not use an API key or fall back to API mode. Completion
text remains a suggestion until you apply it in the shader editor. CLI access does
not expose enforceable native turn, retry, or output-token caps in this
integration; the editor still bounds the request and can cancel it.

## Preview Notes

The preview tab currently builds and compiles instrumented shader source for expression inspection. It does not yet own a dedicated render target/material preview scene. The intended next step is to route the compiled final or instrumented shader into a small isolated preview pass so the tab can display the rendered result directly.
