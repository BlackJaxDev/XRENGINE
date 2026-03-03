# MCP Assistant System Prompt — Design Document

This document explains the design rationale behind the system prompt used by the MCP Assistant (in-editor AI chat window) when interacting with AI providers (OpenAI, Anthropic, Google, GitHub Models). The prompt is built by `BuildSystemInstructions()` in `McpAssistantWindow.cs`.

## Design Goals

1. **The AI should be able to do anything the user can do** — build scenes, create meshes, configure components, write shaders, set materials, author game assets, write C# game scripts, and visually verify results.
2. **Schema-first mutation** — never guess property names; always discover via `get_component_schema`/`get_component_snapshot` first.
3. **Visual verification** — always screenshot after visual changes; don't trust tool return values alone.
4. **Efficient tool usage** — batch operations, ID caching, transactions for complex work.
5. **Deep engine knowledge** — the AI understands the scene graph, component system, material/shader pipeline, transform hierarchy, and all available tools.

## Prompt Structure

### 1. Identity & Engine Context

Establishes the AI as an expert XREngine scene-authoring assistant with deep knowledge of:
- The engine architecture (Windows-first, C#, .NET 10, OpenGL 4.6, deferred PBR pipeline)
- The full 100+ tool catalog
- Machine context (OS, architecture, date, runtime version)

### 2. Completion Protocol

Ensures the AI:
- Completes ALL steps (no partial work)
- Retries on failure
- Caches IDs from tool responses
- Outputs a structured completion footer with done marker

### 3. Context-First Execution Workflow

A mandatory three-phase pattern for every non-trivial request:

**Phase 1 — GATHER CONTEXT**: Before any mutations, use read-only tools to survey the scene (list_worlds, list_scene_nodes, list_components, get_component_schema, capture_viewport_screenshot), discover type information (search_types, get_type_info, get_derived_types), and fetch DocFX API documentation when introspection tools aren't sufficient. Inspect existing nodes relevant to the request. Collect ALL context before proceeding.

**Phase 2 — PLAN**: From gathered context, create a numbered step-by-step action plan. State it to the user. Identify inter-step dependencies. Surface ambiguities for user clarification.

**Phase 3 — EXECUTE**: Carry out the plan step by step with verification (read-back or screenshot) after each significant mutation. Adapt on failure. Final screenshot for visual tasks.

The prompt explicitly states: "Do NOT skip Phase 1. Gathering context first prevents wasted mutations, avoids guessing property names, and ensures you understand the current scene before changing it."

### 4. DocFX API Documentation Research

The AI is informed it can fetch pages from the DocFX API reference site (http://localhost:8080/ when running) for authoritative engine documentation beyond what introspection tools provide. Key URL patterns:
- API index: `http://localhost:8080/apiref/`
- Type page: `http://localhost:8080/apiref/{Namespace}.{TypeName}.html`
- Architecture docs: `http://localhost:8080/architecture/`
- Feature docs: `http://localhost:8080/features/`

This is useful when the AI needs to understand method behavior, parameter semantics, or architectural patterns that aren't fully captured by get_type_info/get_type_members alone.

### 5. Engine Architecture Knowledge (includes DocFX reference)

Teaches the AI about:

- **Scene Graph**: Worlds → Scenes → SceneNodes → TransformBase + XRComponents
- **Component System**: Categorized list of all major component types (rendering, lighting, camera, audio, physics, animation, UI, networking, VR, debug)
- **Transform System**: Translation/Rotation/Scale, local vs world space, Y-up right-handed coordinates, specialized transform types
- **Material & Shader System**: XRMaterial, ShaderVar uniforms (ShaderFloat, ShaderVector3, etc.), factory methods (CreateLitColorMaterial, CreateUnlitColorMaterialForward), GLSL shader organization
- **Mesh System**: ShapeMeshComponent hierarchy, ModelComponent, CSG boolean operations
- **Prefab System**: Create, instantiate, apply/revert overrides
- **Game Scripting**: scaffold_component, write_game_script, compile, hot-reload

### 6. Core Working Principles

Eight numbered principles that govern all tool usage:
1. Schema-first mutation
2. Verify every mutation
3. Materials must be assigned
4. Camera awareness
5. Use batch operations
6. Transactions for complex work
7. ID caching
8. Type discovery

### 7. Tool Workflow Recipes

Concrete step-by-step recipes for common tasks:
- Create a primitive shape
- Build a scene hierarchy (batch)
- Add and configure a component
- Change material/color
- Modify shader uniforms
- Create custom shader materials
- Set up lighting (directional, point, spot)
- Position the editor camera
- Visual verification
- Transform operations
- Game asset management
- Game script workflow
- Type introspection
- Undo & transactions

### 8. Value Format Reference

Exact JSON formats for passing values to tools:
- Colors: `#RRGGBB` hex or `{R, G, B, A}` object
- Vectors: `{X, Y, Z}` objects
- Enums: string names
- GUIDs: string format
- Booleans and numbers: literals

### 9. Hard Rules

Non-negotiable constraints:
- Materials must be assigned to be visible
- Always screenshot after visual changes
- Never guess property names
- Diagnose visibility issues before claiming success

### 10. Conditional Sections

- **requireToolUse**: Whether to always use tools for scene edits or only when beneficial
- **keepCameraOnWorkingArea**: Auto-camera tracking during scene edits
- **Provider-specific**: Anthropic gets "summarize tool results concisely"

## Key Differences from Previous Prompt

| Aspect | Previous | New |
|--------|----------|-----|
| Identity | "an assistant embedded in the XREngine editor" | "an expert scene-authoring assistant" with explicit capability claims |
| Execution workflow | Not specified | Three-phase context-first pattern: Gather → Plan → Execute |
| DocFX research | Not mentioned | Explicit guidance to fetch DocFX API pages for type documentation |
| Engine knowledge | None | Full architecture section (scene graph, components, transforms, materials, shaders, meshes) |
| Component types | Not listed | Categorized listing of all major types |
| Shader/material | "don't guess uniform names" | Full ShaderVar system explanation, factory method uniforms, custom shader workflow |
| Tool recipes | 6 brief one-liners | 13 detailed multi-step recipes with exact parameters |
| Value formats | Implicit | Explicit reference section |
| Working principles | Scattered rules | 8 numbered principles |
| Camera strategy | "focus_node_in_view or set_editor_camera_view" | Detailed positioning strategy with example coordinates |
| Shader uniform editing | Not covered | Full workflow: snapshot → get_object_properties → evaluate_expression → set_object_property |
| Custom shaders | Not covered | Recipe for writing GLSL files + C# material construction |
| Transactions | Not mentioned | Recipe with begin/commit/rollback pattern |

## Token Budget Consideration

The new prompt is ~3,500 tokens (vs ~1,200 previously). This is justified because:
- It eliminates the need for the AI to waste tokens on discovery tool calls for basic architecture questions
- Concrete recipes reduce trial-and-error tool call chains
- The knowledge pays for itself within 1-2 tool interactions that would otherwise require schema discovery

## Implementation

The prompt is built in `McpAssistantWindow.BuildSystemInstructions()` using `StringBuilder`. It is conditionally assembled based on:
- `provider` (Codex/ClaudeCode/Gemini/GitHub)
- `requireToolUse` (whether tools should always be used)
- `attachMcp` (whether MCP server is connected)
- `keepCameraOnWorkingArea` (auto-camera tracking)
- `isRealtimeSession` (WebSocket realtime mode)
