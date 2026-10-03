namespace XREngine.RenderBench;

/// <summary>One external capture attached after measured work, or explicitly missing.</summary>
public sealed record RenderBenchExternalCaptureArtifact(
    string SourceRelativePath,
    string? AttachedPath,
    string Status,
    long? ByteCount,
    string? Sha256);
