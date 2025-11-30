namespace XREngine
{
    public readonly record struct ArchiveExtractionProgress(
        float Progress,
        EArchiveExtractionPhase Phase,
        string? Message = null,
        string? CurrentItem = null);
}
