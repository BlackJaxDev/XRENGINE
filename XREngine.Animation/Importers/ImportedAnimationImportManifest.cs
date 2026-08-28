using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Versioned, loss-aware result of converting a Unity <c>.anim</c> file into
/// XRE's native animation representation.
/// </summary>
[MemoryPackable]
public sealed partial class ImportedAnimationImportManifest
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int CapabilityContractVersion { get; set; } = ImportedAnimationImportCapabilityContract.CurrentVersion;
    public ImportedAnimationSourceIdentity SourceIdentity { get; set; } = new();
    public ImportedAnimationCoordinateContract CoordinateContract { get; set; } = new();
    public ImportedAnimationDomainCapability[] Domains { get; set; } = [];
    public ImportedAnimationSourceBinding[] Bindings { get; set; } = [];
    public ImportedAnimationPreservedPayload[] PreservedPayloads { get; set; } = [];

    /// <summary>
    /// True when every retained source domain can execute through the single native path.
    /// Intentionally discarded source semantics do not block playback.
    /// </summary>
    public bool IsExecutable
    {
        get
        {
            for (int i = 0; i < Domains.Length; i++)
                if (Domains[i].State is not (
                    EImportedAnimationCapabilityState.SupportedAndApplied or
                    EImportedAnimationCapabilityState.IntentionallyDiscarded))
                    return false;
            return true;
        }
    }

    /// <summary>
    /// True when this clip contains semantic data that must be evaluated
    /// against a finalized target humanoid definition.
    /// </summary>
    public bool RequiresHumanoidAvatar
    {
        get
        {
            for (int i = 0; i < Domains.Length; i++)
            {
                ImportedAnimationDomainCapability domain = Domains[i];
                if (domain.SourceItemCount > 0
                    && domain.Domain is EImportedAnimationDataDomain.HumanoidMuscle
                        or EImportedAnimationDataDomain.HumanoidBody
                        or EImportedAnimationDataDomain.HumanoidIK)
                    return true;
            }
            return false;
        }
    }

    public bool TryGetBlockingDiagnostic(out string diagnostic)
        => TryGetBlockingDiagnostic(allowRuntimeAdapters: false, out diagnostic);

    public bool TryGetBlockingDiagnostic(bool allowRuntimeAdapters, out string diagnostic)
    {
        for (int i = 0; i < Domains.Length; i++)
        {
            ImportedAnimationDomainCapability domain = Domains[i];
            if (domain.State == EImportedAnimationCapabilityState.SupportedAndApplied)
                continue;
            if (domain.State == EImportedAnimationCapabilityState.IntentionallyDiscarded)
                continue;
            if (allowRuntimeAdapters && domain.State == EImportedAnimationCapabilityState.RequiresRuntimeAdapter)
                continue;

            string detail = domain.Diagnostics.Length > 0
                ? domain.Diagnostics[0]
                : "The source domain is not executable.";
            diagnostic = $"Unity animation domain '{domain.Domain}' is {domain.State}: {detail}";
            return true;
        }

        diagnostic = string.Empty;
        return false;
    }
}
