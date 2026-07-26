using XREngine.Rendering;

namespace XREngine.Editor.MaterialAuthoring;

/// <summary>
/// Persistent-identity material link registry. Runtime references are resolved
/// on demand, so deleted, moved, or reimported assets do not leave live stale
/// references. Re-entrant group propagation is rejected.
/// </summary>
public sealed class MaterialLinkRegistry
{
    private readonly Dictionary<Guid, MaterialAuthoringPersistentLinkGroup> _groups = [];
    private readonly HashSet<Guid> _propagating = [];

    public IReadOnlyCollection<MaterialAuthoringPersistentLinkGroup> Groups => _groups.Values;

    public string? AddOrReplace(MaterialAuthoringPersistentLinkGroup group)
    {
        if (group.Version != MaterialAuthoringPersistentLinkGroup.CurrentVersion)
            return $"Link version {group.Version} is unsupported.";
        if (string.IsNullOrWhiteSpace(group.SemanticPropertyId))
            return "A semantic property ID is required.";
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);
        foreach (MaterialAuthoringLinkMember member in group.Members)
            if (!identities.Add(member.AssetIdentity))
                return $"Duplicate link member '{member.AssetIdentity}'.";
        _groups[group.Id] = group;
        return null;
    }

    public bool RemoveMember(Guid groupId, string assetIdentity)
    {
        if (!_groups.TryGetValue(groupId, out MaterialAuthoringPersistentLinkGroup? group))
            return false;
        MaterialAuthoringLinkMember[] members = group.Members
            .Where(member => !member.AssetIdentity.Equals(assetIdentity, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _groups[groupId] = group with { Members = members };
        return members.Length != group.Members.Count;
    }

    public bool Remove(Guid groupId) => _groups.Remove(groupId);

    public bool TryPropagate(
        Guid groupId,
        string sourceIdentity,
        Func<MaterialAuthoringLinkMember, XRMaterial?> resolve,
        Func<XRMaterial, XRMaterial, string, string?> validate,
        Action<XRMaterial, XRMaterial, string> copy,
        out MaterialAuthoringTransactionReport report)
    {
        if (!_groups.TryGetValue(groupId, out MaterialAuthoringPersistentLinkGroup? group))
        {
            report = new(false, 0, ["The material link group no longer exists."]);
            return false;
        }
        if (!_propagating.Add(groupId))
        {
            report = new(false, 0, ["A material-link feedback cycle was prevented."]);
            return false;
        }
        try
        {
            MaterialAuthoringLinkMember? sourceMember = group.Members.FirstOrDefault(member =>
                member.AssetIdentity.Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase));
            XRMaterial? source = sourceMember is null ? null : resolve(sourceMember);
            if (source is null)
            {
                report = new(false, 0, ["The source material is deleted, moved, or unavailable."]);
                return false;
            }

            MaterialAuthoringTransaction transaction = new($"Propagate {group.SemanticPropertyId}");
            foreach (MaterialAuthoringLinkMember member in group.Members)
            {
                if (member.AssetIdentity.Equals(sourceIdentity, StringComparison.OrdinalIgnoreCase))
                    continue;
                XRMaterial? target = resolve(member);
                if (target is null)
                    continue;
                XRMaterial captured = target;
                transaction.Add(
                    captured,
                    group.SemanticPropertyId,
                    () => validate(source, captured, group.SemanticPropertyId),
                    () => copy(source, captured, group.SemanticPropertyId),
                    true);
            }
            return transaction.TryExecute(out report);
        }
        finally
        {
            _propagating.Remove(groupId);
        }
    }
}
