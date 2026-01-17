# Text-to-Speech (TTS) System

The XREngine TTS system provides text-to-speech capabilities through multiple cloud providers.

## Components

### TextToSpeechComponent

The main component for synthesizing speech from text.

```csharp
// Basic setup
var tts = sceneNode.AddComponent<TextToSpeechComponent>();
tts.SelectedProvider = ETTSProvider.OpenAI;
tts.ApiKey = "sk-your-api-key";
tts.Voice = "nova"; // Optional: specific voice
tts.AutoPlay = true;

// Speak text (synthesizes and plays)
await tts.SpeakAsync("Hello, world!");

// Or just synthesize without playing
var result = await tts.SynthesizeAsync("Hello");
if (result.Success)
{
    // Use result.AudioData (PCM bytes)
}
```

## Supported Providers

### OpenAI (`ETTSProvider.OpenAI`)
- **API Key**: OpenAI API key
- **Voices**: alloy, echo, fable, onyx, nova, shimmer
- **Models**: tts-1 (faster), tts-1-hd (higher quality)
- **Output**: 24kHz mono 16-bit PCM

### Google Cloud (`ETTSProvider.Google`)
- **API Key**: Google Cloud API key
- **Voices**: Language-specific (e.g., "en-US-Standard-A")
- **Features**: WaveNet and Standard voices, wide language support
- **Output**: 24kHz mono 16-bit PCM

### Azure (`ETTSProvider.Azure`)
- **API Key**: Azure Speech subscription key
- **Secondary Key**: Azure region (e.g., "eastus")
- **Voices**: Neural voices (e.g., "en-US-JennyNeural")
- **Features**: SSML support, many languages
- **Output**: 24kHz mono 16-bit PCM

### ElevenLabs (`ETTSProvider.ElevenLabs`)
- **API Key**: ElevenLabs API key
- **Voices**: Voice IDs from your ElevenLabs account
- **Features**: Premium expressive AI voices, voice cloning
- **Output**: 24kHz mono 16-bit PCM

### Amazon Polly (`ETTSProvider.Amazon`)
- **API Key**: AWS Access Key ID
- **Secondary Key**: AWS Secret Access Key
- **Voices**: Standard and Neural (e.g., "Joanna", "Matthew")
- **Output**: 24kHz mono 16-bit PCM

## Integration with Voice MCP Bridge

The TTS component integrates with `VoiceMcpBridgeComponent` for spoken responses:

```csharp
// Full voice chain with TTS
var bridge = VoiceMcpBridgeComponent.CreateVoiceMcpChain(
    mySceneNode,
    ESTTProvider.OpenAI,
    "sk-stt-key",
    wakeWord: "hey engine",
    llmApiKey: "sk-llm-key",
    ttsProvider: ETTSProvider.OpenAI,
    ttsApiKey: "sk-tts-key" // Optional, defaults to llmApiKey
);

// Or manual setup
var bridge = sceneNode.AddComponent<VoiceMcpBridgeComponent>();
var tts = sceneNode.AddComponent<TextToSpeechComponent>();
tts.ApiKey = "sk-your-key";
bridge.TextToSpeech = tts;
bridge.SpeakResponses = true;
```

## Events

```csharp
tts.SynthesisStarted += (component, text) => Debug.Out($"Starting: {text}");
tts.SynthesisCompleted += (component, result) => Debug.Out($"Done: {result.DurationSeconds}s");
tts.PlaybackStarted += (component) => Debug.Out("Playing...");
tts.PlaybackEnded += (component) => Debug.Out("Finished playing");
tts.ErrorOccurred += (component, error) => Debug.Out($"Error: {error}");
```

## Speech Queue

For sequential speech:

```csharp
tts.QueueSpeech("First message");
tts.QueueSpeech("Second message");
tts.QueueSpeech("Third message");
// Messages play in order

tts.Stop(); // Cancel current and clear queue
```

## Getting Available Voices

```csharp
var voices = await tts.GetAvailableVoicesAsync();
foreach (var voice in voices)
{
    Debug.Out($"{voice.Id}: {voice.Name} ({voice.LanguageCode}, {voice.Gender})");
}
```

## Properties

| Property | Type | Description |
|----------|------|-------------|
| `SelectedProvider` | `ETTSProvider` | The TTS provider to use |
| `ApiKey` | `string` | API key for the provider |
| `SecondaryApiKey` | `string` | Additional config (Azure region, AWS secret) |
| `Language` | `string` | Language code (e.g., "en-US") |
| `Voice` | `string` | Voice identifier (provider-specific) |
| `Model` | `string` | Model identifier (e.g., "tts-1" for OpenAI) |
| `Volume` | `float` | Output volume (0.0-1.0) |
| `AutoPlay` | `bool` | Auto-play synthesized audio |
| `IsSpeaking` | `bool` | Whether currently speaking (read-only) |
| `AudioSource` | `AudioSourceComponent` | Audio source for playback |

## Requirements

- An `AudioSourceComponent` on the same node or accessible for playback
- Valid API key for the selected provider
- Network access to the provider's API
