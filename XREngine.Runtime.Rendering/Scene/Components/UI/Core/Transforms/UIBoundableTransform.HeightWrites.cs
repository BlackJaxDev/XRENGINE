using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UIBoundableTransform
{
    private ulong _heightWriteIntentRevision;

    /// <summary>Gets the latest height write intent, including equal-value requests.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong HeightWriteIntentRevision => _heightWriteIntentRevision;

    private void AdvanceHeightWriteIntent()
        => SetField(ref _heightWriteIntentRevision, checked(_heightWriteIntentRevision + 1), publishNotifications: false);

    /// <summary>Writes height while the caller and exact attached owner remain current.</summary>
    public bool SetHeightGuarded<TState>(float? value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { IsTransformNull: false } node || !ReferenceEquals(node.Transform, this))
            throw new InvalidOperationException("The height owner is unavailable.");
        AdvanceHeightWriteIntent();
        var proof = (Owner: new UIGeometryWriteOwner(this, node), Target: this,
            Intent: _heightWriteIntentRevision, State: state, RequireCurrent: requireCurrent);
        bool changed = SetFieldValidated(ref _height, value, proof,
            static (guard, original, current, _) =>
            {
                RequireHeightWrite(guard);
                if (!Nullable.Equals(original, current))
                    throw new InvalidOperationException("Height changed during assignment.");
            }, propertyName: nameof(Height));
        RequireHeightWrite(proof);
        if (!Nullable.Equals(_height, value))
            throw new InvalidOperationException("The height write was rejected.");
        return changed;
    }

    private static void RequireHeightWrite<TState>((UIGeometryWriteOwner Owner,
        UIBoundableTransform Target, ulong Intent, TState State, Action<TState> RequireCurrent) guard)
    {
        guard.Owner.RequireCurrent();
        if (guard.Target._heightWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer height write replaced this request.");
        guard.RequireCurrent(guard.State);
        guard.Owner.RequireCurrent();
        if (guard.Target._heightWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer height write replaced this request.");
    }
}
