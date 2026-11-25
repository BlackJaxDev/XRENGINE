using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UITransform
{
    private ulong _visibilityWriteIntentRevision;

    /// <summary>Gets the latest visibility write intent, including equal-value requests.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong VisibilityWriteIntentRevision => _visibilityWriteIntentRevision;

    private void AdvanceVisibilityWriteIntent()
        => SetField(ref _visibilityWriteIntentRevision, checked(_visibilityWriteIntentRevision + 1),
            publishNotifications: false);

    /// <summary>Writes visibility only while the attached transform and caller still own the change.</summary>
    public bool SetVisibilityGuarded<TState>(EVisibility value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { IsTransformNull: false } node || !ReferenceEquals(node.Transform, this))
            throw new InvalidOperationException("The visibility owner is unavailable.");
        AdvanceVisibilityWriteIntent();
        UIVisibilityWriteOwner owner = new(this, node, _visibilityWriteIntentRevision);
        bool changed = SetFieldValidated(ref _visibility, value,
            (Owner: owner, State: state, RequireCurrent: requireCurrent),
            static (proof, original, current, _) =>
            {
                proof.Owner.RequireCurrent();
                proof.RequireCurrent(proof.State);
                proof.Owner.RequireCurrent();
                if (original != current)
                    throw new InvalidOperationException("Visibility changed during assignment.");
            }, propertyName: nameof(Visibility));
        owner.RequireCurrent();
        requireCurrent(state);
        owner.RequireCurrent();
        if (_visibility != value)
            throw new InvalidOperationException("The visibility write was rejected.");
        return changed;
    }
}
