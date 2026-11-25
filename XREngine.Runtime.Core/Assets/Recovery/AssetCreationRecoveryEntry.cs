namespace XREngine.Assets.Recovery;

/// <summary>
/// One file that a journaled creation publishes. <see cref="RelativePath"/> is relative to the
/// project root and uses forward slashes.
/// </summary>
public sealed record AssetCreationRecoveryEntry(string RelativePath, long Length, string Sha256);
