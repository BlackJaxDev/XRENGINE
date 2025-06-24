# Speech-to-Text Component

A comprehensive speech-to-text component for XRENGINE that supports multiple cloud-based STT providers including Google, OpenAI, Azure, Deepgram, AssemblyAI, and Rev.ai.

## Features

- **Multiple Provider Support**: Google Speech-to-Text, OpenAI Whisper, Azure Speech Services, Deepgram, AssemblyAI, and Rev.ai
- **Real-time Processing**: Automatic processing of microphone input with configurable buffer sizes
- **Voice Activity Detection**: Automatic detection of speech segments with silence threshold
- **Confidence Filtering**: Filter results based on confidence scores
- **Interim Results**: Support for real-time interim transcription results (where supported)
- **Error Handling**: Comprehensive error handling and reporting
- **Thread-safe**: Safe for use in multi-threaded environments

## Quick Start

### 1. Basic Setup

```csharp
// Add both MicrophoneComponent and SpeechToTextComponent to a scene node
var node = new SceneNode("STT Node");
node.AddComponent<MicrophoneComponent>();
node.AddComponent<SpeechToTextComponent>();
```

### 2. Configure and Use

```csharp
public class MySTTController : XRComponent
{
    private SpeechToTextComponent? _sttComponent;

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        
        _sttComponent = GetSiblingComponent<SpeechToTextComponent>(true);
        
        if (_sttComponent != null)
        {
            // Configure the STT component
            _sttComponent.SelectedProvider = ESTTProvider.Google;
            _sttComponent.ApiKey = "YOUR_API_KEY";
            _sttComponent.Language = "en-US";
            
            // Subscribe to events
            _sttComponent.TextReceived += OnTextReceived;
            _sttComponent.ErrorOccurred += OnErrorOccurred;
        }
    }

    private void OnTextReceived((SpeechToTextComponent component, string text, float confidence) data)
    {
        Debug.Out($"Transcribed: {data.text} (confidence: {data.confidence:F2})");
    }

    private void OnErrorOccurred((SpeechToTextComponent component, string error) data)
    {
        Debug.LogWarning($"STT Error: {data.error}");
    }
}
```

## Supported Providers

### 1. Google Speech-to-Text

**Best for**: High accuracy, multiple languages, real-time processing

```csharp
_sttComponent.SelectedProvider = ESTTProvider.Google;
_sttComponent.ApiKey = "YOUR_GOOGLE_API_KEY";
_sttComponent.Language = "en-US";
_sttComponent.EnableInterimResults = true; // Supports interim results
```

**API Key**: Get from [Google Cloud Console](https://console.cloud.google.com/)

### 2. OpenAI Whisper

**Best for**: High accuracy, multilingual support, good with accents

```csharp
_sttComponent.SelectedProvider = ESTTProvider.OpenAI;
_sttComponent.ApiKey = "YOUR_OPENAI_API_KEY";
_sttComponent.Language = "en";
_sttComponent.EnableInterimResults = false; // No interim results support
```

**API Key**: Get from [OpenAI Platform](https://platform.openai.com/)

### 3. Azure Speech Services

**Best for**: Enterprise integration, custom models, real-time processing

```csharp
_sttComponent.SelectedProvider = ESTTProvider.Azure;
_sttComponent.ApiKey = "YOUR_AZURE_API_KEY";
_sttComponent.Language = "en-US";
```

**API Key**: Get from [Azure Portal](https://portal.azure.com/)

### 4. Deepgram

**Best for**: Real-time processing, low latency, custom models

```csharp
_sttComponent.SelectedProvider = ESTTProvider.Deepgram;
_sttComponent.ApiKey = "YOUR_DEEPGRAM_API_KEY";
_sttComponent.Language = "en-US";
```

**API Key**: Get from [Deepgram Console](https://console.deepgram.com/)

### 5. AssemblyAI

**Best for**: High accuracy, speaker diarization, sentiment analysis

```csharp
_sttComponent.SelectedProvider = ESTTProvider.AssemblyAI;
_sttComponent.ApiKey = "YOUR_ASSEMBLYAI_API_KEY";
_sttComponent.Language = "en";
```

**API Key**: Get from [AssemblyAI Dashboard](https://app.assemblyai.com/)

### 6. Rev.ai

**Best for**: Professional transcription, high accuracy, multiple formats

```csharp
_sttComponent.SelectedProvider = ESTTProvider.RevAI;
_sttComponent.ApiKey = "YOUR_REVAI_API_KEY";
_sttComponent.Language = "en";
```

**API Key**: Get from [Rev.ai Dashboard](https://www.rev.ai/)

## Configuration Options

### Basic Configuration

```csharp
_sttComponent.SelectedProvider = ESTTProvider.Google;    // STT provider to use
_sttComponent.ApiKey = "YOUR_API_KEY";                   // API key for the provider
_sttComponent.Language = "en-US";                        // Language code
```

### Advanced Configuration

```csharp
_sttComponent.EnableInterimResults = true;               // Enable real-time interim results
_sttComponent.ConfidenceThreshold = 0.8f;                // Minimum confidence (0.0-1.0)
_sttComponent.MaxBufferSize = 10;                        // Audio buffers to accumulate
_sttComponent.AutoProcess = true;                        // Auto-process audio buffers
_sttComponent.EnableVAD = true;                          // Voice Activity Detection
```

## Events

### TextReceived
Fired when final transcription is received with confidence above threshold.

```csharp
_sttComponent.TextReceived += (data) => {
    var (component, text, confidence) = data;
    Debug.Out($"Final: {text} (confidence: {confidence:F2})");
};
```

### InterimTextReceived
Fired when interim transcription results are available (where supported).

```csharp
_sttComponent.InterimTextReceived += (data) => {
    var (component, text, isFinal) = data;
    Debug.Out($"Interim: {text} (final: {isFinal})");
};
```

### ErrorOccurred
Fired when an error occurs during transcription.

```csharp
_sttComponent.ErrorOccurred += (data) => {
    var (component, error) = data;
    Debug.LogWarning($"STT Error: {error}");
};
```

## Manual Processing

### Process Audio Manually

```csharp
// Transcribe audio data manually
byte[] audioData = GetAudioData();
string transcription = await _sttComponent.TranscribeAudioAsync(audioData);
```

### Control Buffer Processing

```csharp
// Force processing of buffered audio
_sttComponent.ProcessBufferedAudio();

// Clear the audio buffer
_sttComponent.ClearAudioBuffer();
```

## Voice Command Processing

The component includes a `VoiceCommandProcessor` example for handling voice commands:

```csharp
public class MyVoiceController : XRComponent
{
    private VoiceCommandProcessor? _commandProcessor;

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        
        _commandProcessor = GetSiblingComponent<VoiceCommandProcessor>(true);
        
        if (_commandProcessor != null)
        {
            // Add custom commands
            _commandProcessor.AddCommand("open menu", () => OpenMenu());
            _commandProcessor.AddCommand("close application", () => CloseApp());
        }
    }

    private void OpenMenu() => Debug.Out("Opening menu...");
    private void CloseApp() => Debug.Out("Closing application...");
}
```

## Audio Format Requirements

The component works with the following audio formats:

- **Sample Rates**: 8000 Hz, 16000 Hz, 22050 Hz, 44100 Hz, 48000 Hz
- **Bit Depths**: 8-bit, 16-bit, 32-bit
- **Channels**: Mono (recommended), Stereo
- **Format**: Raw PCM audio data

## Performance Considerations

### Buffer Size
- Smaller buffer sizes (3-5) provide faster response but may miss words
- Larger buffer sizes (8-15) provide better accuracy but slower response
- Recommended: 5-10 buffers for most use cases

### Confidence Threshold
- Higher thresholds (0.8-0.9) ensure accuracy but may miss some speech
- Lower thresholds (0.5-0.7) catch more speech but may include errors
- Recommended: 0.7-0.8 for most applications

### Voice Activity Detection
- Enable VAD for automatic speech segment detection
- Adjust silence threshold based on your environment
- Disable VAD for continuous processing

## Error Handling

Common errors and solutions:

### API Key Errors
```
Error: HTTP 401: Unauthorized
Solution: Check your API key and ensure it's valid
```

### Rate Limiting
```
Error: HTTP 429: Too Many Requests
Solution: Implement rate limiting or upgrade your API plan
```

### Audio Format Errors
```
Error: Invalid audio format
Solution: Ensure audio is in the correct format and sample rate
```

### Network Errors
```
Error: Network timeout
Solution: Check internet connection and API endpoint availability
```

## Best Practices

1. **API Key Security**: Store API keys securely, not in source code
2. **Error Handling**: Always handle errors gracefully
3. **Rate Limiting**: Implement rate limiting for production use
4. **Audio Quality**: Use good quality microphones for better accuracy
5. **Language Selection**: Choose the appropriate language for your use case
6. **Testing**: Test with different accents and background noise levels

## Troubleshooting

### No Audio Detected
- Check microphone permissions
- Verify MicrophoneComponent is properly configured
- Ensure microphone is not muted

### Poor Transcription Quality
- Use a better microphone
- Reduce background noise
- Adjust confidence threshold
- Try a different provider

### High Latency
- Reduce buffer size
- Use a provider with lower latency
- Optimize network connection
- Consider local processing for critical applications

## Examples

See `SpeechToTextExample.cs` for comprehensive examples of:
- Configuring different providers
- Handling events
- Processing voice commands
- Manual audio transcription
- Runtime provider switching

## License

This component is part of XRENGINE and follows the same license terms. 