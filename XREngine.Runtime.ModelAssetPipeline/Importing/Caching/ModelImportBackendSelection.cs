namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Records the resolver snapshot and concrete backend that completed an import.
/// </summary>
public sealed class ModelImportBackendSelection
{
    public ModelImportBackendSelection(
        ModelImportBackendResolution resolution,
        ModelImportBackendDescriptor producer)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(producer);

        if (!resolution.Candidates.Any(candidate =>
            candidate.StableId.Equals(producer.StableId, StringComparison.Ordinal)
            && candidate.ImplementationVersion == producer.ImplementationVersion))
            throw new ArgumentException("The selected producer must belong to the resolver candidate snapshot.", nameof(producer));

        Resolution = resolution;
        Producer = producer;
    }

    public ModelImportBackendResolution Resolution { get; }
    public ModelImportBackendDescriptor Producer { get; }
    public ModelImportBackendPolicy RequestedPolicy => Resolution.RequestedPolicy;
    public string CandidateListHash => Resolution.CandidateListHash;
    public string ProducerId => Producer.StableId;
    public uint ProducerVersion => Producer.ImplementationVersion;
}
