# Voice MCP Bridge

The `VoiceMcpBridgeComponent` enables voice-controlled interaction with the XREngine MCP server. Speak commands into your microphone, and the engine will execute them.

## Architecture

```
┌─────────────────┐    Audio     ┌──────────────────────┐    Text     ┌──────────────────────┐
│ MicrophoneComp  │──────────────│  SpeechToTextComp    │─────────────│  VoiceMcpBridgeComp  │
│                 │  BufferRecv  │  (OpenAI Whisper)    │  TextRecv   │                      │
└─────────────────┘              └──────────────────────┘             └──────────┬───────────┘
                                                                                  │
                                                                           JSON-RPC POST
                                                                                  │
                                                                                  ▼
                                                                     ┌─────────────────────┐
                                                                     │   McpServerHost     │
                                                                     │   localhost:5467    │
                                                                     └─────────────────────┘
```

## Quick Start

```csharp
// 1. Create the component chain on a scene node
var mic = sceneNode.AddComponent<MicrophoneComponent>();
var stt = sceneNode.AddComponent<SpeechToTextComponent>();  // Auto-finds sibling MicrophoneComponent
var bridge = sceneNode.AddComponent<VoiceMcpBridgeComponent>();

// 2. Configure STT (requires API key for cloud provider)
stt.SelectedProvider = ESTTProvider.OpenAI;
stt.ApiKey = "sk-...";
stt.AutoProcess = true;

// 3. Configure bridge
bridge.SpeechToText = stt;
bridge.WakeWord = "hey engine";       // Optional: require wake word
bridge.RequireWakeWord = true;
bridge.ProcessingMode = EVoiceMcpMode.Hybrid;

// 4. Optional: Configure LLM for complex commands
bridge.LlmProvider = ELlmProvider.OpenAI;
bridge.LlmApiKey = "sk-...";
bridge.LlmModel = "gpt-4o-mini";

// 5. Listen for events
bridge.CommandRecognized += (b, text) => Debug.Out($"Command: {text}");
bridge.ResponseReceived += (b, response) => 
{
    if (response.Success)
        Debug.Out($"Result: {response.ContentText}");
    else
        Debug.Out($"Error: {response.Error}");
};

// 6. Start listening
mic.StartCapture();
```

**Or use the factory method for even simpler setup:**

```csharp
var bridge = VoiceMcpBridgeComponent.CreateVoiceMcpChain(
    sceneNode,
    ESTTProvider.OpenAI,
    "sk-stt-key",
    wakeWord: "hey engine",
    llmApiKey: "sk-llm-key"
);

bridge.ResponseReceived += (b, r) => Debug.Out(r.ContentText);
bridge.StartListening();
```

## Processing Modes

| Mode | Description | API Cost | Speed |
|------|-------------|----------|-------|
| `KeywordsOnly` | Pattern matching only | None | Fastest |
| `LlmOnly` | Always use LLM | Per request | Slower |
| `Hybrid` | Keywords first, LLM fallback | Minimal | Balanced |

## Built-in Voice Commands

### Basic Commands
| Voice Command | MCP Tool |
|--------------|----------|
| "undo" / "undo that" | `undo` |
| "redo" / "redo that" | `redo` |
| "delete" / "remove selected" | `delete_selected_nodes` |
| "what's selected?" | `get_selection` |
| "deselect" / "clear selection" | `clear_selection` |

### Primitives
| Voice Command | MCP Tool |
|--------------|----------|
| "create a sphere" | `create_primitive_shape` (Sphere) |
| "add a cube" | `create_primitive_shape` (Cube) |
| "spawn a cone" | `create_primitive_shape` (Cone) |
| "make a box" | `create_primitive_shape` (Box) |

### Scene Management
| Voice Command | MCP Tool |
|--------------|----------|
| "save" / "save world" | `save_world` |
| "load world MyWorld" | `load_world` |
| "list worlds" | `list_worlds` |
| "show hierarchy" | `list_scene_nodes` |
| "select Player" | `select_node_by_name` |

Legacy tool names such as `get_scene_hierarchy`, `select_scene_node`, and `delete_selected` are still accepted by MCP alias mapping for backward compatibility.

### Play Mode
| Voice Command | MCP Tool |
|--------------|----------|
| "play" / "start game" | `enter_play_mode` |
| "stop" / "exit play mode" | `exit_play_mode` |

## Wake Word

Enable wake word to prevent the bridge from processing every utterance:

```csharp
bridge.WakeWord = "hey engine";
bridge.RequireWakeWord = true;
bridge.WakeWordTimeout = TimeSpan.FromSeconds(30);
```

When enabled:
1. Say "hey engine" to activate listening
2. Speak your command within the timeout window
3. After timeout, you need to say the wake word again

You can also combine them: "Hey engine, create a sphere"

## Custom Commands

Register your own voice commands with regex patterns:

```csharp
// Simple command
bridge.RegisterCommand(
    patterns: ["^teleport home$", "^go home$"],
    toolName: "teleport_player",
    argumentBuilder: null
);

// Command with captured arguments
bridge.RegisterCommand(
    patterns: ["^set color to (red|green|blue|yellow)$"],
    toolName: "set_material_color",
    argumentBuilder: match => new JsonObject 
    { 
        ["color"] = match.Groups[1].Value 
    }
);

// Complex pattern with multiple captures
bridge.RegisterCommand(
    patterns: [@"^move (\w+) to (-?\d+),?\s*(-?\d+),?\s*(-?\d+)$"],
    toolName: "set_transform",
    argumentBuilder: match => new JsonObject
    {
        ["nodeName"] = match.Groups[1].Value,
        ["position"] = new JsonObject
        {
            ["x"] = float.Parse(match.Groups[2].Value),
            ["y"] = float.Parse(match.Groups[3].Value),
            ["z"] = float.Parse(match.Groups[4].Value)
        }
    }
);
```

## LLM Integration

For commands that don't match keywords, the bridge can use an LLM to classify intent:

### OpenAI
```csharp
bridge.LlmProvider = ELlmProvider.OpenAI;
bridge.LlmApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
bridge.LlmModel = "gpt-4o-mini";  // Cost-effective
```

### Anthropic
```csharp
bridge.LlmProvider = ELlmProvider.Anthropic;
bridge.LlmApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
bridge.LlmModel = "claude-3-haiku-20240307";  // Fast and cheap
```

## Events

```csharp
// Wake word detected
bridge.WakeWordDetected += (b) => PlaySound("listening.wav");

// Command recognized (before processing)
bridge.CommandRecognized += (b, text) => ShowUI($"Processing: {text}");

// Tool call about to be sent
bridge.ToolCallSending += (b, tool, args) => 
    Debug.Out($"Calling {tool} with {args}");

// Response received
bridge.ResponseReceived += (b, response) =>
{
    if (response.Success)
        ShowNotification(response.ContentText);
    else
        ShowError(response.Error);
};

// Transcription ignored (below confidence, no wake word, etc.)
bridge.TranscriptionIgnored += (b, text, reason) =>
    Debug.Out($"Ignored '{text}': {reason}");

// Error occurred
bridge.ErrorOccurred += (b, error) => ShowError(error);
```

## Configuration Reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `SpeechToText` | `SpeechToTextComponent` | null | Required STT component |
| `WakeWord` | `string` | "" | Wake word phrase (empty = disabled) |
| `RequireWakeWord` | `bool` | false | Require wake word for commands |
| `WakeWordTimeout` | `TimeSpan` | 30s | How long wake word stays active |
| `MinConfidence` | `float` | 0.7 | Minimum STT confidence threshold |
| `ProcessingMode` | `EVoiceMcpMode` | Hybrid | Keywords, LLM, or Hybrid |
| `McpHost` | `string` | "localhost" | MCP server hostname |
| `McpPort` | `int` | 5467 | MCP server port |
| `LlmProvider` | `ELlmProvider` | OpenAI | LLM provider for intent |
| `LlmApiKey` | `string` | "" | API key for LLM provider |
| `LlmModel` | `string` | "gpt-4o-mini" | Model name for LLM |
| `SpeakResponses` | `bool` | false | TTS for responses (future) |

## Tips

1. **Start with KeywordsOnly** mode to test without API costs
2. **Use short wake words** that are unlikely to appear in normal speech
3. **Adjust MinConfidence** based on your microphone quality
4. **Register custom commands** for your specific workflow
5. **Use Hybrid mode** for the best balance of speed and flexibility

## Troubleshooting

### Commands not recognized
- Check `MinConfidence` threshold
- Enable `TranscriptionIgnored` event to see why
- Try speaking more clearly or adjusting microphone

### Wake word not detected
- Speak the wake word clearly
- Check the wake word spelling matches what you configured
- Try a simpler wake word

### LLM not working
- Verify API key is set correctly
- Check `ErrorOccurred` event for API errors
- Ensure network connectivity to LLM provider

### MCP server not responding
- Verify MCP server is enabled in Editor Preferences
- Check `McpHost` and `McpPort` settings
- Test with: `curl -X POST http://localhost:5467/mcp/ -d '{"jsonrpc":"2.0","id":"1","method":"tools/list"}'`
