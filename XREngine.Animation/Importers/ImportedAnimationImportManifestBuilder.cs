namespace XREngine.Animation.Importers;

/// <summary>
/// Import-time accumulator kept separate from the durable manifest so the
/// serialized representation remains compact and immutable during playback.
/// </summary>
internal sealed class ImportedAnimationImportManifestBuilder
{
    private readonly Dictionary<EImportedAnimationDataDomain, ImportedAnimationDomainCapability> _domains = [];
    private readonly Dictionary<EImportedAnimationDataDomain, List<string>> _diagnostics = [];
    private readonly List<ImportedAnimationSourceBinding> _bindings = [];
    private readonly List<ImportedAnimationPreservedPayload> _preservedPayloads = [];

    public ImportedAnimationSourceIdentity SourceIdentity { get; set; } = new();
    public ImportedAnimationCoordinateContract CoordinateContract { get; } = new();

    public void RecordBinding(
        EImportedAnimationDataDomain domain,
        EImportedAnimationCapabilityState state,
        string sourceField,
        string nodePath,
        string attribute,
        int? classId,
        string runtimeTarget,
        string diagnostic = "")
    {
        RecordDomainItem(domain, state, diagnostic);
        _bindings.Add(new ImportedAnimationSourceBinding
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
        EImportedAnimationDataDomain domain,
        EImportedAnimationCapabilityState state,
        string sourceLocation,
        string diagnostic,
        string serializedYaml)
    {
        RecordDomainItem(domain, state, diagnostic);
        PreservePayload(domain, sourceLocation, serializedYaml);
    }

    public void PreservePayload(
        EImportedAnimationDataDomain domain,
        string sourceLocation,
        string serializedYaml)
    {
        if (string.IsNullOrWhiteSpace(serializedYaml))
            return;

        _preservedPayloads.Add(new ImportedAnimationPreservedPayload
        {
            Domain = domain,
            SourceLocation = sourceLocation,
            SerializedYaml = serializedYaml,
        });
    }

    public void RecordNotice(EImportedAnimationDataDomain domain, string diagnostic)
    {
        if (!_diagnostics.TryGetValue(domain, out List<string>? diagnostics))
        {
            diagnostics = [];
            _diagnostics.Add(domain, diagnostics);
        }

        diagnostics.Add(diagnostic);
    }

    public ImportedAnimationImportManifest Build()
    {
        ImportedAnimationDomainCapability[] domains = [.. _domains.Values.OrderBy(static x => x.Domain)];
        for (int i = 0; i < domains.Length; i++)
        {
            ImportedAnimationDomainCapability domain = domains[i];
            domain.Diagnostics = _diagnostics.TryGetValue(domain.Domain, out List<string>? diagnostics)
                ? [.. diagnostics]
                : [];
        }

        return new ImportedAnimationImportManifest
        {
            SourceIdentity = SourceIdentity,
            CoordinateContract = CoordinateContract,
            Domains = domains,
            Bindings = [.. _bindings],
            PreservedPayloads = [.. _preservedPayloads],
        };
    }

    private void RecordDomainItem(
        EImportedAnimationDataDomain domain,
        EImportedAnimationCapabilityState state,
        string diagnostic)
    {
        if (!_domains.TryGetValue(domain, out ImportedAnimationDomainCapability? capability))
        {
            capability = new ImportedAnimationDomainCapability
            {
                Domain = domain,
                State = EImportedAnimationCapabilityState.SupportedAndApplied,
            };
            _domains.Add(domain, capability);
        }

        capability.SourceItemCount++;
        if (state == EImportedAnimationCapabilityState.SupportedAndApplied)
            capability.AppliedItemCount++;
        else
            capability.PreservedItemCount++;

        if (state > capability.State)
            capability.State = state;

        if (!string.IsNullOrWhiteSpace(diagnostic))
            RecordNotice(domain, diagnostic);
    }
}
