using MemoryPack;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Scene;

public partial class XRScene
{
    private ulong _visibilityWriteIntentRevision;

    [YamlIgnore, MemoryPackIgnore]
    internal ulong VisibilityWriteIntentRevision => _visibilityWriteIntentRevision;

    private void SetVisibility(bool value)
    {
        SceneVisibilityWriteOwner owner = BeginVisibilityWrite();
        SetFieldValidated(ref _isVisible, value, owner,
            static (proof, original, current, _) =>
            {
                proof.RequireCurrent();
                if (original != current)
                    throw new InvalidOperationException("Scene visibility changed during assignment.");
            }, propertyName: nameof(IsVisible));
    }

    private SceneVisibilityWriteOwner BeginVisibilityWrite()
    {
        SetField(ref _visibilityWriteIntentRevision, checked(_visibilityWriteIntentRevision + 1), publishNotifications: false);
        return new(this, ObjectConstructionIdentity.Capture(this), _visibilityWriteIntentRevision,
            IsDestroyed, IsDestroyQueued);
    }

    /// <summary>Writes visibility only while the caller and the exact scene lifetime remain current.</summary>
    internal void SetVisibilityGuarded<TState>(bool value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (IsDestroyed || IsDestroyQueued)
            throw new InvalidOperationException("The scene visibility owner is no longer live.");
        SceneVisibilityWriteOwner owner = BeginVisibilityWrite();
        SetFieldValidated(ref _isVisible, value, (Owner: owner, State: state, Require: requireCurrent),
            static (proof, original, current, _) =>
            {
                proof.Owner.RequireCurrent();
                proof.Require(proof.State);
                proof.Owner.RequireCurrent();
                if (original != current)
                    throw new InvalidOperationException("Scene visibility changed during assignment.");
            }, propertyName: nameof(IsVisible));
        owner.RequireCurrent();
        requireCurrent(state);
        owner.RequireCurrent();
        if (_isVisible != value)
            throw new InvalidOperationException("The scene visibility write was rejected.");
    }
}
