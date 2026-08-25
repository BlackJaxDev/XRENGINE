namespace XREngine.Animation.Importers;

/// <summary>
/// Import-time accumulator kept separate from the durable manifest so the
/// serialized representation remains compact and immutable during playback.
/// </summary>
internal sealed class UnityAnimationImportManifestBuilder
{
    private readonly Dictionary<EUnityAnimationDataDomain, UnityAnimationDomainCapability> _domains = [];
    private readonly Dictionary<EUnityAnimationDataDomain, List<string>> _diagnostics = [];
    private readonly List<UnityAnimationSourceBinding> _bindings = [];
    private readonly List<UnityAnimationPreservedPayload> _preservedPayloads = [];

    public UnityAnimationSourceIdentity SourceIdentity { get; set; } = new();
    public UnityAnimationCoordinateContract CoordinateContract { get; } = new();

    public void RecordBinding(
        EUnityAnimationDataDomain domain,
        EUnityAnimationCapabilityState state,
        string sourceField,
        string nodePath,
        string attribute,
        int? classId,
        string runtimeTarget,
        string diagnostic = "")
    {
        RecordDomainItem(domain, state, diagnostic);
        _bindings.Add(new UnityAnimationSourceBinding
        {
            Domain = domain,
            State = state,
            SourceField = sourceField,
            NodePath = nodePath,
            Attribute = attribute,
            ClassId = classId,
            RuntimeTarget = runtimeTarget,
            Diagnostic = diagnostic,
        });
    }

    public void RecordSection(
        EUnityAnimationDataDomain domain,
        EUnityAnimationCapabilityState state,
        string sourceLocation,
        string diagnostic,
        string serializedYaml)
    {
        RecordDomainItem(domain, state, diagnostic);
        PreservePayload(domain, sourceLocation, serializedYaml);
    }

    public void PreservePayload(
        EUnityAnimationDataDomain domain,
        string sourceLocation,
        string serializedYaml)
    {
        if (string.IsNullOrWhiteSpace(serializedYaml))
            return;

        _preservedPayloads.Add(new UnityAnimationPreservedPayload
        {
            Domain = domain,
            SourceLocation = sourceLocation,
            SerializedYaml = serializedYaml,
        });
    }

    public void RecordNotice(EUnityAnimationDataDomain domain, string diagnostic)
    {
        if (!_diagnostics.TryGetValue(domain, out List<string>? diagnostics))
        {
            diagnostics = [];
            _diagnostics.Add(domain, diagnostics);
        }

        diagnostics.Add(diagnostic);
    }

    public UnityAnimationImportManifest Build()
    {
        UnityAnimationDomainCapability[] domains = [.. _domains.Values.OrderBy(static x => x.Domain)];
        for (int i = 0; i < domains.Length; i++)
        {
            UnityAnimationDomainCapability domain = domains[i];
            domain.Diagnostics = _diagnostics.TryGetValue(domain.Domain, out List<string>? diagnostics)
                ? [.. diagnostics]
                : [];
        }

        return new UnityAnimationImportManifest
        {
            SourceIdentity = SourceIdentity,
            CoordinateContract = CoordinateContract,
            Domains = domains,
            Bindings = [.. _bindings],
            PreservedPayloads = [.. _preservedPayloads],
        };
    }

    private void RecordDomainItem(
        EUnityAnimationDataDomain domain,
        EUnityAnimationCapabilityState state,
        string diagnostic)
    {
        if (!_domains.TryGetValue(domain, out UnityAnimationDomainCapability? capability))
        {
            capability = new UnityAnimationDomainCapability
            {
                Domain = domain,
                State = EUnityAnimationCapabilityState.SupportedAndApplied,
            };
            _domains.Add(domain, capability);
        }

        capability.SourceItemCount++;
        if (state == EUnityAnimationCapabilityState.SupportedAndApplied)
            capability.AppliedItemCount++;
        else
            capability.PreservedItemCount++;

        if (state > capability.State)
            capability.State = state;

        if (!string.IsNullOrWhiteSpace(diagnostic))
            RecordNotice(domain, diagnostic);
    }
}
