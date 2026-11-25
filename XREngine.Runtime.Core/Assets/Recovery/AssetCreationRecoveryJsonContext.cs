using System.Text.Json.Serialization;

namespace XREngine.Assets.Recovery;

/// <summary>Source-generated JSON metadata for the creation recovery manifest and journal.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = false)]
[JsonSerializable(typeof(AssetCreationRecoveryManifest))]
[JsonSerializable(typeof(AssetCreationRecoveryRecord))]
internal sealed partial class AssetCreationRecoveryJsonContext : JsonSerializerContext
{
}
