namespace XREngine;

public readonly record struct AssetLoadProgress(
    string RootAssetPath,
    string AssetPath,
    AssetLoadProgressStage Stage,
    string Status,
    float Progress,
    int CompletedDependencyLoads = 0,
    int TotalDependencyLoads = 0);
