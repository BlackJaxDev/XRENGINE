namespace XREngine.UnityScriptHost.Contracts;

/// <summary>Contains bounded partial conversion evidence. It does not certify runtime parity or grant publication.</summary>
[Serializable]
public sealed record UnityConversionCoverageReport
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxFeatureCount = 4096;
    public const int MaxTextLength = 2048;

    /// <summary>Gets the report schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    /// <summary>Gets the stage that produced the evidence.</summary>
    public UnityConversionAssessmentStage Stage { get; init; }
    /// <summary>Gets whether every reached feature was assessed. Current producers report partial coverage.</summary>
    public bool IsComplete { get; init; }
    /// <summary>Gets the feature observations. An empty list does not mean that conversion succeeded.</summary>
    public UnityConversionFeature[] Features { get; init; } = [];
    /// <summary>Gets how many feature records exceeded the report capacity.</summary>
    public int SuppressedFeatureCount { get; init; }
    /// <summary>Gets whether any retained text was shortened to the report limit.</summary>
    public bool TextTruncated { get; init; }
}
