using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UIBoundableTransform
{
    private ulong _widthWriteIntentRevision;

    /// <summary>Gets the latest width write intent, including equal-value requests.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong WidthWriteIntentRevision => _widthWriteIntentRevision;

    private void AdvanceWidthWriteIntent()
        => SetField(ref _widthWriteIntentRevision, checked(_widthWriteIntentRevision + 1), publishNotifications: false);

    /// <summary>Writes width while the caller and exact attached owner remain current.</summary>
    public bool SetWidthGuarded<TState>(float? value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { IsTransformNull: false } node || !ReferenceEquals(node.Transform, this))
            throw new InvalidOperationException("The width owner is unavailable.");
        AdvanceWidthWriteIntent();
        var proof = (Owner: new UIGeometryWriteOwner(this, node), Target: this,
            Intent: _widthWriteIntentRevision, State: state, RequireCurrent: requireCurrent);
        bool changed = SetFieldValidated(ref _width, value, proof,
            static (guard, original, current, _) =>
            {
                RequireWidthWrite(guard);
                if (!Nullable.Equals(original, current))
                    throw new InvalidOperationException("Width changed during assignment.");
            }, propertyName: nameof(Width));
        RequireWidthWrite(proof);
        if (!Nullable.Equals(_width, value))
            throw new InvalidOperationException("The width write was rejected.");
        return changed;
    }

    private static void RequireWidthWrite<TState>((UIGeometryWriteOwner Owner,
        UIBoundableTransform Target, ulong Intent, TState State, Action<TState> RequireCurrent) guard)
    {
        guard.Owner.RequireCurrent();
        if (guard.Target._widthWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer width write replaced this request.");
        guard.RequireCurrent(guard.State);
        guard.Owner.RequireCurrent();
        if (guard.Target._widthWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer width write replaced this request.");
    }
}
