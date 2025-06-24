# RVC Audio Converter

The RVC (Retrieval-based Voice Conversion) Audio Converter is a local AI-powered voice conversion system that integrates with the XRENGINE's MicrophoneComponent to provide real-time voice transformation using the [RVC-Project framework](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion).

## Features

- **Local Voice Conversion**: Convert your voice using local AI models without internet connectivity
- **High Quality**: Uses RVC's state-of-the-art voice conversion technology
- **Real-time Processing**: Optimized for real-time applications with queue management
- **Multiple F0 Methods**: Support for pm, harvest, crepe, and rmvpe pitch extraction
- **Configurable Settings**: Fine-tune voice conversion parameters
- **Queue Management**: Built-in audio processing queue with timeout handling
- **Network Integration**: Works seamlessly with XRENGINE's networking system
- **No API Costs**: Completely local processing with no usage fees

## Prerequisites

1. **Python Installation**: Python 3.8+ with pip
2. **RVC Installation**: Install RVC using `pip install rvc`
3. **RVC Model**: Download a trained RVC model (.pth file)
4. **Index File**: Optional index file for enhanced quality
5. **System Resources**: GPU recommended for optimal performance

## Installation

### 1. Install RVC

```bash
# Install RVC from PyPI
pip install rvc

# Or install from source
git clone https://github.com/RVC-Project/Retrieval-based-Voice-Conversion.git
cd Retrieval-based-Voice-Conversion
pip install -e .
```

### 2. Download Models

Get pre-trained RVC models from:
- [Hugging Face RVC Models](https://huggingface.co/lj1995/VoiceConversionWebUI)
- [RVC Community Models](https://huggingface.co/lj1995/VoiceConversionWebUI/tree/main)

## Quick Start

### 1. Basic Setup

```csharp
// Get your microphone component
var microphone = GetComponent<MicrophoneComponent>();

// Add RVC converter
var converter = microphone.AddRVCConverter(
    modelPath: "path/to/your/model.pth",
    indexPath: "path/to/your/index.index",  // Optional
    pythonPath: "python",                   // Python executable path
    rvcScriptPath: "rvc"                    // RVC script name
);

// Configure settings
converter.SpeakerId = 0;
converter.F0UpKey = 0;
converter.F0Method = RVCF0Methods.RMVPE;
converter.IndexRate = 0.75f;
converter.RmsMixRate = 0.25f;
```

### 2. Using the Example Component

```csharp
// Add RVCExample component to your GameObject
var example = gameObject.AddComponent<RVCExample>();

// Configure in inspector or via code
example.ModelPath = "path/to/your/model.pth";
example.IndexPath = "path/to/your/index.index";
example.PythonPath = "python";
example.EnableVoiceConversion = true;
```

## Configuration Options

### Basic Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `ModelPath` | string | required | Path to RVC model file (.pth) |
| `IndexPath` | string | "" | Path to index file for enhanced quality |
| `SpeakerId` | int | 0 | Speaker/Singer ID for multi-speaker models |
| `F0UpKey` | int | 0 | Pitch transposition in semitones |

### F0 (Pitch) Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `F0Method` | string | "rmvpe" | Pitch extraction algorithm |
| `F0File` | string | "" | Custom F0 curve file (optional) |

**Available F0 Methods:**
- **`pm`**: Fast but less accurate
- **`harvest`**: Good balance of speed and accuracy
- **`crepe`**: High accuracy, slower
- **`rmvpe`**: Best accuracy, recommended

### Quality Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `IndexRate` | float | 0.75 | Search feature ratio (0.0-1.0) |
| `FilterRadius` | int | 3 | Median filter radius for pitch |
| `ResampleSr` | int | 0 | Output sample rate (0 = no resampling) |
| `RmsMixRate` | float | 0.25 | Volume envelope scaling (0.0-1.0) |
| `Protect` | float | 0.33 | Voiceless consonant protection (0.0-1.0) |

## F0 Method Comparison

| Method | Speed | Accuracy | Use Case |
|--------|-------|----------|----------|
| `pm` | Fastest | Low | Real-time applications |
| `harvest` | Fast | Medium | General use |
| `crepe` | Slow | High | High-quality conversion |
| `rmvpe` | Medium | Highest | Best overall quality |

## Advanced Usage

### Runtime Model Switching

```csharp
// Change model during runtime
example.ChangeModel("path/to/new/model.pth", "path/to/new/index.index");

// Update converter settings
example.UpdateConverterSettings(
    speakerId: 1,
    f0UpKey: 12,  // Raise pitch by one octave
    f0Method: RVCF0Methods.RMVPE,
    indexRate: 0.8f,
    rmsMixRate: 0.3f,
    protect: 0.5f
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
var converter1 = microphone.AddRVCConverter(modelPath1, indexPath1);
var converter2 = microphone.AddRVCConverter(modelPath2, indexPath2);

// Remove specific converters
microphone.RemoveAllRVCConverters();
```

## Integration with Networking

The RVC converter works seamlessly with XRENGINE's networking system:

```csharp
// Enable network broadcasting with voice conversion
microphone.Capture = true;
microphone.Receive = true;
microphone.CompressOverNetwork = true;

// The converted audio will be automatically broadcast to other clients
```

## Performance Considerations

### Processing Time

RVC processing time depends on several factors:
- **Audio length**: Longer audio = more processing time
- **F0 method**: `rmvpe` > `crepe` > `harvest` > `pm`
- **Hardware**: GPU acceleration significantly improves speed
- **Model size**: Larger models may be slower but higher quality

### Memory Usage

- **Queue Management**: Audio chunks are queued and processed asynchronously
- **Buffer Reuse**: Converted audio replaces original buffers to minimize memory usage
- **Temporary Files**: Input/output WAV files are created in temp directory and cleaned up

### Optimization Tips

1. **Use appropriate F0 method** for your use case
2. **Adjust buffer sizes** to balance latency and quality
3. **Use GPU acceleration** when available
4. **Monitor queue size** to prevent memory issues
5. **Set appropriate timeouts** for your hardware

## Troubleshooting

### Common Issues

1. **RVC Not Found**
   ```
   Error: RVC inference failed
   Solution: Ensure RVC is installed: pip install rvc
   ```

2. **Model File Not Found**
   ```
   Error: Model file not found
   Solution: Check model path and ensure file exists
   ```

3. **Python Not Found**
   ```
   Error: Python executable not found
   Solution: Set correct PythonPath or add Python to PATH
   ```

4. **Processing Timeout**
   ```
   Error: RVC inference timed out
   Solution: Increase timeout or use faster F0 method
   ```

5. **Poor Quality**
   ```
   Issue: Low quality conversion
   Solution: Use rmvpe F0 method, adjust IndexRate, check model quality
   ```

### Debug Information

Enable debug output to monitor converter status:

```csharp
// Debug output shows:
// - Model validation status
// - RVC process execution
// - Queue processing status
// - Error messages
// - Processing time information
```

## Model Management

### Finding Models

1. **Hugging Face**: [RVC Models Repository](https://huggingface.co/lj1995/VoiceConversionWebUI)
2. **Community Models**: Various community-contributed models
3. **Training Your Own**: Use RVC training scripts

### Model Types

- **Single Speaker**: One voice per model
- **Multi Speaker**: Multiple voices in one model
- **YourTTS**: YourTTS-based models
- **CoquiTTS**: CoquiTTS-based models

### Model Validation

```csharp
// Check if model is valid
bool isValid = example.IsModelValid();
bool indexValid = example.IsIndexValid();

if (!isValid)
{
    Debug.Out("Model file is invalid or missing");
}
```

## API Reference

### RVCConverter Properties

| Property | Type | Description |
|----------|------|-------------|
| `ModelPath` | string | Path to RVC model file |
| `IndexPath` | string | Path to index file |
| `SpeakerId` | int | Speaker ID for multi-speaker models |
| `F0UpKey` | int | Pitch transposition in semitones |
| `F0Method` | string | Pitch extraction method |
| `F0File` | string | Custom F0 curve file |
| `IndexRate` | float | Search feature ratio (0.0-1.0) |
| `FilterRadius` | int | Median filter radius |
| `ResampleSr` | int | Output sample rate |
| `RmsMixRate` | float | Volume envelope scaling (0.0-1.0) |
| `Protect` | float | Voiceless consonant protection (0.0-1.0) |

### RVCConverter Methods

| Method | Description |
|--------|-------------|
| `ClearQueue()` | Clear processing queue |
| `GetQueueCount()` | Get number of items in queue |
| `WaitForCompletionAsync()` | Wait for all processing to complete |
| `IsModelValid()` | Check if model file exists |
| `IsIndexValid()` | Check if index file exists |
| `KillRVCProcess()` | Force kill RVC process |

### MicrophoneComponent Methods

| Method | Description |
|--------|-------------|
| `AddRVCConverter()` | Add converter to pipeline |
| `RemoveAllRVCConverters()` | Remove all converters |
| `GetRVCConverters()` | Get all active converters |
| `WaitForAllRVCConvertersAsync()` | Wait for all converters to complete |

## Examples

See `RVCExample.cs` for comprehensive usage examples including:
- Basic setup and configuration
- Runtime model switching
- Settings adjustment
- Queue management
- Error handling

## Comparison with ElevenLabs

| Feature | RVC | ElevenLabs |
|---------|-----|------------|
| **Internet Required** | No | Yes |
| **API Costs** | None | Per usage |
| **Latency** | Higher | Lower |
| **Quality** | High | Very High |
| **Setup Complexity** | Medium | Low |
| **Model Availability** | Community | Official |
| **Custom Training** | Yes | Limited |

## Support

For issues with the RVC converter:
1. Check the debug output for error messages
2. Verify RVC installation: `pip list | grep rvc`
3. Test with different F0 methods and settings
4. Check model file validity and format
5. Ensure Python is in PATH and accessible

For RVC framework issues:
- Visit [RVC-Project GitHub](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion)
- Check [RVC Documentation](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion#readme)
- Join RVC community discussions
- Review model training guides

## License

The RVC converter is provided under the same license as the XRENGINE project. The RVC framework itself is licensed under MIT License. 