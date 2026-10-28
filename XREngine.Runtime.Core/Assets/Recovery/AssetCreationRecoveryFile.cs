namespace XREngine.Assets.Recovery;

/// <summary>One file to publish: its full public path and its exact bytes.</summary>
public readonly record struct AssetCreationRecoveryFile(string PublicPath, byte[] Bytes);
