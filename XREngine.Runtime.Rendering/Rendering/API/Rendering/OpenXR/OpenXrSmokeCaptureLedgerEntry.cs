namespace XREngine.Rendering.API.Rendering.OpenXR;

public sealed class OpenXrSmokeCaptureLedgerEntry
{
    public string PipelineName { get; set; } = string.Empty;
    public string OutputRole { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int LayerIndex { get; set; }
    public int ExpectedLayerCount { get; set; }
    public uint ViewMask { get; set; }
    public string AntiAliasingMode { get; set; } = string.Empty;
    public string ViewKind { get; set; } = string.Empty;
    public ulong RenderFrameId { get; set; }
    public int ExternalImageSlot { get; set; } = -1;
    public int Width { get; set; }
    public int Height { get; set; }
    public int NonBlackPixelCount { get; set; }
    public double NonBlackPixelRatio { get; set; }
    public double MaximumLuminance { get; set; }
    public double LuminanceEnergy { get; set; }
    public float BloomCentroidX { get; set; }
    public float BloomCentroidY { get; set; }
    public float VelocityMeanX { get; set; }
    public float VelocityMeanY { get; set; }
    public float VelocityMeanMagnitude { get; set; }
    public float VelocityMaxMagnitude { get; set; }
    public int VelocityNonZeroSampleCount { get; set; }
    public float EdgeMeanGradient { get; set; }
    public float EdgeMaxGradient { get; set; }
    public int TopBandRows { get; set; }
    public int TopBandNonBlackPixelCount { get; set; }
    public double TopBandNonBlackPixelRatio { get; set; }
    public double TopBandMaximumLuminance { get; set; }
    public int TopBandMagentaPixelCount { get; set; }
    public int LuminanceFingerprintWidth { get; set; }
    public int LuminanceFingerprintHeight { get; set; }
    public double[] LuminanceFingerprint { get; set; } = [];
    public int VelocityMagnitudeFingerprintWidth { get; set; }
    public int VelocityMagnitudeFingerprintHeight { get; set; }
    public double[] VelocityMagnitudeFingerprint { get; set; } = [];
    public string TemporalScenario { get; set; } = string.Empty;
    public string TemporalSample { get; set; } = string.Empty;
    public string VelocityOracle { get; set; } = string.Empty;
    public int TemporalSequenceFrame { get; set; } = -1;
    public string Path { get; set; } = string.Empty;
    public long LengthBytes { get; set; }
    public DateTimeOffset LastWriteTimeUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string MetricsCapturePath { get; set; } = string.Empty;
    public DateTimeOffset MetricsCapturedAtUtc { get; set; }
}
