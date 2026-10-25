using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retains the owners and exact track of one inspector float curve.</summary>
internal sealed class InspectorFloatCurveSource
{
    private readonly (object Owner, Guid? Identity, object? World, SceneNode? Node,
        XRAsset? Document, Guid? DocumentIdentity,
        Func<object, PropAnimFloat, bool> IsCurrentSource,
        InspectorFloatTransitionOwnership? TransitionOwnership)[] _owners;
    private readonly Guid _curveIdentity;
    private readonly XRAsset _curveDocument;
    private readonly Guid _curveDocumentIdentity;

    private InspectorFloatCurveSource(PropAnimFloat curve,
        (object, Guid?, object?, SceneNode?, XRAsset?, Guid?,
            Func<object, PropAnimFloat, bool>,
            InspectorFloatTransitionOwnership?)[] owners)
    {
        Curve = curve;
        Track = curve.Keyframes;
        _curveIdentity = curve.ID;
        _curveDocument = curve.SourceAsset;
        _curveDocumentIdentity = _curveDocument.ID;
        _owners = owners;
        Documents = [.. owners.Select(entry => entry.Item5).OfType<XRAsset>()
            .Append(_curveDocument).Distinct<XRAsset>(ReferenceEqualityComparer.Instance)];
        ReservationOwners = [curve, Track, .. owners.Select(entry => entry.Item1)];
    }

    public PropAnimFloat Curve { get; }
    public KeyframeTrack<FloatKeyframe> Track { get; }
    public XRAsset[] Documents { get; }
    public object[] ReservationOwners { get; }

    public static bool TryCapture(PropertyInfo property, object?[]? targets,
        out InspectorFloatCurveSource? source, out string reason)
    {
        source = null;
        reason = "No valid targets.";
        if (targets is null || targets.Length == 0 ||
            property.GetMethod is null || property.GetMethod.IsStatic ||
            !typeof(PropAnimFloat).IsAssignableFrom(property.PropertyType))
            return false;
        EditorListSourceAccessor? accessor = EditorListSourceAccessor.For(property);
        Func<object, PropAnimFloat, bool>? isCurrentSource = accessor is not null
            ? (owner, expected) => accessor.IsCurrent(owner, expected)
            : property.DeclaringType == typeof(AnimStateTransition) &&
                property.Name == nameof(AnimStateTransition.CustomBlendFunction)
                ? (owner, expected) =>
                    ((AnimStateTransition)owner).IsCurrentCustomBlendFunction(expected)
                : null;
        reason = "This curve property has an unguarded getter.";
        if (isCurrentSource is null)
            return false;
        var owners = new List<(object, Guid?, object?, SceneNode?, XRAsset?, Guid?,
            Func<object, PropAnimFloat, bool>, InspectorFloatTransitionOwnership?)>();
        PropAnimFloat? curve = null;
        foreach (object? target in targets)
        {
            reason = "The curve owner is unavailable.";
            if (target is null || !property.DeclaringType!.IsInstanceOfType(target) ||
                target is XRObjectBase { IsDestroyed: true } or
                    XRObjectBase { IsDestroyQueued: true })
                return false;
            object? world = EditorListMutation.WorldOf(target);
            SceneNode? node = EditorListMutation.NodeOf(target);
            InspectorFloatTransitionOwnership? transitionOwnership = null;
            if (target is AnimStateTransition transition &&
                !InspectorFloatTransitionOwnership.TryCapture(transition,
                    out transitionOwnership, out reason))
                return false;
            XRAsset? document = transitionOwnership?.Document ??
                EditorMutationDirtyState.ResolveDocument(target);
            if (target is XRWorldObjectBase && document is null)
                return false;
            reason = "The curve source changed while it was read.";
            PropAnimFloat? value;
            try { value = property.GetValue(target) as PropAnimFloat; }
            catch (TargetInvocationException)
            {
                reason = "The curve getter failed.";
                return false;
            }
            if (value is null || !isCurrentSource(target, value))
                return false;
            if (curve is not null && !ReferenceEquals(curve, value))
            {
                reason = "Multiple curves are selected.";
                return false;
            }
            curve = value;
            owners.Add((target, (target as XRObjectBase)?.ID, world, node,
                document, document?.ID, isCurrentSource, transitionOwnership));
        }
        reason = "The curve or its track is unavailable.";
        if (curve is null || curve.GetType() != typeof(PropAnimFloat) ||
            curve.IsDestroyed || curve.IsDestroyQueued)
            return false;
        KeyframeTrack<FloatKeyframe>? track = curve.Keyframes;
        if (track is null ||
            track.GetType() != typeof(KeyframeTrack<FloatKeyframe>))
            return false;
        source = new(curve, [.. owners]);
        if (source.IsCurrent())
        {
            reason = string.Empty;
            return true;
        }
        source = null;
        return false;
    }

    public bool IsCurrent()
    {
        if (!IsCurrentFast())
            return false;
        foreach (var entry in _owners)
        {
            if (entry.TransitionOwnership is { } ownership &&
                !ownership.IsCurrent())
                return false;
            XRAsset? document = entry.TransitionOwnership is { } transition
                ? transition.Document
                : EditorMutationDirtyState.ResolveDocument(entry.Owner);
            if (!ReferenceEquals(document, entry.Document))
                return false;
        }
        return true;
    }

    /// <summary>Checks identities without resolving scene documents or allocating.</summary>
    public bool IsCurrentFast()
    {
        if (Curve.IsDestroyed || Curve.IsDestroyQueued || Curve.ID != _curveIdentity ||
            _curveDocument.IsDestroyed || _curveDocument.IsDestroyQueued ||
            _curveDocument.ID != _curveDocumentIdentity ||
            !ReferenceEquals(Curve.SourceAsset, _curveDocument) ||
            !ReferenceEquals(Curve.Keyframes, Track))
            return false;
        foreach (var entry in _owners)
        {
            if (entry.Owner is XRObjectBase baseOwner &&
                (baseOwner.IsDestroyed || baseOwner.IsDestroyQueued ||
                 baseOwner.ID != entry.Identity) ||
                !ReferenceEquals(EditorListMutation.WorldOf(entry.Owner), entry.World) ||
                !ReferenceEquals(EditorListMutation.NodeOf(entry.Owner), entry.Node) ||
                entry.Document is { } document &&
                    (document.IsDestroyed || document.IsDestroyQueued ||
                     document.ID != entry.DocumentIdentity) ||
                entry.TransitionOwnership is { } transition &&
                    !transition.IsCurrentFast() ||
                !entry.IsCurrentSource(entry.Owner, Curve))
                return false;
        }
        return true;
    }
}
