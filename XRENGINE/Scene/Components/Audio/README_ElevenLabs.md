# ElevenLabs Audio Converter

The ElevenLabs Audio Converter is an AI-powered voice conversion system that integrates with the XRENGINE's MicrophoneComponent to provide real-time voice transformation using ElevenLabs' advanced Speech-to-Speech Stream API.

## Features

- **Real-time Voice Conversion**: Convert your voice to any of thousands of AI voices in real-time using the [Speech-to-Speech Stream API](https://elevenlabs.io/docs/api-reference/speech-to-speech/stream)
- **High Quality**: Uses ElevenLabs' state-of-the-art voice cloning models
- **Low Latency**: Optimized for real-time applications with PCM audio format
- **Multiple Models**: Support for monolingual and multilingual models
- **Configurable Settings**: Fine-tune voice stability, similarity, and style
- **Queue Management**: Built-in audio processing queue with retry logic
- **Network Integration**: Works seamlessly with XRENGINE's networking system

## Prerequisites

1. **ElevenLabs API Key**: Get your API key from [ElevenLabs Dashboard](https://elevenlabs.io/)
2. **Voice ID**: Choose from the [ElevenLabs Voice Library](https://elevenlabs.io/voice-library)
3. **Internet Connection**: Required for API calls to ElevenLabs
4. **Speech-to-Speech Model**: Uses models that support voice conversion (check `can_do_voice_conversion` property)

## Quick Start

### 1. Basic Setup

```csharp
// Get your microphone component
var microphone = GetComponent<MicrophoneComponent>();

// Add ElevenLabs converter using Speech-to-Speech Stream API
var converter = microphone.AddElevenLabsConverter(
    apiKey: "YOUR_API_KEY",
    voiceId: "21m00Tcm4TlvDq8ikWAM", // Rachel voice
    modelId: "eleven_english_sts_v2"  // Speech-to-Speech model
);

// Configure settings
converter.Stability = 0.5f;
converter.SimilarityBoost = 0.75f;
converter.UseSpeakerBoost = true;
```

### 2. Using the Example Component

```csharp
// Add ElevenLabsExample component to your GameObject
var example = gameObject.AddComponent<ElevenLabsExample>();

// Configure in inspector or via code
example.ApiKey = "YOUR_API_KEY";
example.VoiceId = ElevenLabsVoices.English.Rachel;
example.ModelId = ElevenLabsVoices.Models.EnglishSTS; // Speech-to-Speech model
example.EnableVoiceConversion = true;
```

## API Endpoint

The converter uses the **Speech-to-Speech Stream API** endpoint:
```
POST https://api.elevenlabs.io/v1/speech-to-speech/:voice_id/stream
```

This endpoint is specifically designed for real-time voice conversion and provides:
- **Lower latency** than traditional voice conversion APIs
- **Streaming audio output** for real-time applications
- **PCM audio input** support for optimal performance
- **Voice settings** override for fine-tuning

## Configuration Options

### Voice Settings

| Setting | Range | Description |
|---------|-------|-------------|
| `Stability` | 0.0 - 1.0 | Controls voice consistency. Higher values = more stable but less expressive |
| `SimilarityBoost` | 0.0 - 1.0 | Controls how similar the output is to the target voice |
| `Style` | 0.0 - 1.0 | Controls style strength and expressiveness |
| `UseSpeakerBoost` | bool | Enhances speaker clarity and reduces background noise |

### Performance Settings

| Setting | Range | Description |
|---------|-------|-------------|
| `LatencyOptimization` | 0 - 4 | Optimizes for lower latency (higher values = lower latency, lower quality) |
| `MaxRetries` | 1+ | Maximum number of API retry attempts |
| `RetryDelayMs` | 100+ | Delay between retry attempts in milliseconds |

### Model Selection

- **`eleven_english_sts_v2`**: English Speech-to-Speech model (default)
- **`eleven_multilingual_sts_v2`**: Multilingual Speech-to-Speech model
- **Other STS models**: Check available models with `can_do_voice_conversion=true`

### Audio Format

The API supports optimal audio formats for low latency:
- **Input**: 16-bit PCM at 16kHz, single channel (mono), little-endian
- **Output**: MP3 at 44.1kHz, 128kbps (configurable)

## Popular Voice IDs

### English Voices
```csharp
// Female voices
ElevenLabsVoices.English.Rachel    // "21m00Tcm4TlvDq8ikWAM"
ElevenLabsVoices.English.Dorothy   // "ThT5KcBeYPX3keUQqHPh"
ElevenLabsVoices.English.Bella     // "EXAVITQu4vr4xnSDxMaL"

// Male voices
ElevenLabsVoices.English.Adam      // "pNInz6obpgDQGcFmaJgB"
ElevenLabsVoices.English.Sam       // "yoZ06aMxZJJ28mfd3POQ"
ElevenLabsVoices.English.Antoni    // "ErXwobaYiN019PkySvjV"
ElevenLabsVoices.English.Arnold    // "VR6AewLTigWG4xSOukaG"
```

### Multilingual Voices
```csharp
ElevenLabsVoices.Multilingual.Nicole  // "piTKgcLEGmPE4e6mEKli"
ElevenLabsVoices.Multilingual.Emily   // "LcfcDJNUP1GQjkzn1xUU"
ElevenLabsVoices.Multilingual.Fin     // "D38z5RcWu1voky8WS1ja"
```

## Advanced Usage

### Runtime Voice Switching

```csharp
// Change voice during runtime
example.ChangeVoice(ElevenLabsVoices.English.Adam);

// Update converter settings
example.UpdateConverterSettings(
    stability: 0.7f,
    similarityBoost: 0.8f,
    style: 0.3f
);
```

### Queue Management

```csharp
// Check processing queue status
int queueCount = example.GetProcessingQueueCount();
Debug.Out($"Audio chunks in queue: {queueCount}");

// Clear processing queue
example.ClearProcessingQueue();

// Wait for all processing to complete
await example.WaitForProcessingComplete();
```

### Multiple Converters

```csharp
// Add multiple converters for different voices
var converter1 = microphone.AddElevenLabsConverter(apiKey, voiceId1);
var converter2 = microphone.AddElevenLabsConverter(apiKey, voiceId2);

// Remove specific converters
microphone.RemoveAllElevenLabsConverters();
```

## Integration with Networking

The ElevenLabs converter works seamlessly with XRENGINE's networking system:

```csharp
// Enable network broadcasting with voice conversion
microphone.Capture = true;
microphone.Receive = true;
microphone.CompressOverNetwork = true;

// The converted audio will be automatically broadcast to other clients
```

## Error Handling

The converter includes built-in error handling and retry logic:

```csharp
// Check for errors in the debug output
// Common errors:
// - "HTTP 401: Unauthorized" - Invalid API key
// - "HTTP 429: Too Many Requests" - Rate limit exceeded
// - "HTTP 422: Unprocessable Entity" - Audio format or parameter error
// - "HTTP 413: Payload Too Large" - Audio chunk too large
```

## Performance Considerations

### Latency Optimization

The Speech-to-Speech Stream API provides several latency optimization levels:

- **Level 0**: Default mode (no latency optimizations)
- **Level 1**: Normal latency optimizations (~50% improvement)
- **Level 2**: Strong latency optimizations (~75% improvement)
- **Level 3**: Max latency optimizations
- **Level 4**: Max latency optimizations with text normalizer disabled

```csharp
// For real-time applications, use higher optimization levels
converter.LatencyOptimization = 3; // Max optimization
```

### Audio Format Optimization

For best performance:
- Use **PCM format** (`file_format=pcm_s16le_16`) for input
- Target **16kHz sample rate** for optimal latency
- Use **16-bit depth** for compatibility

### API Usage
- **Rate Limits**: ElevenLabs has rate limits based on your subscription
- **Chunk Size**: Smaller audio chunks reduce latency but increase API calls
- **Retry Logic**: Built-in retry logic handles temporary API failures

### Memory Usage
- **Queue Management**: Audio chunks are queued and processed asynchronously
- **Buffer Reuse**: Converted audio replaces original buffers to minimize memory usage
- **Cleanup**: Use `RemoveAllElevenLabsConverters()` to clean up resources

## Troubleshooting

### Common Issues

1. **No Audio Conversion**
   - Check API key is valid
   - Verify internet connection
   - Ensure microphone is capturing audio
   - Check if voice supports Speech-to-Speech conversion

2. **High Latency**
   - Reduce buffer size
   - Increase latency optimization level
   - Use PCM audio format
   - Check network connection

3. **Poor Quality**
   - Use higher quality model
   - Adjust stability and similarity settings
   - Ensure good microphone quality
   - Check audio format compatibility

4. **API Errors**
   - Check API key validity
   - Verify rate limits
   - Check audio format compatibility
   - Ensure voice supports STS conversion

### Debug Information

Enable debug output to monitor converter status:

```csharp
// Debug output shows:
// - Converter initialization status
// - API call results
// - Queue processing status
// - Error messages
// - Audio format conversion details
```

## API Reference

### ElevenLabsConverter Properties

| Property | Type | Description |
|----------|------|-------------|
| `VoiceId` | string | Target voice ID |
| `ModelId` | string | AI model to use (must support STS) |
| `Stability` | float | Voice stability (0.0-1.0) |
| `SimilarityBoost` | float | Voice similarity (0.0-1.0) |
| `Style` | float | Style strength (0.0-1.0) |
| `UseSpeakerBoost` | bool | Enable speaker enhancement |
| `LatencyOptimization` | int | Latency optimization level (0-4) |
| `MaxRetries` | int | Maximum retry attempts |
| `RetryDelayMs` | int | Retry delay in milliseconds |

### ElevenLabsConverter Methods

| Method | Description |
|--------|-------------|
| `ClearQueue()` | Clear processing queue |
| `GetQueueCount()` | Get number of items in queue |
| `WaitForCompletionAsync()` | Wait for all processing to complete |

### MicrophoneComponent Methods

| Method | Description |
|--------|-------------|
| `AddElevenLabsConverter()` | Add converter to pipeline |
| `RemoveAllElevenLabsConverters()` | Remove all converters |
| `GetElevenLabsConverters()` | Get all active converters |
| `WaitForAllElevenLabsConvertersAsync()` | Wait for all converters to complete |

## Examples

See `ElevenLabsExample.cs` for comprehensive usage examples including:
- Basic setup and configuration
- Runtime voice switching
- Settings adjustment
- Queue management
- Error handling

## Support

For issues with the ElevenLabs converter:
1. Check the debug output for error messages
2. Verify your ElevenLabs API key and subscription
3. Test with different voice IDs and settings
4. Check network connectivity and API status
5. Ensure the voice supports Speech-to-Speech conversion

For ElevenLabs API issues:
- Visit [ElevenLabs Documentation](https://docs.elevenlabs.io/)
- Check [ElevenLabs Status Page](https://status.elevenlabs.io/)
- Contact ElevenLabs support through their dashboard
- Review [Speech-to-Speech Stream API docs](https://elevenlabs.io/docs/api-reference/speech-to-speech/stream) 