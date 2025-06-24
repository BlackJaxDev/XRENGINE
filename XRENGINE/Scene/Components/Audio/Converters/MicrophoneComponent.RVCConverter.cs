using System.Diagnostics;

namespace XREngine.Components
{
    public partial class MicrophoneComponent
    {
        public class RVCConverter : AudioConverter
        {
            private string _modelPath;
            private string _indexPath;
            private int _speakerId;
            private int _f0UpKey;
            private string _f0Method;
            private string _f0File;
            private float _indexRate;
            private int _filterRadius;
            private int _resampleSr;
            private float _rmsMixRate;
            private float _protect;
            private readonly Queue<byte[]> _processingQueue = new();
            private readonly object _queueLock = new();
            private bool _isProcessing = false;
            private readonly SemaphoreSlim _semaphore = new(1, 1);
            private Process? _rvcProcess;
            private readonly string _rvcPythonPath;
            private readonly string _rvcScriptPath;
            private bool _isInitialized = false;

            public RVCConverter(string modelPath, string indexPath = "", string rvcPythonPath = "python", string rvcScriptPath = "rvc")
            {
                _modelPath = modelPath;
                _indexPath = indexPath;
                _speakerId = 0;
                _f0UpKey = 0;
                _f0Method = "rmvpe";
                _f0File = "";
                _indexRate = 0.75f;
                _filterRadius = 3;
                _resampleSr = 0;
                _rmsMixRate = 0.25f;
                _protect = 0.33f;
                _rvcPythonPath = rvcPythonPath;
                _rvcScriptPath = rvcScriptPath;

                // Validate model path
                if (!File.Exists(_modelPath))
                {
                    Debug.Out($"RVC Error: Model file not found at {_modelPath}");
                }
            }

            // Configuration properties
            public string ModelPath
            {
                get => _modelPath;
                set
                {
                    if (_modelPath != value)
                    {
                        _modelPath = value;
                        _isInitialized = false;
                        // Clear queue when model changes
                        lock (_queueLock)
                        {
                            _processingQueue.Clear();
                        }
                    }
                }
            }

            public string IndexPath
            {
                get => _indexPath;
                set
                {
                    if (_indexPath != value)
                    {
                        _indexPath = value;
                        _isInitialized = false;
                    }
                }
            }

            public int SpeakerId
            {
                get => _speakerId;
                set => _speakerId = value;
            }

            public int F0UpKey
            {
                get => _f0UpKey;
                set => _f0UpKey = value;
            }

            public string F0Method
            {
                get => _f0Method;
                set => _f0Method = value;
            }

            public string F0File
            {
                get => _f0File;
                set => _f0File = value;
            }

            public float IndexRate
            {
                get => _indexRate;
                set => _indexRate = Math.Clamp(value, 0.0f, 1.0f);
            }

            public int FilterRadius
            {
                get => _filterRadius;
                set => _filterRadius = Math.Max(0, value);
            }

            public int ResampleSr
            {
                get => _resampleSr;
                set => _resampleSr = Math.Max(0, value);
            }

            public float RmsMixRate
            {
                get => _rmsMixRate;
                set => _rmsMixRate = Math.Clamp(value, 0.0f, 1.0f);
            }

            public float Protect
            {
                get => _protect;
                set => _protect = Math.Clamp(value, 0.0f, 1.0f);
            }

            protected override async void ConvertBuffer(byte[] buffer, int bitsPerSample, int sampleRate)
            {
                if (string.IsNullOrEmpty(_modelPath) || buffer.Length == 0)
                    return;

                // Add to processing queue
                lock (_queueLock)
                {
                    _processingQueue.Enqueue([.. buffer]);
                }

                // Start processing if not already running
                if (!_isProcessing)
                    await Task.Run(ProcessQueueAsync);
            }

            private async Task ProcessQueueAsync()
            {
                await _semaphore.WaitAsync();
                _isProcessing = true;

                try
                {
                    while (true)
                    {
                        byte[]? audioData = null;

                        lock (_queueLock)
                        {
                            if (_processingQueue.Count > 0)
                            {
                                audioData = _processingQueue.Dequeue();
                            }
                        }

                        if (audioData == null)
                            break;

                        await ProcessAudioChunkAsync(audioData);
                    }
                }
                catch (Exception ex)
                {
                    Debug.Out($"RVC conversion error: {ex.Message}");
                }
                finally
                {
                    _isProcessing = false;
                    _semaphore.Release();
                }
            }

            private async Task ProcessAudioChunkAsync(byte[] audioData)
            {
                try
                {
                    // Create temporary files for input and output
                    string tempDir = Path.GetTempPath();
                    string inputFile = Path.Combine(tempDir, $"rvc_input_{Guid.NewGuid()}.wav");
                    string outputFile = Path.Combine(tempDir, $"rvc_output_{Guid.NewGuid()}.wav");

                    try
                    {
                        // Save input audio to WAV file
                        SaveAudioToWav(audioData, inputFile, 16, 44100);

                        // Run RVC inference
                        bool success = await RunRVCInference(inputFile, outputFile);

                        if (success && File.Exists(outputFile))
                        {
                            // Load converted audio and replace original buffer
                            byte[] convertedAudio = LoadAudioFromWav(outputFile);
                            if (convertedAudio.Length > 0)
                            {
                                // Resize and copy to original buffer
                                if (convertedAudio.Length <= audioData.Length)
                                {
                                    Buffer.BlockCopy(convertedAudio, 0, audioData, 0, convertedAudio.Length);
                                    
                                    // Fill remaining space with silence if needed
                                    if (convertedAudio.Length < audioData.Length)
                                    {
                                        for (int i = convertedAudio.Length; i < audioData.Length; i++)
                                        {
                                            audioData[i] = 0;
                                        }
                                    }
                                }
                                else
                                {
                                    // Truncate if converted audio is longer
                                    Buffer.BlockCopy(convertedAudio, 0, audioData, 0, audioData.Length);
                                }
                            }
                        }
                    }
                    finally
                    {
                        // Clean up temporary files
                        try
                        {
                            if (File.Exists(inputFile)) File.Delete(inputFile);
                            if (File.Exists(outputFile)) File.Delete(outputFile);
                        }
                        catch (Exception ex)
                        {
                            Debug.Out($"RVC cleanup error: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.Out($"RVC conversion failed: {ex.Message}");
                }
            }

            private async Task<bool> RunRVCInference(string inputFile, string outputFile)
            {
                try
                {
                    // Build RVC command line arguments
                    var arguments = new List<string>
                    {
                        "infer",
                        "-m", _modelPath,
                        "-i", inputFile,
                        "-o", outputFile,
                        "-s", _speakerId.ToString(),
                        "-fu", _f0UpKey.ToString(),
                        "-fm", _f0Method,
                        "-ir", _indexRate.ToString("F2"),
                        "-fr", _filterRadius.ToString(),
                        "-rsr", _resampleSr.ToString(),
                        "-rmr", _rmsMixRate.ToString("F2"),
                        "-p", _protect.ToString("F2")
                    };

                    // Add optional parameters
                    if (!string.IsNullOrEmpty(_indexPath))
                    {
                        arguments.AddRange(["-if", _indexPath]);
                    }

                    if (!string.IsNullOrEmpty(_f0File))
                    {
                        arguments.AddRange(["-ff", _f0File]);
                    }

                    // Create process start info
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _rvcPythonPath,
                        Arguments = $"-m {_rvcScriptPath} {string.Join(" ", arguments)}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // Run RVC process
                    using var process = new Process { StartInfo = startInfo };
                    process.Start();

                    // Wait for completion with timeout
                    bool completed = await Task.Run(() => process.WaitForExit(30000)); // 30 second timeout

                    if (!completed)
                    {
                        process.Kill();
                        Debug.Out("RVC inference timed out");
                        return false;
                    }

                    if (process.ExitCode != 0)
                    {
                        string error = await process.StandardError.ReadToEndAsync();
                        Debug.Out($"RVC inference failed with exit code {process.ExitCode}: {error}");
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.Out($"RVC inference error: {ex.Message}");
                    return false;
                }
            }

            private static void SaveAudioToWav(byte[] audioData, string filePath, int bitsPerSample, int sampleRate)
            {
                try
                {
                    using var memoryStream = new MemoryStream();
                    var waveFormat = new NAudio.Wave.WaveFormat(sampleRate, bitsPerSample, 1);
                    
                    using var waveWriter = new NAudio.Wave.WaveFileWriter(filePath, waveFormat);
                    waveWriter.Write(audioData, 0, audioData.Length);
                    waveWriter.Flush();
                }
                catch (Exception ex)
                {
                    Debug.Out($"Error saving audio to WAV: {ex.Message}");
                }
            }

            private static byte[] LoadAudioFromWav(string filePath)
            {
                try
                {
                    using var waveReader = new NAudio.Wave.WaveFileReader(filePath);
                    var audioData = new byte[waveReader.Length];
                    waveReader.ReadExactly(audioData);
                    return audioData;
                }
                catch (Exception ex)
                {
                    Debug.Out($"Error loading audio from WAV: {ex.Message}");
                    return [];
                }
            }

            public void ClearQueue()
            {
                lock (_queueLock)
                {
                    _processingQueue.Clear();
                }
            }

            public int GetQueueCount()
            {
                lock (_queueLock)
                {
                    return _processingQueue.Count;
                }
            }

            public async Task WaitForCompletionAsync()
            {
                while (true)
                {
                    int queueCount;
                    lock (_queueLock)
                    {
                        queueCount = _processingQueue.Count;
                    }

                    if (queueCount == 0 && !_isProcessing)
                        break;

                    await Task.Delay(10);
                }
            }

            public bool IsModelValid()
                => File.Exists(_modelPath);

            public bool IsIndexValid()
                => string.IsNullOrEmpty(_indexPath) || File.Exists(_indexPath);

            public void KillRVCProcess()
            {
                try
                {
                    _rvcProcess?.Kill();
                    _rvcProcess = null;
                }
                catch (Exception ex)
                {
                    Debug.Out($"Error killing RVC process: {ex.Message}");
                }
            }
        }
    }
} 