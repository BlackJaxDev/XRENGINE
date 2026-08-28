using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private ImportedAnimationImportManifest? _sourceImportManifest;

    /// <summary>
    /// Path-independent source identity, normalized binding inventory, and
    /// per-domain execution status produced by the Unity <c>.anim</c> importer.
    /// </summary>
    [MemoryPackIgnore]
    public ImportedAnimationImportManifest? SourceImportManifest
    {
        get => _sourceImportManifest;
        set => SetField(ref _sourceImportManifest, value);
    }

    public bool TryValidateSourcePlaybackCapabilities(out string diagnostic)
        => TryValidateSourcePlaybackCapabilities(allowRuntimeAdapters: false, out diagnostic);

    public bool TryValidateSourcePlaybackCapabilities(bool allowRuntimeAdapters, out string diagnostic)
    {
        ImportedAnimationImportManifest? manifest = SourceImportManifest;
        if (manifest is null)
        {
            diagnostic = string.Empty;
            return true;
        }

        if (manifest.SchemaVersion != ImportedAnimationImportManifest.CurrentSchemaVersion)
        {
            diagnostic =
                $"Unity import manifest schema {manifest.SchemaVersion} is not supported; " +
                $"expected {ImportedAnimationImportManifest.CurrentSchemaVersion}.";
            return false;
        }

        if (manifest.CapabilityContractVersion != ImportedAnimationImportCapabilityContract.CurrentVersion)
        {
            diagnostic =
                $"Unity animation capability contract {manifest.CapabilityContractVersion} is not supported; " +
                $"expected {ImportedAnimationImportCapabilityContract.CurrentVersion}.";
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
            ImportedAnimationImportCapabilityContract.SourceFormat,
            StringComparison.Ordinal))
        {
            diagnostic =
                $"Unity animation source format '{manifest.SourceIdentity.SourceFormat}' is not supported; " +
                $"expected '{ImportedAnimationImportCapabilityContract.SourceFormat}'.";
            return false;
        }

        if (!ImportedAnimationImportCapabilityContract.SupportsSerializedVersion(
            manifest.SourceIdentity.SerializedVersion))
        {
            diagnostic =
                $"Unity AnimationClip serializedVersion {manifest.SourceIdentity.SerializedVersion} " +
                $"is outside capability contract {ImportedAnimationImportCapabilityContract.CurrentVersion}.";
            return false;
        }

        if (!string.Equals(
            manifest.CoordinateContract.ContractId,
            ImportedAnimationCoordinateContract.CurrentContractId,
            StringComparison.Ordinal))
        {
            diagnostic =
                $"Unity clip coordinate contract '{manifest.CoordinateContract.ContractId}' " +
                $"does not match runtime contract '{ImportedAnimationCoordinateContract.CurrentContractId}'.";
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
