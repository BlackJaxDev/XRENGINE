namespace XREngine.Assets.Recovery;

/// <summary>One append-only journal line. <see cref="Note"/> records observed locations or a failure.</summary>
public sealed record AssetCreationRecoveryRecord(
    long Sequence,
    EAssetCreationRecoveryState State,
    DateTime Utc,
    string? Note = null);
