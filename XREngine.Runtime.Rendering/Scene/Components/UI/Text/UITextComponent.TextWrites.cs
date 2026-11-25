using System;
using System.ComponentModel;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UITextComponent
{
    private ulong _textWriteIntentRevision;
    private Action? _activeTextWriteAdmission;

    /// <summary>Identifies the latest display text write attempt.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong TextWriteIntentRevision => _textWriteIntentRevision;

    /// <summary>Writes display text only while this component and the caller stay current.</summary>
    public bool SetTextGuarded<TState>(string? value, TState state,
        Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { } node)
            throw new InvalidOperationException("The display text owner is unavailable.");
        ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ulong revision = checked(++_textWriteIntentRevision);
        Action? previousAdmission = _activeTextWriteAdmission;
        Action admission = null!;
        void Require()
        {
            if (!identity.IsCurrent || !nodeIdentity.IsCurrent ||
                node.IsDestroyed || node.IsDestroyQueued ||
                IsDestroyed || IsDestroyQueued ||
                !ReferenceEquals(SceneNode, node) ||
                !UITextWriteOwner.Contains(node, this) ||
                _textWriteIntentRevision != revision ||
                !ReferenceEquals(_activeTextWriteAdmission, admission))
                throw new InvalidOperationException("The display text owner changed during assignment.");
            requireCurrent(state);
            if (!identity.IsCurrent || !nodeIdentity.IsCurrent ||
                node.IsDestroyed || node.IsDestroyQueued ||
                IsDestroyed || IsDestroyQueued ||
                !ReferenceEquals(SceneNode, node) ||
                !UITextWriteOwner.Contains(node, this) ||
                _textWriteIntentRevision != revision ||
                !ReferenceEquals(_activeTextWriteAdmission, admission))
                throw new InvalidOperationException("The display text owner changed during validation.");
        }

        admission = Require;
        _activeTextWriteAdmission = admission;
        try
        {
            string? originalText = _text;
            bool changed = SetFieldValidated(ref _text, value, admission,
                static (currentAdmission, original, current, _) =>
                {
                    currentAdmission();
                    if (!string.Equals(original, current, StringComparison.Ordinal))
                        throw new InvalidOperationException("The display text changed during assignment.");
                }, propertyName: nameof(Text));
            Require();
            if (!changed && !ReferenceEquals(originalText, value))
                throw new InvalidOperationException("The display text write was rejected.");
            if (!string.Equals(_text, value, StringComparison.Ordinal))
                throw new InvalidOperationException("The display text write was rejected.");
            return changed;
        }
        finally
        {
            if (ReferenceEquals(_activeTextWriteAdmission, admission))
                _activeTextWriteAdmission = previousAdmission;
        }
    }
}
