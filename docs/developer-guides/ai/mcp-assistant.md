# MCP Assistant

The MCP Assistant is the in-editor chat surface for AI-assisted scene and asset authoring. It builds provider-specific instructions, optionally attaches the editor MCP server, and guides the model toward schema-first, verifiable editor mutations.

This feature doc promotes the implemented behavior described by `docs/work/design/UI/mcp-assistant-system-prompt.md`.

## Purpose

The assistant is designed for requests that modify or inspect the active editor world. It can help create scene nodes, configure components, author materials and shaders, work with assets, and verify visible results through MCP viewport screenshots when the MCP server is attached.

The prompt is intentionally engine-aware. It teaches the model the scene graph, component model, transform conventions, material/shader model, common tool recipes, value formats, and completion expectations before any tool calls are made.

## Prompt Model

`McpAssistantWindow.BuildSystemInstructions()` assembles the instructions from runtime context:

- selected provider,
- whether the request likely needs tool use,
- whether MCP is attached,
- whether the assistant should keep the editor camera near the active work area,
- and whether the session is realtime.

The generated prompt emphasizes three phases:

1. Gather context with read-only tools and type/schema inspection.
2. Plan the edit with dependencies and any ambiguity called out.
3. Execute mutations with read-back or screenshot verification.

## Mutation Rules

The assistant prompt treats component mutation as schema-first. For non-trivial edits it should discover component schemas and snapshots before assigning values, cache IDs returned by tools, and batch operations where possible.

For visual work the prompt requires verification by viewport screenshot instead of trusting mutation return values. This is especially important for material, lighting, transform, and camera changes where a valid API response can still produce an invisible or incorrect result.

## Provider Support

The window builds instructions for multiple providers, including Codex, Claude Code, Gemini, and GitHub Models. Provider-specific sections adjust details such as realtime behavior and how aggressively tool results should be summarized.

## Implementation References

- `XREngine.Editor/UI/Tools/McpAssistantWindow.cs`
- `XREngine.UnitTests/Editor/McpAssistantWindowContextTests.cs`
- `docs/developer-guides/ai/mcp-server.md`

