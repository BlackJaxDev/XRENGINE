namespace XREngine.Assets.Recovery;

/// <summary>The immutable description of one journaled creation.</summary>
public sealed record AssetCreationRecoveryManifest(
    int Schema,
    string OperationId,
    Guid AssetGuid,
    AssetCreationRecoveryEntry[] Entries);
