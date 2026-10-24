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
