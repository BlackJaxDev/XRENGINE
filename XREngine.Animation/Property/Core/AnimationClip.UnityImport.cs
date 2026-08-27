using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private UnityAnimationImportManifest? _unityImportManifest;

    /// <summary>
    /// Path-independent source identity, normalized binding inventory, and
    /// per-domain execution status produced by the Unity <c>.anim</c> importer.
    /// </summary>
    [MemoryPackIgnore]
    public UnityAnimationImportManifest? UnityImportManifest
    {
        get => _unityImportManifest;
        set => SetField(ref _unityImportManifest, value);
    }

    public bool TryValidateUnityPlaybackCapabilities(out string diagnostic)
        => TryValidateUnityPlaybackCapabilities(allowRuntimeAdapters: false, out diagnostic);

    public bool TryValidateUnityPlaybackCapabilities(bool allowRuntimeAdapters, out string diagnostic)
    {
        UnityAnimationImportManifest? manifest = UnityImportManifest;
        if (manifest is null)
        {
            diagnostic = string.Empty;
            return true;
        }

        if (manifest.SchemaVersion != UnityAnimationImportManifest.CurrentSchemaVersion)
        {
            diagnostic =
                $"Unity import manifest schema {manifest.SchemaVersion} is not supported; " +
                $"expected {UnityAnimationImportManifest.CurrentSchemaVersion}.";
            return false;
        }

        if (manifest.CapabilityContractVersion != UnityAnimationImportCapabilityContract.CurrentVersion)
        {
            diagnostic =
                $"Unity animation capability contract {manifest.CapabilityContractVersion} is not supported; " +
                $"expected {UnityAnimationImportCapabilityContract.CurrentVersion}.";
            return false;
        }

        if (manifest.SourceIdentity is null
            || manifest.CoordinateContract is null
            || manifest.Domains is null
            || manifest.Bindings is null
            || manifest.PreservedPayloads is null)
        {
            diagnostic = "The Unity import manifest is incomplete or malformed.";
            return false;
        }

        if (!string.Equals(
            manifest.SourceIdentity.SourceFormat,
            UnityAnimationImportCapabilityContract.SourceFormat,
            StringComparison.Ordinal))
        {
            diagnostic =
                $"Unity animation source format '{manifest.SourceIdentity.SourceFormat}' is not supported; " +
                $"expected '{UnityAnimationImportCapabilityContract.SourceFormat}'.";
            return false;
        }

        if (!UnityAnimationImportCapabilityContract.SupportsSerializedVersion(
            manifest.SourceIdentity.SerializedVersion))
        {
            diagnostic =
                $"Unity AnimationClip serializedVersion {manifest.SourceIdentity.SerializedVersion} " +
                $"is outside capability contract {UnityAnimationImportCapabilityContract.CurrentVersion}.";
            return false;
        }

        if (!string.Equals(
            manifest.CoordinateContract.ContractId,
            UnityAnimationCoordinateContract.CurrentContractId,
            StringComparison.Ordinal))
        {
            diagnostic =
                $"Unity clip coordinate contract '{manifest.CoordinateContract.ContractId}' " +
                $"does not match runtime contract '{UnityAnimationCoordinateContract.CurrentContractId}'.";
            return false;
        }

        if (manifest.SourceIdentity.SourceContentSha256.Length != 64
            || manifest.SourceIdentity.ImportSettingsSha256.Length != 64)
        {
            diagnostic = "Unity clip content or import-settings identity is missing or malformed.";
            return false;
        }

        if (!manifest.TryGetBlockingDiagnostic(allowRuntimeAdapters, out diagnostic))
        {
            diagnostic = string.Empty;
            return true;
        }

        return false;
    }
}
