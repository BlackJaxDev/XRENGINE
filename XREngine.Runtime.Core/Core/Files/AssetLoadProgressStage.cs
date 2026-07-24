namespace XREngine;

public enum AssetLoadProgressStage
{
    CheckingCache,
    OpeningFile,
    ParsingAssetGraph,
    ResolvingDependencies,
    ImportingThirdParty,
    Finalizing,
    Completed,
    Failed
}
