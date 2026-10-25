using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

public partial class UIListTransform
{
    private ulong _virtualBoundsIntentRevision;

    /// <summary>
    /// Assigns both bounds while caller and list ownership remain current.
    /// A veto or callback conflict stops later writes. Earlier accepted writes remain.
    /// The validator must check caller ownership without changing this list.
    /// </summary>
    public void SetVirtualBoundsGuarded<TState>(float upper, float lower, TState state,
        Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
        var admission = (Owner: this, Revision: checked(++_virtualBoundsIntentRevision),
            Identity: ObjectConstructionIdentity.Capture(this), State: state, RequireCurrent: requireCurrent);
        SetFieldValidated(ref _upperVirtualBound, upper, admission,
            static (guard, _, _, _) => ValidateBoundsWrite(guard),
            propertyName: nameof(UpperVirtualBound));
        ValidateBoundsWrite(admission);
        if (!_upperVirtualBound.Equals(upper))
            throw new InvalidOperationException("The upper virtual bound write was rejected.");
        SetFieldValidated(ref _lowerVirtualBound, lower, admission,
            static (guard, _, _, _) => ValidateBoundsWrite(guard),
            propertyName: nameof(LowerVirtualBound));
        ValidateBoundsWrite(admission);
        if (!_lowerVirtualBound.Equals(lower))
            throw new InvalidOperationException("The lower virtual bound write was rejected.");
    }

    private static void ValidateBoundsWrite<TState>((UIListTransform Owner, ulong Revision,
        ObjectConstructionIdentity Identity, TState State, Action<TState> RequireCurrent) guard)
    {
        RequireBoundsOwner(guard.Owner, guard.Revision, guard.Identity);
        guard.RequireCurrent(guard.State);
        RequireBoundsOwner(guard.Owner, guard.Revision, guard.Identity);
    }

    private static void RequireBoundsOwner(UIListTransform owner, ulong revision,
        ObjectConstructionIdentity identity)
    {
        if (!identity.IsCurrent || owner.IsDestroyed || owner.IsDestroyQueued ||
            owner._virtualBoundsIntentRevision != revision)
            throw new InvalidOperationException("The virtual bounds owner changed during assignment.");
    }
}
