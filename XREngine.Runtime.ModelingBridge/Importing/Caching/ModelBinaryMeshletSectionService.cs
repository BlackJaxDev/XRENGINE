using XREngine.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Loads the optional meshlet section and publishes it before mesh visibility.
/// The secondary callback receives cached core geometry only; it must never
/// invoke a source parser. The complete prefab/model cache remains out of scope.
/// </summary>
internal static class ModelBinaryMeshletSectionService
{
    public static ModelBinaryMeshletSectionPublishResult LoadAndPublish(
        IEnumerable<ModelBinaryMeshletSectionEntry> primary,
        IEnumerable<ModelBinaryMeshletSectionEntry>? secondary,
        Func<ModelBinaryMeshletSectionKey, XRMesh?> resolveMesh,
        Func<ModelBinaryMeshletSectionKey, XRMesh, MeshletPayload?>? repairFromCachedCore,
        Action<IReadOnlyList<ModelBinaryMeshletSectionEntry>>? republish,
        bool readOnly,
        IEnumerable<ModelBinaryMeshletSectionKey>? expectedKeys = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(resolveMesh);
        SortedSet<ModelBinaryMeshletSectionKey> unresolved = [];
        SortedDictionary<ModelBinaryMeshletSectionKey, (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry)> canonical = [];
        HashSet<ModelBinaryMeshletSectionKey> primarySeen = [];
        HashSet<ModelBinaryMeshletSectionKey> primaryKeys = [];
        HashSet<ModelBinaryMeshletSectionKey> secondarySeen = [];
        HashSet<ModelBinaryMeshletSectionKey> secondaryKeys = [];
        HashSet<ModelBinaryMeshletSectionKey> repairedKeys = [];

        // Do not touch a live mesh while reading the primary section. A later
        // malformed entry must be repairable without leaving a partial section
        // attached to the model's meshes.
        foreach (ModelBinaryMeshletSectionEntry entry in primary)
        {
            if (!primarySeen.Add(entry.Key))
            {
                canonical.Remove(entry.Key);
                primaryKeys.Remove(entry.Key);
                unresolved.Add(entry.Key);
                continue;
            }

            if (!TryStage(entry, resolveMesh, out (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged))
            {
                unresolved.Add(entry.Key);
                continue;
            }

            canonical.Add(entry.Key, staged);
            primaryKeys.Add(entry.Key);
            unresolved.Remove(entry.Key);
        }

        if (secondary is not null)
        {
            foreach (ModelBinaryMeshletSectionEntry entry in secondary)
            {
                // A valid primary is authoritative. Secondary data is only a
                // bounded cache-only repair source for entries the primary did
                // not provide successfully.
                if (canonical.ContainsKey(entry.Key))
                    continue;

                if (!secondarySeen.Add(entry.Key))
                {
                    canonical.Remove(entry.Key);
                    secondaryKeys.Remove(entry.Key);
                    unresolved.Add(entry.Key);
                    continue;
                }

                if (!TryStage(entry, resolveMesh, out (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged))
                {
                    unresolved.Add(entry.Key);
                    continue;
                }

                canonical.Add(entry.Key, staged);
                secondaryKeys.Add(entry.Key);
                unresolved.Remove(entry.Key);
            }
        }

        if (expectedKeys is not null)
            foreach (ModelBinaryMeshletSectionKey key in expectedKeys)
                if (!canonical.ContainsKey(key))
                    unresolved.Add(key);

        if (repairFromCachedCore is not null)
        {
            foreach (ModelBinaryMeshletSectionKey key in unresolved.ToArray())
            {
                XRMesh? mesh = resolveMesh(key);
                MeshletPayload? payload = mesh is null ? null : repairFromCachedCore(key, mesh);
                if (payload is null || !TryStage(new(key, payload), resolveMesh, out (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged))
                    continue;

                canonical[key] = staged;
                primaryKeys.Remove(key);
                secondaryKeys.Remove(key);
                repairedKeys.Add(key);
                unresolved.Remove(key);
            }
        }

        // Revalidate the complete selected closure immediately before the one
        // live-mesh mutation phase. This keeps attachment transactional if a
        // callback or payload state changed while repair was running.
        foreach ((ModelBinaryMeshletSectionKey key, (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged) in canonical.ToArray())
        {
            if (TryStage(staged.Entry, resolveMesh, out (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) validated))
            {
                canonical[key] = validated;
                continue;
            }

            canonical.Remove(key);
            primaryKeys.Remove(key);
            secondaryKeys.Remove(key);
            repairedKeys.Remove(key);
            unresolved.Add(key);
        }

        bool complete = unresolved.Count == 0;
        int primaryHydrated = 0;
        int secondaryHydrated = 0;
        int repaired = 0;
        if (complete)
        {
            foreach ((ModelBinaryMeshletSectionKey key, (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged) in canonical)
            {
                staged.Mesh.AttachValidatedCookedMeshletPayload(staged.Entry.Payload);
                if (primaryKeys.Contains(key)) primaryHydrated++;
                else if (secondaryKeys.Contains(key)) secondaryHydrated++;
                else if (repairedKeys.Contains(key)) repaired++;
            }
        }

        IReadOnlyList<ModelBinaryMeshletSectionEntry> republishEntries = canonical.Values
            .Select(static staged => staged.Entry)
            .ToArray();
        string? warning = complete
            ? null
            : "Meshlet section could not be resolved completely; no payloads were attached or republished.";
        bool retainedReadOnly = complete && readOnly && repaired != 0;
        if (complete && repaired != 0 && republish is not null && !readOnly)
            republish(republishEntries);
        else if (retainedReadOnly)
            warning = "Meshlet repair succeeded but cache is read-only; repaired payloads were retained in memory only.";

        ModelBinaryMeshletSectionKey[] unmatched = [.. unresolved];
        ModelBinaryMeshletSectionTelemetry.Record(primaryHydrated, secondaryHydrated, repaired, unmatched.Length);
        return new(primaryHydrated, secondaryHydrated, repaired, unmatched, retainedReadOnly, warning);
    }

    private static bool IsTerminal(MeshletPayloadState state)
        => state is MeshletPayloadState.Present or MeshletPayloadState.Disabled or MeshletPayloadState.Empty;

    /// <summary>
    /// Resolves and fully validates one candidate without assigning it to the
    /// mesh. Invalid cache semantics are repairable input, while callback
    /// failures remain visible to the caller.
    /// </summary>
    private static bool TryStage(
        ModelBinaryMeshletSectionEntry entry,
        Func<ModelBinaryMeshletSectionKey, XRMesh?> resolveMesh,
        out (XRMesh Mesh, ModelBinaryMeshletSectionEntry Entry) staged)
    {
        staged = default;
        if (entry is null || entry.Payload is null || !IsTerminal(entry.Payload.State))
            return false;

        XRMesh? mesh = resolveMesh(entry.Key);
        if (mesh is null)
            return false;

        try
        {
            entry.Payload.ValidateForMesh(mesh, entry.Payload.SourceMeshIdentity);
            staged = new(mesh, entry);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
