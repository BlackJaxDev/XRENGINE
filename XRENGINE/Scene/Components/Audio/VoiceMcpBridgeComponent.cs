using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Components
{
    /// <summary>
    /// Bridges voice input from a <see cref="SpeechToTextComponent"/> to the MCP server,
    /// enabling voice-controlled interactions with the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component listens for transcribed speech and translates it into MCP tool calls.
    /// It supports three modes of operation:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Keywords</b>: Simple pattern matching for common commands (fastest, no API cost)</description></item>
    /// <item><description><b>LLM</b>: Sends transcription to an LLM for intent classification (most flexible)</description></item>
    /// <item><description><b>Hybrid</b>: Tries keywords first, falls back to LLM for unrecognized commands</description></item>
    /// </list>
    /// <para>
    /// Optionally supports wake word detection to avoid processing every utterance.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Basic setup with wake word
    /// var bridge = sceneNode.AddComponent&lt;VoiceMcpBridgeComponent&gt;();
    /// bridge.SpeechToText = sttComponent;
    /// bridge.WakeWord = "hey engine";
    /// bridge.ProcessingMode = EVoiceMcpMode.Hybrid;
    /// bridge.Enabled = true;
    /// 
    /// // Listen for responses
    /// bridge.ResponseReceived += (comp, response) => Debug.Audio($"MCP: {response}");
    /// </code>
    /// </example>
    public partial class VoiceMcpBridgeComponent : XRComponent
    {
        #region Fields

        private SpeechToTextComponent? _speechToText;
        private string _wakeWord = string.Empty;
        private bool _requireWakeWord = false;
        private float _minConfidence = 0.7f;
        private EVoiceMcpMode _processingMode = EVoiceMcpMode.Hybrid;
        private string _mcpHost = "localhost";
        private int _mcpPort = 5467;
        private string _llmApiKey = string.Empty;
        private ELlmProvider _llmProvider = ELlmProvider.OpenAI;
        private string _llmModel = "gpt-4o-mini";
        private bool _speakResponses = false;
        private TextToSpeechComponent? _textToSpeech;
        private bool _isProcessing = false;
        private DateTime _lastWakeWordTime = DateTime.MinValue;
        private TimeSpan _wakeWordTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpClient = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly List<VoiceCommand> _customCommands = [];
        private static readonly List<VoiceCommand> _builtInCommands = BuildDefaultCommands();

        #endregion

        #region Properties

        /// <summary>
        /// The speech-to-text component that provides transcribed voice input.
        /// </summary>
        public SpeechToTextComponent? SpeechToText
        {
            get => _speechToText;
            set
            {
                if (_speechToText == value)
                    return;

                if (_speechToText is not null)
                {
                    _speechToText.TextReceived -= OnTextReceived;
                    _speechToText.ErrorOccurred -= OnSttError;
                }

                _speechToText = value;

                if (_speechToText is not null)
                {
                    _speechToText.TextReceived += OnTextReceived;
                    _speechToText.ErrorOccurred += OnSttError;
                }
            }
        }

        /// <summary>
        /// Optional wake word that must be spoken before commands are processed.
        /// Set to empty string to disable wake word detection.
        /// </summary>
        /// <example>"hey engine", "computer", "jarvis"</example>
        public string WakeWord
        {
            get => _wakeWord;
            set => SetField(ref _wakeWord, value?.Trim().ToLowerInvariant() ?? string.Empty);
        }

        /// <summary>
        /// When true, commands are only processed after the wake word is detected.
        /// The wake word activates listening for <see cref="WakeWordTimeout"/>.
        /// </summary>
        public bool RequireWakeWord
        {
            get => _requireWakeWord;
            set => SetField(ref _requireWakeWord, value);
        }

        /// <summary>
        /// How long after wake word detection the component remains active for commands.
        /// </summary>
        public TimeSpan WakeWordTimeout
        {
            get => _wakeWordTimeout;
            set => SetField(ref _wakeWordTimeout, value);
        }

        /// <summary>
        /// Minimum confidence threshold for processing transcriptions.
        /// Transcriptions below this threshold are ignored.
        /// </summary>
        public float MinConfidence
        {
            get => _minConfidence;
            set => SetField(ref _minConfidence, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>
        /// The processing mode for interpreting voice commands.
        /// </summary>
        public EVoiceMcpMode ProcessingMode
        {
            get => _processingMode;
            set => SetField(ref _processingMode, value);
        }

        /// <summary>
        /// The hostname of the MCP server. Defaults to "localhost".
        /// </summary>
        public string McpHost
        {
            get => _mcpHost;
            set => SetField(ref _mcpHost, value ?? "localhost");
        }

        /// <summary>
        /// The port of the MCP server. Defaults to 5467.
        /// </summary>
        public int McpPort
        {
            get => _mcpPort;
            set => SetField(ref _mcpPort, value);
        }

        /// <summary>
        /// API key for LLM provider (required for LLM and Hybrid modes).
        /// </summary>
        public string LlmApiKey
        {
            get => _llmApiKey;
            set => SetField(ref _llmApiKey, value ?? string.Empty);
        }

        /// <summary>
        /// The LLM provider to use for intent classification.
        /// </summary>
        public ELlmProvider LlmProvider
        {
            get => _llmProvider;
            set => SetField(ref _llmProvider, value);
        }

        /// <summary>
        /// The model name to use with the LLM provider.
        /// </summary>
        public string LlmModel
        {
            get => _llmModel;
            set => SetField(ref _llmModel, value ?? "gpt-4o-mini");
        }

        /// <summary>
        /// When true, uses text-to-speech to speak MCP responses back to the user.
        /// </summary>
        public bool SpeakResponses
        {
            get => _speakResponses;
            set => SetField(ref _speakResponses, value);
        }

        /// <summary>
        /// The text-to-speech component used to speak responses.
        /// If not set and SpeakResponses is true, will look for a sibling TextToSpeechComponent.
        /// </summary>
        public TextToSpeechComponent? TextToSpeech
        {
            get => _textToSpeech ??= GetSiblingComponent<TextToSpeechComponent>(false);
            set => SetField(ref _textToSpeech, value);
        }

        /// <summary>
        /// Whether the component is currently processing a command.
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// Whether the wake word is currently active (within timeout).
        /// </summary>
        public bool IsWakeWordActive => !RequireWakeWord || 
            (DateTime.Now - _lastWakeWordTime) < WakeWordTimeout;

        /// <summary>
        /// Read-only list of custom voice commands registered with this component.
        /// </summary>
        public IReadOnlyList<VoiceCommand> CustomCommands => _customCommands;

        #endregion

        #region Events

        /// <summary>
        /// Raised when a voice command is recognized and about to be processed.
        /// </summary>
        public event Action<VoiceMcpBridgeComponent, string>? CommandRecognized;

        /// <summary>
        /// Raised when the wake word is detected.
        /// </summary>
        public event Action<VoiceMcpBridgeComponent>? WakeWordDetected;

        /// <summary>
        /// Raised when an MCP tool call is about to be sent.
        /// </summary>
        public event Action<VoiceMcpBridgeComponent, string, JsonObject?>? ToolCallSending;

        /// <summary>
        /// Raised when an MCP response is received.
        /// </summary>
        public event Action<VoiceMcpBridgeComponent, McpVoiceResponse>? ResponseReceived;

        /// <summary>
        /// Raised when an error occurs during processing.
        /// </summary>
        public event Action<VoiceMcpBridgeComponent, string>? ErrorOccurred;

        /// <summary>
        /// Raised when a transcription is ignored (below confidence, no wake word, etc.).
        /// </summary>
        public event Action<VoiceMcpBridgeComponent, string, string>? TranscriptionIgnored;

        #endregion

        #region Factory Methods

        /// <summary>
        /// Creates a complete voice MCP chain on the specified scene node.
        /// </summary>
        /// <param name="node">The scene node to attach components to.</param>
        /// <param name="sttProvider">The speech-to-text provider to use.</param>
        /// <param name="sttApiKey">API key for the STT provider.</param>
        /// <param name="wakeWord">Optional wake word (empty to disable).</param>
        /// <param name="llmApiKey">Optional LLM API key for Hybrid/LLM modes.</param>
        /// <param name="llmProvider">LLM provider to use.</param>
        /// <param name="ttsProvider">Optional TTS provider for spoken responses.</param>
        /// <param name="ttsApiKey">Optional API key for TTS provider (defaults to llmApiKey if not provided).</param>
        /// <returns>The configured VoiceMcpBridgeComponent.</returns>
        /// <example>
        /// <code>
        /// var bridge = VoiceMcpBridgeComponent.CreateVoiceMcpChain(
        ///     mySceneNode,
        ///     ESTTProvider.OpenAI,
        ///     "sk-stt-key",
        ///     wakeWord: "hey engine",
        ///     llmApiKey: "sk-llm-key",
        ///     ttsProvider: ETTSProvider.OpenAI
        /// );
        /// bridge.ResponseReceived += HandleResponse;
        /// </code>
        /// </example>
        public static VoiceMcpBridgeComponent CreateVoiceMcpChain(
            SceneNode node,
            ESTTProvider sttProvider,
            string sttApiKey,
            string wakeWord = "",
            string llmApiKey = "",
            ELlmProvider llmProvider = ELlmProvider.OpenAI,
            ETTSProvider? ttsProvider = null,
            string? ttsApiKey = null)
        {
            // Create microphone component first (STT requires it as sibling)
            node.AddComponent<MicrophoneComponent>();
            
            // Create and configure STT component (auto-finds sibling MicrophoneComponent)
            var stt = node.AddComponent<SpeechToTextComponent>()!;
            stt.SelectedProvider = sttProvider;
            stt.ApiKey = sttApiKey;
            stt.AutoProcess = true;
            stt.EnableVAD = true;

            // Create and configure bridge
            var bridge = node.AddComponent<VoiceMcpBridgeComponent>()!;
            bridge.SpeechToText = stt;
            
            if (!string.IsNullOrEmpty(wakeWord))
            {
                bridge.WakeWord = wakeWord;
                bridge.RequireWakeWord = true;
            }

            if (!string.IsNullOrEmpty(llmApiKey))
            {
                bridge.LlmApiKey = llmApiKey;
                bridge.LlmProvider = llmProvider;
                bridge.ProcessingMode = EVoiceMcpMode.Hybrid;
            }
            else
            {
                bridge.ProcessingMode = EVoiceMcpMode.KeywordsOnly;
            }

            // Create TTS component if provider is specified
            if (ttsProvider.HasValue)
            {
                // Also need an AudioSourceComponent for TTS playback
                node.AddComponent<AudioSourceComponent>();
                
                var tts = node.AddComponent<TextToSpeechComponent>()!;
                tts.SelectedProvider = ttsProvider.Value;
                tts.ApiKey = ttsApiKey ?? llmApiKey; // Use LLM key as fallback for OpenAI
                tts.AutoPlay = true;
                bridge.TextToSpeech = tts;
                bridge.SpeakResponses = true;
            }

            return bridge;
        }

        /// <summary>
        /// Starts voice capture on the microphone component.
        /// </summary>
        public void StartListening()
        {
            SpeechToText?.Microphone?.StartCapture();
        }

        /// <summary>
        /// Stops voice capture on the microphone component.
        /// </summary>
        public void StopListening()
        {
            SpeechToText?.Microphone?.StopCapture();
        }

        /// <summary>
        /// Gets whether the microphone is currently capturing audio.
        /// </summary>
        public bool IsListening => SpeechToText?.Microphone?.IsCapturing ?? false;

        #endregion

        #region Lifecycle

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            // Re-attach events if STT is already set
            if (_speechToText is not null)
            {
                _speechToText.TextReceived -= OnTextReceived;
                _speechToText.TextReceived += OnTextReceived;
                _speechToText.ErrorOccurred -= OnSttError;
                _speechToText.ErrorOccurred += OnSttError;
            }
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (_speechToText is not null)
            {
                _speechToText.TextReceived -= OnTextReceived;
                _speechToText.ErrorOccurred -= OnSttError;
            }
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _httpClient.Dispose();
        }

        #endregion

        #region Command Registration

        /// <summary>
        /// Registers a custom voice command with pattern matching.
        /// </summary>
        /// <param name="patterns">Regex patterns to match (case-insensitive)</param>
        /// <param name="toolName">The MCP tool to call when matched</param>
        /// <param name="argumentBuilder">Function to build tool arguments from regex match</param>
        public void RegisterCommand(string[] patterns, string toolName, Func<Match, JsonObject?>? argumentBuilder = null)
        {
            _customCommands.Add(new VoiceCommand(patterns, toolName, argumentBuilder));
        }

        /// <summary>
        /// Removes all custom commands.
        /// </summary>
        public void ClearCustomCommands()
        {
            _customCommands.Clear();
        }

        #endregion

        #region Event Handlers

        private void OnTextReceived((SpeechToTextComponent Component, string Text, float Confidence) args)
        {
            if (!IsActive)
                return;

            var text = args.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Check confidence threshold
            if (args.Confidence < MinConfidence)
            {
                TranscriptionIgnored?.Invoke(this, text, $"Confidence {args.Confidence:P0} below threshold {MinConfidence:P0}");
                return;
            }

            var lowerText = text.ToLowerInvariant();

            // Check for wake word
            if (!string.IsNullOrEmpty(WakeWord))
            {
                if (lowerText.Contains(WakeWord))
                {
                    _lastWakeWordTime = DateTime.Now;
                    WakeWordDetected?.Invoke(this);
                    Debug.Audio($"[VoiceMCP] Wake word detected: \"{WakeWord}\"");

                    // Remove wake word from text to process the rest
                    var idx = lowerText.IndexOf(WakeWord);
                    text = text.Remove(idx, WakeWord.Length).Trim();
                    lowerText = text.ToLowerInvariant();

                    if (string.IsNullOrWhiteSpace(text))
                        return; // Only wake word was spoken
                }
                else if (RequireWakeWord && !IsWakeWordActive)
                {
                    TranscriptionIgnored?.Invoke(this, text, "Wake word not active");
                    return;
                }
            }

            // Process the command
            _ = ProcessCommandAsync(text);
        }

        private void OnSttError((SpeechToTextComponent Component, string Error) args)
        {
            ErrorOccurred?.Invoke(this, $"STT Error: {args.Error}");
        }

        #endregion

        #region Command Processing

        private async Task ProcessCommandAsync(string text)
        {
            if (_isProcessing)
            {
                Debug.Audio("[VoiceMCP] Already processing a command, ignoring.");
                return;
            }

            _isProcessing = true;
            CommandRecognized?.Invoke(this, text);
            Debug.Audio($"[VoiceMCP] Processing command: \"{text}\"");

            try
            {
                switch (ProcessingMode)
                {
                    case EVoiceMcpMode.KeywordsOnly:
                        await ProcessWithKeywordsAsync(text);
                        break;

                    case EVoiceMcpMode.LlmOnly:
                        await ProcessWithLlmAsync(text);
                        break;

                    case EVoiceMcpMode.Hybrid:
                        if (!await ProcessWithKeywordsAsync(text))
                            await ProcessWithLlmAsync(text);
                        break;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex.Message);
                Debug.Audio($"[VoiceMCP] Error: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private async Task<bool> ProcessWithKeywordsAsync(string text)
        {
            var lowerText = text.ToLowerInvariant();

            // Check custom commands first
            foreach (var cmd in _customCommands)
            {
                if (cmd.TryMatch(lowerText, out var toolName, out var args))
                {
                    await SendMcpToolCallAsync(toolName, args);
                    return true;
                }
            }

            // Check built-in commands
            foreach (var cmd in _builtInCommands)
            {
                if (cmd.TryMatch(lowerText, out var toolName, out var args))
                {
                    await SendMcpToolCallAsync(toolName, args);
                    return true;
                }
            }

            Debug.Audio($"[VoiceMCP] No keyword match for: \"{text}\"");
            return false;
        }

        private async Task ProcessWithLlmAsync(string text)
        {
            if (string.IsNullOrEmpty(LlmApiKey))
            {
                ErrorOccurred?.Invoke(this, "LLM API key not configured");
                return;
            }

            try
            {
                var toolCall = await ClassifyIntentWithLlmAsync(text);
                if (toolCall is not null)
                {
                    await SendMcpToolCallAsync(toolCall.ToolName, toolCall.Arguments);
                }
                else
                {
                    Debug.Audio($"[VoiceMCP] LLM could not classify: \"{text}\"");
                    ErrorOccurred?.Invoke(this, $"Could not understand command: {text}");
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"LLM Error: {ex.Message}");
            }
        }

        #endregion

        #region MCP Communication

        /// <summary>
        /// Sends a tool call to the MCP server.
        /// </summary>
        public async Task<McpVoiceResponse> SendMcpToolCallAsync(string toolName, JsonObject? arguments = null)
        {
            ToolCallSending?.Invoke(this, toolName, arguments);
            Debug.Audio($"[VoiceMCP] Calling tool: {toolName}");

            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString(),
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments ?? new JsonObject()
                }
            };

            var json = request.ToJsonString(_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"http://{McpHost}:{McpPort}/mcp/";

            try
            {
                var httpResponse = await _httpClient.PostAsync(url, content);
                var responseText = await httpResponse.Content.ReadAsStringAsync();

                var response = new McpVoiceResponse
                {
                    Success = httpResponse.IsSuccessStatusCode,
                    ToolName = toolName,
                    RawResponse = responseText
                };

                if (httpResponse.IsSuccessStatusCode)
                {
                    try
                    {
                        var responseJson = JsonNode.Parse(responseText);
                        response.Result = responseJson?["result"];
                        response.Error = responseJson?["error"]?.ToString();

                        // Extract content text if available
                        var resultContent = response.Result?["content"];
                        if (resultContent is JsonArray contentArray && contentArray.Count > 0)
                        {
                            var firstContent = contentArray[0];
                            if (firstContent?["type"]?.ToString() == "text")
                            {
                                response.ContentText = firstContent["text"]?.ToString();
                            }
                        }
                    }
                    catch
                    {
                        // Response wasn't valid JSON
                    }
                }
                else
                {
                    response.Error = $"HTTP {(int)httpResponse.StatusCode}: {httpResponse.ReasonPhrase}";
                }

                ResponseReceived?.Invoke(this, response);

                if (SpeakResponses && !string.IsNullOrEmpty(response.ContentText))
                {
                    await SpeakTextAsync(response.ContentText);
                }

                return response;
            }
            catch (Exception ex)
            {
                var response = new McpVoiceResponse
                {
                    Success = false,
                    ToolName = toolName,
                    Error = ex.Message
                };
                ResponseReceived?.Invoke(this, response);
                return response;
            }
        }

        /// <summary>
        /// Lists available tools from the MCP server.
        /// </summary>
        public async Task<string[]> ListMcpToolsAsync()
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Guid.NewGuid().ToString(),
                ["method"] = "tools/list"
            };

            var json = request.ToJsonString(_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"http://{McpHost}:{McpPort}/mcp/";

            try
            {
                var httpResponse = await _httpClient.PostAsync(url, content);
                var responseText = await httpResponse.Content.ReadAsStringAsync();
                var responseJson = JsonNode.Parse(responseText);

                var tools = responseJson?["result"]?["tools"]?.AsArray();
                if (tools is null)
                    return [];

                return [.. tools
                    .Select(t => t?["name"]?.ToString())
                    .Where(n => n is not null)
                    .Cast<string>()];
            }
            catch
            {
                return [];
            }
        }

        #endregion

        #region LLM Integration

        private async Task<LlmToolCall?> ClassifyIntentWithLlmAsync(string text)
        {
            var tools = await ListMcpToolsAsync();
            if (tools.Length == 0)
            {
                Debug.Audio("[VoiceMCP] No MCP tools available");
                return null;
            }

            var systemPrompt = BuildLlmSystemPrompt(tools);
            var userPrompt = $"User command: \"{text}\"\n\nRespond with a JSON object containing \"tool\" and \"arguments\" fields, or {{\"tool\": null}} if no tool matches.";

            string? responseJson = LlmProvider switch
            {
                ELlmProvider.OpenAI => await CallOpenAiAsync(systemPrompt, userPrompt),
                ELlmProvider.Anthropic => await CallAnthropicAsync(systemPrompt, userPrompt),
                _ => null
            };

            if (string.IsNullOrEmpty(responseJson))
                return null;

            try
            {
                // Extract JSON from response (handle markdown code blocks)
                responseJson = ExtractJsonFromResponse(responseJson);
                var parsed = JsonNode.Parse(responseJson);
                var toolName = parsed?["tool"]?.ToString();

                if (string.IsNullOrEmpty(toolName))
                    return null;

                var arguments = parsed?["arguments"]?.AsObject() ?? new JsonObject();
                return new LlmToolCall(toolName, arguments);
            }
            catch
            {
                Debug.Audio($"[VoiceMCP] Failed to parse LLM response: {responseJson}");
                return null;
            }
        }

        private string BuildLlmSystemPrompt(string[] tools)
        {
            return $@"You are a voice command interpreter for a 3D game engine. 
Your job is to map natural language commands to MCP tool calls.

Available tools: {string.Join(", ", tools)}

Common tool mappings:
- ""create/add/spawn a sphere/cube/etc"" -> create_primitive_shape
- ""select X"" -> select_scene_node
- ""delete/remove selected"" -> delete_selected
- ""undo"" -> undo
- ""redo"" -> redo
- ""save"" -> save_world
- ""load world X"" -> load_world
- ""list worlds"" -> list_worlds
- ""move X to Y"" -> set_property (Transform)
- ""rotate X"" -> set_property (Transform)
- ""scale X"" -> set_property (Transform)
- ""what is selected"" -> get_selection
- ""describe scene"" -> get_scene_hierarchy

Respond ONLY with valid JSON in this format:
{{""tool"": ""tool_name"", ""arguments"": {{...}}}}

If the command doesn't match any tool, respond with:
{{""tool"": null}}";
        }

        private async Task<string?> CallOpenAiAsync(string systemPrompt, string userPrompt)
        {
            var request = new JsonObject
            {
                ["model"] = LlmModel,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt }
                },
                ["temperature"] = 0.1,
                ["max_tokens"] = 500
            };

            var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = content
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {LlmApiKey}");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Debug.Audio($"[VoiceMCP] OpenAI error: {responseText}");
                return null;
            }

            var responseJson = JsonNode.Parse(responseText);
            return responseJson?["choices"]?[0]?["message"]?["content"]?.ToString();
        }

        private async Task<string?> CallAnthropicAsync(string systemPrompt, string userPrompt)
        {
            var request = new JsonObject
            {
                ["model"] = string.IsNullOrEmpty(LlmModel) ? "claude-3-haiku-20240307" : LlmModel,
                ["system"] = systemPrompt,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt }
                },
                ["max_tokens"] = 500
            };

            var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = content
            };
            httpRequest.Headers.Add("x-api-key", LlmApiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Debug.Audio($"[VoiceMCP] Anthropic error: {responseText}");
                return null;
            }

            var responseJson = JsonNode.Parse(responseText);
            return responseJson?["content"]?[0]?["text"]?.ToString();
        }

        private static string ExtractJsonFromResponse(string response)
        {
            // Handle markdown code blocks
            var match = Regex.Match(response, @"```(?:json)?\s*([\s\S]*?)```");
            if (match.Success)
                return match.Groups[1].Value.Trim();

            // Try to find JSON object directly
            var start = response.IndexOf('{');
            var end = response.LastIndexOf('}');
            if (start >= 0 && end > start)
                return response[start..(end + 1)];

            return response;
        }

        #endregion

        #region Text-to-Speech

        private async Task SpeakTextAsync(string text)
        {
            var tts = TextToSpeech;
            if (tts == null)
            {
                Debug.Audio($"[VoiceMCP] No TTS component available, would speak: {text}");
                return;
            }

            try
            {
                Debug.Audio($"[VoiceMCP] Speaking: {text}");
                await tts.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                Debug.Audio($"[VoiceMCP] TTS error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"TTS error: {ex.Message}");
            }
        }

        #endregion

        #region Built-in Commands

        private static List<VoiceCommand> BuildDefaultCommands()
        {
            return
            [
                // Undo/Redo
                new(["^undo$", "^undo that$", "^undo last$"], "undo"),
                new(["^redo$", "^redo that$"], "redo"),

                // Selection
                new(["^delete(?: selected)?$", "^remove(?: selected)?$", "^delete (?:that|this|it)$"], "delete_selected"),
                new(["^what(?:'s| is) selected\\??$", "^get selection$", "^show selection$"], "get_selection"),
                new(["^deselect(?: all)?$", "^clear selection$"], "clear_selection"),

                // Primitives
                new(
                    ["^(?:create|add|spawn|make)(?: a)? (sphere|cube|box|plane|cylinder|cone|capsule|torus)$"],
                    "create_primitive_shape",
                    m => new JsonObject { ["shapeType"] = NormalizeShapeType(m.Groups[1].Value) }
                ),

                // Save/Load
                new(["^save(?: world)?$", "^save (?:the )?scene$"], "save_world"),
                new(
                    ["^(?:load|open)(?: world)? (.+)$"],
                    "load_world",
                    m => new JsonObject { ["worldName"] = m.Groups[1].Value.Trim() }
                ),
                new(["^list worlds$", "^show worlds$", "^what worlds(?:are there)?\\??$"], "list_worlds"),

                // Scene hierarchy
                new(["^(?:show|get|describe)(?: the)? (?:scene|hierarchy)$", "^what(?:'s| is) in the scene\\??$"], "get_scene_hierarchy"),
                
                // Node selection by name
                new(
                    ["^select (.+)$"],
                    "select_scene_node",
                    m => new JsonObject { ["nodeName"] = m.Groups[1].Value.Trim() }
                ),

                // Play mode
                new(["^play$", "^start(?: game)?$", "^enter play mode$"], "enter_play_mode"),
                new(["^stop$", "^stop(?: game)?$", "^exit play mode$"], "exit_play_mode"),

                // Help
                new(["^help$", "^what can you do\\??$", "^show commands$"], "list_tools"),
            ];
        }

        private static string NormalizeShapeType(string input)
        {
            return input.ToLowerInvariant() switch
            {
                "box" => "Cube",
                "cube" => "Cube",
                "sphere" => "Sphere",
                "plane" => "Plane",
                "cylinder" => "Cylinder",
                "cone" => "Cone",
                "capsule" => "Capsule",
                "torus" => "Torus",
                _ => input
            };
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Processing mode for voice command interpretation.
    /// </summary>
    public enum EVoiceMcpMode
    {
        /// <summary>
        /// Only use keyword/pattern matching. Fastest, no API costs.
        /// </summary>
        KeywordsOnly,

        /// <summary>
        /// Only use LLM for intent classification. Most flexible, requires API key.
        /// </summary>
        LlmOnly,

        /// <summary>
        /// Try keywords first, fall back to LLM for unrecognized commands.
        /// </summary>
        Hybrid
    }

    /// <summary>
    /// LLM provider for intent classification.
    /// </summary>
    public enum ELlmProvider
    {
        /// <summary>OpenAI GPT models.</summary>
        OpenAI,

        /// <summary>Anthropic Claude models.</summary>
        Anthropic
    }

    /// <summary>
    /// Represents a voice command pattern and its associated MCP tool.
    /// </summary>
    public class VoiceCommand
    {
        private readonly Regex[] _patterns;
        private readonly string _toolName;
        private readonly Func<Match, JsonObject?>? _argumentBuilder;

        /// <summary>
        /// Creates a new voice command.
        /// </summary>
        /// <param name="patterns">Regex patterns to match (case-insensitive)</param>
        /// <param name="toolName">The MCP tool to call when matched</param>
        /// <param name="argumentBuilder">Optional function to build arguments from the regex match</param>
        public VoiceCommand(string[] patterns, string toolName, Func<Match, JsonObject?>? argumentBuilder = null)
        {
            _patterns = patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();
            _toolName = toolName;
            _argumentBuilder = argumentBuilder;
        }

        /// <summary>
        /// Attempts to match the input text against this command's patterns.
        /// </summary>
        public bool TryMatch(string text, out string toolName, out JsonObject? arguments)
        {
            foreach (var pattern in _patterns)
            {
                var match = pattern.Match(text);
                if (match.Success)
                {
                    toolName = _toolName;
                    arguments = _argumentBuilder?.Invoke(match);
                    return true;
                }
            }

            toolName = string.Empty;
            arguments = null;
            return false;
        }
    }

    /// <summary>
    /// Response from an MCP tool call initiated by voice command.
    /// </summary>
    public class McpVoiceResponse
    {
        /// <summary>Whether the call succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>The tool that was called.</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>The raw JSON response from the server.</summary>
        public string? RawResponse { get; set; }

        /// <summary>The parsed result node.</summary>
        public JsonNode? Result { get; set; }

        /// <summary>Error message if the call failed.</summary>
        public string? Error { get; set; }

        /// <summary>Extracted text content from the response, if available.</summary>
        public string? ContentText { get; set; }
    }

    /// <summary>
    /// Represents an LLM-classified tool call.
    /// </summary>
    internal record LlmToolCall(string ToolName, JsonObject Arguments);

    #endregion
}
