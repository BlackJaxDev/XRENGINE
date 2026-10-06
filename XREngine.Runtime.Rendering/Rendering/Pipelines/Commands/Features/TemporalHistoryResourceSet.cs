namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Stores the fixed image roles used by a temporal submission.</summary>
internal readonly record struct TemporalHistoryResourceSet(
    TemporalHistoryResourceIdentity Color,
    TemporalHistoryResourceIdentity Depth,
    TemporalHistoryResourceIdentity TsrColor,
    TemporalHistoryResourceIdentity Metadata,
    TemporalHistoryResourceIdentity Exposure = default);
