namespace XREngine.Rendering.Commands;

/// <summary>
/// Ring-owned immutable images for renderer-neutral global scene resources.
/// Empty domains still seal a valid sequence so consumers never need synthetic
/// rows to distinguish an empty scene from an unavailable publication.
/// </summary>
public sealed class AdvancedGlobalSceneResourcePublicationSnapshot
{
    internal AdvancedGlobalSceneResourcePublicationSnapshot(
        AdvancedGlobalResourceDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Lights = database.Lights.CreatePublicationSnapshot(includeRecordImage: true);
        Shadows = database.Shadows.CreatePublicationSnapshot(includeRecordImage: true);
        Probes = database.Probes.CreatePublicationSnapshot(includeRecordImage: true);
        Environments = database.Environments.CreatePublicationSnapshot(includeRecordImage: true);
        Decals = database.Decals.CreatePublicationSnapshot(includeRecordImage: true);
        GiResources = database.GiResources.CreatePublicationSnapshot(includeRecordImage: true);
    }

    public ulong Sequence { get; private set; }

    public AdvancedGlobalResourceDatabaseGenerations Generations { get; private set; }

    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedLightRecord> Lights { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedShadowRecord> Shadows { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedProbeRecord> Probes { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedEnvironmentRecord> Environments { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedDecalRecord> Decals { get; }
    public AdvancedGpuRecordTablePublicationSnapshot<AdvancedGiResourceRecord> GiResources { get; }

    internal bool TryCaptureTableState(
        ulong sequence,
        in AdvancedGlobalResourceDatabaseGenerations generations)
    {
        if (sequence == 0u ||
            Lights.Sequence != sequence ||
            Shadows.Sequence != sequence ||
            Probes.Sequence != sequence ||
            Environments.Sequence != sequence ||
            Decals.Sequence != sequence ||
            GiResources.Sequence != sequence)
        {
            Sequence = 0u;
            Generations = default;
            return false;
        }

        Sequence = sequence;
        Generations = generations;
        return true;
    }
}
