namespace XREngine.Rendering.Commands;

/// <summary>Scalar view identity copied from one published package.</summary>
public readonly record struct S13aIdentityManifestView(
    uint ViewId, ulong ViewGeneration, ulong HistoryKey,
    ulong SourceCameraIdentity, int ViewportWidth, int ViewportHeight,
    uint OutputLayer, EAdvancedViewRecordFlags Flags);
