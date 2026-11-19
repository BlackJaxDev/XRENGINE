# Assistant

The Assistant is the in-editor chat surface for engine questions, diagnosis, and scene or asset authoring. Open it with **Tools > Assistant**. It builds instructions for each request and can attach the editor MCP server.

## Purpose

The assistant is designed for requests that modify or inspect the active editor world. It can help create scene nodes, configure components, author materials and shaders, work with assets, and verify visible results through MCP viewport screenshots when the MCP server is attached.

The prompt directs the model to inspect relevant source, schemas, and current state before making engine-specific claims. It does not claim complete knowledge of the engine. The supplied tools define the capabilities of each request.

## Prompt Model

`McpAssistantWindow.BuildSystemInstructions()` assembles the instructions from runtime context:

- whether MCP is attached,
- whether the assistant should keep the editor camera near the active work area,
- the selected request mode,
- and the saved personal instructions.

The mode selector offers **Auto**, **Explain (read only)**, **Diagnose (read only)**, and **Edit**. Auto selects Explain for a clear explanation question, Edit for a clear change request, and Diagnose for other requests. The selected mode is captured when the request starts. Later UI changes do not add tools to that request. Explain and Diagnose permit only explicit inspection tools that the MCP host marks read only. The editor filters the tool list and checks each call again before execution. Edit can use the attached editor tools under their host permission policy.

For an explanation, the prompt asks for a direct answer, then useful evidence and limits. For a diagnosis, it asks for observed state, confirmed findings, and possible causes. It does not require a plan or an edit. For an edit, the prompt asks the assistant to:

1. Inspect the relevant target and writable schema.
2. Make the requested change under the host permission policy.
3. Verify the changed target and property with read-back. Use a screenshot for a visual claim.

A short plan is useful for complex work. The prompt asks the assistant to state the purpose before a meaningful group of tool calls and summarize useful findings or a change of plan. It asks for short answers with the editor's labels and for new technical terms to be explained when needed.

**Advanced > Personal instructions** accepts up to 8192 characters and saves them in local editor preferences. The request captures the saved text and adds it to the engine instructions as a JSON string. Users can set wording, language, and detail preferences. The text cannot grant tool permission, override evidence rules, or change the completion protocol.

## Engine Source Evidence

`search_engine_help` searches bounded engine source and guide files for literal text. It is not a semantic index or a call graph. `read_engine_help` returns numbered lines from one result, with a file hash, source kind, revision when available, and a build match status. Both tools have read-only MCP permission. The assistant can use them with type, schema, and current scene inspection tools to explain engine behavior. It must separate inspected code, observed editor state, documentation, and inference.

The service can read a source checkout or an optional packaged `EngineHelp` bundle. A checkout reports `binaryMatch: unverified` even when it has a revision, because the checkout can differ from the running editor. The bundle reports `binaryMatch: manifestMatched` only when its manifest editor assembly MVID matches the loaded editor assembly. This checks the manifest identity; it does not prove that each bundled source file matches the binary. A read result includes SHA-256 for the returned file bytes. Search results do not include a file hash until the assistant reads the file.

The source reader assumes a trusted checkout or bundle. It accepts `.cs` files under `src/` or top-level `XREngine.*` directories and `.md` files under `docs/`. It rejects absolute paths, parent segments, and paths that cross a reparse point. Search accepts a literal term of at most 128 characters and returns at most 20 matches. It visits at most 10,000 directories, considers at most 5,000 files, reads at most 64 MiB in total, and skips files above 512 KiB. A read returns at most 120 lines from a file of at most 512 KiB. These checks bound normal reads and reject links. They do not make a hostile filesystem with concurrent changes safe.

The Assistant shows source references from successful `read_engine_help` calls under **Sources** on a reply. **Inspect source** reads a bounded excerpt again in a popup and compares its current hash with the cited hash. If the hash changed, the popup tells the user to ask again. The popup also shows the build match status. These references support the answer; they do not certify that the checkout matches the running editor.

Realtime access has a narrower tool set. Explain and Diagnose expose no realtime capture tool. Realtime requests do not have the MCP source tools. Use standard chat when an answer needs source evidence.

## Mutation Rules

The assistant prompt asks for the relevant target and writable schema before an edit. It asks the assistant to reuse valid IDs from tool results and to keep dependent changes in order.

For a visual claim, the prompt asks for screenshot verification. A mutation result alone does not prove the visible result.

## Connections And Models

The main view has **Assistant**, **Model**, and **Effort** menus. It shows Codex, Claude, Copilot, and Gemini as brands. The model menu uses the selected account or API credential to load available models. It accepts only a model offered by that connection. A saved model that is absent from the current catalog does not enable **Send**.

Effort settings are saved per provider, access mode, and model. **Default** omits the explicit effort override and lets the provider apply its setting. Other menu values come only from the selected model's runtime capability catalog. The request captures its effort choice when it starts. If a catalog change makes a saved effort invalid, **Send** stays disabled until the user selects an advertised level or **Default**.

Subscription access uses the installed Codex app server, Claude Code stream-JSON client, Antigravity ACP client for Gemini, or Copilot CLI JSON-RPC server. Each client owns authentication and generation. XRENGINE does not implement subscription OAuth or call a provider with extracted subscription tokens. Codex, Claude, and Gemini retain explicit API access. Copilot uses its native account connection. The saved provider value `GitHubModels` retains ordinal 3; `Copilot` uses ordinal 4. A saved GitHub Models connection remains visible as retired until the user selects a supported brand.

Google retired personal Gemini CLI access. The Gemini subscription choice therefore uses Antigravity. Its native protocol confirms the selected model but does not report the model that generated the reply or a stable account identifier. Runtime evidence keeps the actual model unknown. The editor explicitly sets `RequireActualModelEvidence` to false for this connection and explains the limit in Manage. Broker runs retain the strict default. `SupportsConversationReplay` is false, so Assistant sends only the current request and its selected context. The main status and Manage popup state that each request starts a new chat. Other clients use an account fingerprint to prevent history reuse after an account change.

**Connect / Manage** contains account actions and API key setup. API mode can paste a key from the clipboard or use `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, or `GEMINI_API_KEY`. The connection status identifies unavailable or incomplete setups. Changing the brand or access mode starts a new chat. Account controls appear only in Manage. Connected clients offer Switch account; Gemini offers Reconnect because its authenticate command can reuse the current account. Advanced contains only executable and technical controls.

The composer keeps the mode selector and request box above **Send** and **Stop**. **+ Add context** controls a viewport screenshot and editor tools. Each selected item has a removable chip. **Advanced** contains personal instructions, response limits, automatic continuation and summarization for API mode, editor tool and camera options, model refresh, diagnostics, and request logging. The advanced content has its own scroll area so the composer stays reachable.

The shader editor uses the same per-brand connections and account model catalogs. Its model menu can select a shader-specific model from the compatible models. A shader completion is a suggestion until the user applies it.

Assistant permits tool results up to 4 MiB, including encoded viewport images. This uses the shared validator's maximum instead of the broker's 256 KiB text default. Larger results fail with a visible size error.

`AssistantModelCatalog` reads the API provider model endpoints. It limits discovery to 30 seconds, 32 pages, 2 MiB per page, and 4096 models. Claude and Gemini pagination is supported. OpenAI does not report endpoint capabilities in its model list, so the catalog filters known chat model families and excludes specialized output models. Realtime models appear only when realtime access is selected. These API model catalogs do not advertise adjustable effort, so **Default** is the only effort choice in API mode. Codex, Claude Code, and Copilot subscription catalogs expose per-model effort values. The runtime checks the selected model against a fresh catalog before it applies an explicit effort. Gemini ACP, Antigravity, API realtime, and other connections without a verified effort control use **Default** only. Codex Assistant accepts models without medium effort when another advertised level or **Default** is selected; shader completion keeps its existing medium-effort requirement.

`McpAssistantWindow.Models` publishes catalog results on the UI thread. A provider, access-mode, or credential change cancels pending discovery and discards its response. Model IDs provide distinct menu item identities when display names match. API credentials stay in request headers and are omitted from diagnostics. Environment-key setup saves an environment reference; pasting or removing a key clears that reference.

The visible title is Assistant. The internal `McpAssistantWindow` name, preference property names, and ImGui window ID remain stable to preserve saved settings and dock layouts.

## Conversation Display

User requests appear in compact bubbles. Assistant text uses the full message width. Each reply shows Working, Completed, More work remains, Stopped, or Failed from its execution result. Tool errors remain visible in the activity summary. Completed means that the request ended; it does not certify that scene changes were verified.

Tool activity stays in chronological groups. Groups identify the purpose of related actions while preserving call order. Expand a group to see the target, action, observed outcome, and verification status. These fields come from the tool arguments and result; a successful mutation call does not become a verified visual result. Failed calls remain visible. Expand **Details** for the technical tool name, input, and raw result. File links and image previews remain outside the collapsed activity group. Copy reply copies the selected response. Stop cancels the active request; actions that already completed remain applied.

**Explain this** is available at supported inspector entries, property controls, and shader errors. It places a captured editor reference in an editable Assistant draft and selects Explain. The user reviews the question and sends it. The reference is not a fresh state reading. An explanation reply offers **How it works** and **Show source** as follow-up drafts. **Show in editor** appears when the captured target can be selected. The Assistant does not send these follow-ups until the user reviews and sends them.

Automatic scrolling follows new output while the reader remains at the bottom. Scrolling up or expanding activity, Details, or a preview stops following. Jump to latest resumes it. Message heights are measured again after a width or font-size change. Parsed message text, tool group labels, and list snapshots are retained until their source changes.

Context opens a view of the selected connection and current attachment choices. It explains the native replay limit or lets the user inspect an existing API summary. Compaction and replay keep bounded tool facts separate from the visible activity labels. Retained facts include source references and evidence IDs, actual outcomes, failures, constraints, assumptions, and open questions. A later summary must not convert an inference or a tool success into independent verification. Context controls do not create project memory or load skills. Attachment chips and composer actions wrap in narrow panels. Enter sends the current draft; Shift+Enter inserts a line break.

Conversation persistence, branching, project memory, skill loading, automatic model review, and per-reply undo remain separate planned work. The panel does not expose controls for those features yet.

## Implementation References

- `XREngine.Editor/UI/Tools/McpAssistantWindow/`
- `XREngine.Editor/UI/Tools/SubscriptionEditorConnection.cs`
- `XREngine.Editor/UI/Tools/CodexEditorConnection.cs`
- `XREngine.AgentRuntimes/`
- `XREngine.Editor/UI/Tools/AssistantModelCatalog.cs`
- `XREngine.UnitTests/Editor/McpAssistantWindowContextTests.cs`
- `docs/developer-guides/ai/mcp-server.md`

