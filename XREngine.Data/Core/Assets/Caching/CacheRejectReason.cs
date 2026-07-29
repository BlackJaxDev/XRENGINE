namespace XREngine.Core.Files.Caching
{
    /// <summary>
    /// Identifies the primary reason that a cache payload could not be used or published.
    /// Structured diagnostics may provide additional expected and actual values.
    /// </summary>
    public enum CacheRejectReason
    {
        None,
        FileMissing,
        LegacyFormat,
        Truncated,
        InvalidPreamble,
        SchemaVersionMismatch,
        PayloadVersionMismatch,
        HeaderChecksumMismatch,
        InvalidStringPool,
        ChunkTableChecksumMismatch,
        StringPoolChecksumMismatch,
        DependencyManifestChecksumMismatch,
        ReferencedOutputMissing,
        ReferencedOutputIncompatible,
        EntrySourceMissing,
        SourceLengthMismatch,
        SourceTimestampMismatch,
        SourceHashMismatch,
        DependencyMissing,
        DependencyLengthMismatch,
        DependencyTimestampMismatch,
        DependencyHashMismatch,
        RequestedBackendPolicyMismatch,
        BackendResolutionPolicyMismatch,
        ImporterBackendMismatch,
        ImporterBackendVersionMismatch,
        ImportOptionsHashMismatch,
        ModelCookSettingsHashMismatch,
        MaterialPolicyVersionMismatch,
        RequiredChunkMissing,
        UnknownRequiredChunk,
        RequiredComponentCodecMissing,
        ComponentCodecVersionMismatch,
        ChunkChecksumMismatch,
        ChunkVersionMismatch,
        UnsupportedChunkCodec,
        InvalidChunkTable,
        InvalidChunkRange,
        OverlappingChunkRange,
        ResourceLimitExceeded,
        AssetTypeMismatch,
        CodecUnavailable,
        SerializationFailed,
        Unreadable,
    }
}
