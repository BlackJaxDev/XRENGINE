using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Versioned, loss-aware result of converting a Unity <c>.anim</c> file into
/// XRE's native animation representation.
/// </summary>
[MemoryPackable]
public sealed partial class UnityAnimationImportManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public UnityAnimationSourceIdentity SourceIdentity { get; set; } = new();
    public UnityAnimationCoordinateContract CoordinateContract { get; set; } = new();
    public UnityAnimationDomainCapability[] Domains { get; set; } = [];
    public UnityAnimationSourceBinding[] Bindings { get; set; } = [];
    public UnityAnimationPreservedPayload[] PreservedPayloads { get; set; } = [];

    /// <summary>
    /// True only when every behaviorally relevant source domain present in the
    /// clip is executable through the single native path.
    /// </summary>
    public bool IsExecutable
    {
        get
        {
            for (int i = 0; i < Domains.Length; i++)
                if (Domains[i].State != EUnityAnimationCapabilityState.SupportedAndApplied)
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
                UnityAnimationDomainCapability domain = Domains[i];
                if (domain.SourceItemCount > 0
                    && domain.Domain is EUnityAnimationDataDomain.HumanoidMuscle
                        or EUnityAnimationDataDomain.HumanoidBody
                        or EUnityAnimationDataDomain.HumanoidIK)
                    return true;
            }
            return false;
        }
    }

    public bool TryGetBlockingDiagnostic(out string diagnostic)
    {
        for (int i = 0; i < Domains.Length; i++)
        {
            UnityAnimationDomainCapability domain = Domains[i];
            if (domain.State == EUnityAnimationCapabilityState.SupportedAndApplied)
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
