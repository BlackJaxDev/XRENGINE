using System.ComponentModel;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UIListTransform
{
    private ulong _contentScrollIntentRevision;

    /// <summary>Identifies the latest offset write attempt on the owner thread.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong ContentScrollIntentRevision => _contentScrollIntentRevision;

    /// <summary>
    /// Assigns an offset only while the caller and list retain the same ownership.
    /// The validator must check caller ownership without changing this list.
    /// A callback conflict throws before a stale write or later caller work proceeds.
    /// </summary>
    public bool SetContentScrollOffsetGuarded<TState>(float value, TState state,
        Action<TState> requireCurrent)
        => SetContentScrollOffsetCore(value, state, requireCurrent, true);

    private bool SetContentScrollOffsetCore<TState>(float value, TState state,
        Action<TState> requireCurrent, bool validateResult)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        GetMutationCanvas()?.ChangeJournal.VerifyMutationAccess();
        var admission = (Owner: this, Revision: checked(++_contentScrollIntentRevision),
            Identity: ObjectConstructionIdentity.Capture(this), State: state, RequireCurrent: requireCurrent);
        bool changed = SetFieldValidated(ref _contentScrollOffset, value, admission,
            static (guard, _, _, _) => ValidateScrollWrite(guard),
            propertyName: nameof(ContentScrollOffset));
        if (validateResult)
            ValidateScrollWrite(admission);
        return changed;
    }

    private static void ValidateScrollWrite<TState>((UIListTransform Owner, ulong Revision,
        ObjectConstructionIdentity Identity, TState State, Action<TState> RequireCurrent) guard)
    {
        RequireScrollOwner(guard.Owner, guard.Revision, guard.Identity);
        guard.RequireCurrent(guard.State);
        RequireScrollOwner(guard.Owner, guard.Revision, guard.Identity);
    }

    private static void RequireScrollOwner(UIListTransform owner, ulong revision,
        ObjectConstructionIdentity identity)
    {
        if (!identity.IsCurrent || owner.IsDestroyed || owner.IsDestroyQueued ||
            owner._contentScrollIntentRevision != revision)
            throw new InvalidOperationException("The list scroll owner changed during assignment.");
    }
}
