using System.Collections.ObjectModel;

namespace XREngine.Data.Core;

/// <summary>
/// Retains the exact objects whose base constructors ran in one owned construction.
/// This ledger does not authorize destruction after an object acquires another owner.
/// </summary>
public sealed class ObjectConstructionOwnership
{
    private readonly List<XRObjectBase> _objects = [];
    private readonly HashSet<XRObjectBase> _origins = new(ReferenceEqualityComparer.Instance);
    private readonly ReadOnlyCollection<XRObjectBase> _view;
    private readonly ObjectConstructionIdentityGuard _identityGuard = new();
    private ReadOnlyCollection<ObjectConstructionIdentity>? _identities;

    internal ObjectConstructionOwnership() => _view = _objects.AsReadOnly();

    /// <summary>Includes unpublished and non-cache objects, including failed constructions.</summary>
    public IReadOnlyList<XRObjectBase> Objects => _view;

    /// <summary>Contains final identities captured before construction admission was released.</summary>
    public IReadOnlyList<ObjectConstructionIdentity> Identities
        => _identities is { } identities ? identities : Array.Empty<ObjectConstructionIdentity>();
    public bool HasSealedIdentities => _identities is not null;

    /// <summary>
    /// Derives a ledger from sealed constructor receipts without reading new identities.
    /// This proves origin only. The caller must still prove current ownership before cleanup.
    /// </summary>
    public ObjectConstructionOwnership CreateSubset(IEnumerable<XRObjectBase> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        HashSet<XRObjectBase> selected = new(ReferenceEqualityComparer.Instance);
        foreach (XRObjectBase value in objects)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!selected.Add(value))
                throw new InvalidOperationException("A construction subset cannot repeat an allocation.");
        }
        lock (_origins)
        {
            if (_identities is null)
                throw new InvalidOperationException("A construction subset requires sealed identities.");
            if (!selected.IsSubsetOf(_origins))
                throw new InvalidOperationException("A construction subset cannot include borrowed objects.");
            ObjectConstructionOwnership subset = new();
            List<ObjectConstructionIdentity> identities = new(selected.Count);
            foreach (ObjectConstructionIdentity identity in _identities)
                if (selected.Contains(identity.Target))
                {
                    subset._objects.Add(identity.Target);
                    subset._origins.Add(identity.Target);
                    identities.Add(identity);
                }
            subset._identities = identities.AsReadOnly();
            return subset;
        }
    }

    internal void SealIdentities()
    {
        lock (_origins)
        {
            if (_identities is not null)
                throw new InvalidOperationException("Construction identities can be sealed only once.");
            List<ObjectConstructionIdentity> identities = new(_objects.Count);
            foreach (XRObjectBase value in _objects)
                identities.Add(new(value, value.ID, value.LifetimeRevision));
            _identities = identities.AsReadOnly();
        }
    }

    /// <summary>Tests constructor origin, not current lifetime or graph ownership.</summary>
    public bool Contains(XRObjectBase value)
    {
        lock (_origins)
            return _origins.Contains(value);
    }

    internal void Record(XRObjectBase value)
    {
        lock (_origins)
        {
            if (_origins.Add(value))
            {
                _objects.Add(value);
                _identityGuard.Attach(value);
            }
        }
    }

    internal void BeginPublication() => _identityGuard.BeginPublication();
    internal void ReleaseIdentityGuards() => _identityGuard.Release(_objects);
    internal ObjectConstructionIdentityGuard IdentityGuard => _identityGuard;
}
