using System.ComponentModel;
using YamlDotNet.Serialization;
using System.Numerics;

namespace XREngine.Rendering.UI;

public partial class UITransform
{
    private ulong _translationWriteIntentRevision;

    /// <summary>Gets the latest translation write intent, including equal-value requests.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong TranslationWriteIntentRevision => _translationWriteIntentRevision;

    private void AdvanceTranslationWriteIntent()
        => SetField(ref _translationWriteIntentRevision, checked(_translationWriteIntentRevision + 1), publishNotifications: false);

    /// <summary>Writes translation while the caller and exact attached owner remain current.</summary>
    public bool SetTranslationGuarded<TState>(Vector2 value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { IsTransformNull: false } node || !ReferenceEquals(node.Transform, this))
            throw new InvalidOperationException("The translation owner is unavailable.");
        AdvanceTranslationWriteIntent();
        var proof = (Owner: new UIGeometryWriteOwner(this, node), Target: this,
            Intent: _translationWriteIntentRevision, State: state, RequireCurrent: requireCurrent);
        bool changed = SetFieldValidated(ref _translation, value, proof,
            static (guard, original, current, _) =>
            {
                RequireTranslationWrite(guard);
                if (!original.Equals(current))
                    throw new InvalidOperationException("Translation changed during assignment.");
            }, propertyName: nameof(Translation));
        RequireTranslationWrite(proof);
        if (!_translation.Equals(value))
            throw new InvalidOperationException("The translation write was rejected.");
        return changed;
    }

    private static void RequireTranslationWrite<TState>((UIGeometryWriteOwner Owner,
        UITransform Target, ulong Intent, TState State, Action<TState> RequireCurrent) guard)
    {
        guard.Owner.RequireCurrent();
        if (guard.Target._translationWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer translation write replaced this request.");
        guard.RequireCurrent(guard.State);
        guard.Owner.RequireCurrent();
        if (guard.Target._translationWriteIntentRevision != guard.Intent)
            throw new InvalidOperationException("A newer translation write replaced this request.");
    }
}
