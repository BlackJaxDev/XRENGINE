namespace XREngine.Rendering;

public enum TextureResidencyTransitionKind
{
    None = 0,
    Preview,
    Promotion,
    Demotion,
    Repair,
    PressureDemotion,
}

public readonly record struct TextureResidencyRange(
    uint ResidentMaxDimension,
    int BaseMipLevel,
    int LevelCount,
    SparseTextureStreamingPageSelection PageSelection,
    long EstimatedCommittedBytes);

public readonly record struct TextureResidencyTelemetry(
    string BackendName,
    string? TextureName,
    string? SourcePath,
    uint SourceWidth,
    uint SourceHeight,
    TextureResidencyRange Current,
    TextureResidencyRange Desired,
    TextureResidencyTransitionKind PendingTransitionKind,
    bool UsesSparseResidency,
    int StorageGeneration);

public readonly record struct TextureUploadTelemetry(
    string BackendName,
    string? TextureName,
    string? SourcePath,
    TextureUploadKind UploadKind,
    TextureUploadSourceKind SourceKind,
    TextureUploadPriorityClass PriorityClass,
    int MipStart,
    int MipCount,
    long EstimatedBytes,
    long UploadedBytes,
    double QueueWaitMilliseconds,
    double ExecutionMilliseconds,
    int StorageGeneration);

public readonly record struct TextureCookedSourceMetadata(
    string SourceVersion,
    TextureSourceMipLayout[] Mips,
    TextureSourcePageLayout[] Pages,
    long EstimatedResidentBytes);

public readonly record struct TextureSourceMipLayout(
    int MipLevel,
    long OffsetBytes,
    long LengthBytes,
    uint Width,
    uint Height);

public readonly record struct TextureSourcePageLayout(
    int MipLevel,
    int PageX,
    int PageY,
    long OffsetBytes,
    long LengthBytes);
