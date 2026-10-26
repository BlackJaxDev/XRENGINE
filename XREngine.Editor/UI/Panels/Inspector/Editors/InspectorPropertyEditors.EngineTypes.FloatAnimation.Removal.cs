using System.Globalization;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Components;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class EngineTypes
    {
        private sealed partial class FloatAnimationDetail
        {
            private SceneNode? _groupRemovalControls;
            private UITextComponent? _groupRemovalStatus;
            private UIButtonComponent? _previousRemovalCurve;
            private UIButtonComponent? _nextRemovalCurve;
            private UIButtonComponent? _markRemovalKey;
            private UIButtonComponent? _removeMarkedKeys;
            private InspectorFloatCurveRemovalMarks? _removalMarks;
            private InspectorFloatCurveLengthSelection? _presentedRemovalSelection;
            private ulong _presentedRemovalRevision;
            private ulong _presentedRemovalRows;
            private ulong _presentedRemovalIntent;
            private int _groupCurveIndex;
            private FloatKeyframe? _groupSelected;
            private readonly ObjectConstructionIdentity _removalRootTransformIdentity;
            private Func<bool>? _ownsRemovalTable;
            private Func<bool>? _ownsRemovalTablePath;
            private Func<bool>? _ownsRemovalControls;

            private void BuildGroupedRemovalControls(SceneNode card)
            {
                _groupRemovalControls = card.NewChild();
                if (!ContinueBuild()) return;
                EnsureVerticalLayout(_groupRemovalControls);
                if (!ContinueBuild()) return;
                _groupRemovalStatus = AddInfoLabel(_groupRemovalControls, "Grouped key removal");
                if (!ContinueBuild()) return;
                _previousRemovalCurve = CreateInspectorButton(_groupRemovalControls,
                    "Previous removal curve", () => ChangeRemovalCurve(-1));
                if (!ContinueBuild()) return;
                _nextRemovalCurve = CreateInspectorButton(_groupRemovalControls,
                    "Next removal curve", () => ChangeRemovalCurve(1));
                if (!ContinueBuild()) return;
                _markRemovalKey = CreateInspectorButton(_groupRemovalControls,
                    "Mark / Unmark key for removal", ToggleRemovalMark);
                if (!ContinueBuild()) return;
                _removeMarkedKeys = CreateInspectorButton(_groupRemovalControls,
                    "Remove marked keys", RemoveMarkedKeys);
                if (!ContinueBuild()) return;
                SceneNode controls = _groupRemovalControls;
                var controlsTransform = controls.Transform;
                var cardTransform = card.Transform;
                ObjectConstructionIdentity controlsId = ObjectConstructionIdentity.Capture(controls);
                ObjectConstructionIdentity controlsTransformId = ObjectConstructionIdentity.Capture(controlsTransform);
                ObjectConstructionIdentity cardId = ObjectConstructionIdentity.Capture(card);
                ObjectConstructionIdentity cardTransformId = ObjectConstructionIdentity.Capture(cardTransform);
                XRComponent?[] builtControls = [_previousRemovalCurve, _nextRemovalCurve, _markRemovalKey, _removeMarkedKeys, _groupRemovalStatus];
                Func<bool>[] controlsCurrent = builtControls.Select(control => CaptureRemovalControl(control!, controls)).ToArray();
                _ownsRemovalControls = () => LiveRemovalIdentity(_rootIdentity) &&
                    LiveRemovalIdentity(_removalRootTransformIdentity) &&
                    ReferenceEquals(_root.Transform, _removalRootTransformIdentity.Target) &&
                    LiveRemovalIdentity(controlsId) && LiveRemovalIdentity(controlsTransformId) &&
                    LiveRemovalIdentity(cardId) && LiveRemovalIdentity(cardTransformId) &&
                    !controls.IsDestroyed && !controls.IsDestroyQueued &&
                    !card.IsDestroyed && !card.IsDestroyQueued &&
                    ReferenceEquals(controls.Transform, controlsTransform) &&
                    ReferenceEquals(controls.Parent, card) && ReferenceEquals(controlsTransform.Parent, cardTransform) &&
                    ReferenceEquals(card.Transform, cardTransform) && ReferenceEquals(card.Parent, _root) &&
                    ReferenceEquals(cardTransform.Parent, _root.Transform) && controlsCurrent.All(current => current());
            }

            private static bool LiveRemovalIdentity(ObjectConstructionIdentity identity)
                => identity.IsCurrent && identity.Target is { IsDestroyed: false, IsDestroyQueued: false };

            private static Func<bool> CaptureRemovalControl(XRComponent button, SceneNode parent)
            {
                SceneNode node = button.SceneNode;
                var transform = node.Transform;
                ObjectConstructionIdentity componentId = ObjectConstructionIdentity.Capture(button);
                ObjectConstructionIdentity nodeId = ObjectConstructionIdentity.Capture(node);
                ObjectConstructionIdentity transformId = ObjectConstructionIdentity.Capture(transform);
                return () => componentId.IsCurrent && nodeId.IsCurrent && transformId.IsCurrent &&
                    !button.IsDestroyed && !button.IsDestroyQueued && !node.IsDestroyed && !node.IsDestroyQueued &&
                    !transform.IsDestroyed && !transform.IsDestroyQueued &&
                    ReferenceEquals(button.SceneNode, node) && node.Components.Contains(button) &&
                    ReferenceEquals(node.Transform, transform) && ReferenceEquals(node.Parent, parent) &&
                    ReferenceEquals(transform.Parent, parent.Transform);
            }

            private bool RefreshGroupedRemovalVisibility(InspectorFloatCurveSource? source, ulong revision)
            {
                if (_lengthSelection is not null &&
                    (_ownsRemovalTable?.Invoke() != true || _ownsRemovalControls?.Invoke() != true))
                {
                    Retire();
                    return false;
                }
                if (_ownsRemovalControls?.Invoke() == true &&
                    _groupRemovalControls?.Transform is UITransform transform)
                    transform.Visibility = _lengthSelection is null ? EVisibility.Collapsed : EVisibility.Visible;
                return IsCurrent(source, revision) && (_lengthSelection is null || IsRemovalViewCurrent(revision));
            }

            private bool IsRemovalViewCurrent(ulong revision)
                => IsCurrent(null, revision) && _ownsRemovalTable?.Invoke() == true &&
                    _ownsRemovalControls?.Invoke() == true;

            private bool RefreshGroupedRemovalRows(ulong revision)
            {
                InspectorFloatCurveLengthSelection? group = _lengthSelection;
                InspectorFloatCurveRemovalMarks? marks = _removalMarks;
                marks?.PruneInvalid();
                if (!IsRemovalViewCurrent(revision)) return false;
                InspectorFloatCurveSource? curve = group is not null && marks is not null
                    ? group.Curves[_groupCurveIndex] : null;
                List<(ulong Id, FloatKeyframe Item)> snapshot = [];
                if (curve is not null)
                    foreach (FloatKeyframe key in curve.Track)
                    {
                        if (snapshot.Count == MaximumKeys || !IsRemovalViewCurrent(revision)) break;
                        if (!_ids.TryGetValue(key, out ulong id)) _ids.Add(key, id = ++_nextId);
                        _keys[id] = key;
                        snapshot.Add((id, key));
                    }
                if (!IsRemovalViewCurrent(revision)) return false;
                // Only the displayed curve needs row IDs. Marked keys keep their exact
                // references in the separate marking model, not recycled row objects.
                if (_ids.Count > MaximumKeys * 2)
                {
                    HashSet<FloatKeyframe> visible = [.. snapshot.Select(entry => entry.Item)];
                    foreach (FloatKeyframe key in _ids.Keys.Where(key => !visible.Contains(key)).ToArray())
                        _ids.Remove(key);
                }
                if (_groupSelected is not null && !snapshot.Any(entry => ReferenceEquals(entry.Item, _groupSelected)))
                    _groupSelected = null;
                _rows.Replace(snapshot);
                if (!IsRemovalViewCurrent(revision)) return false;
                _table!.SelectedId = _groupSelected is { } selected && _ids.TryGetValue(selected, out ulong selectedId)
                    ? selectedId : null;
                if (!IsRemovalViewCurrent(revision)) return false;
                if (_groupRemovalStatus is not null)
                {
                    FloatKeyframe? marked = marks?.GetMarkedKey(_groupCurveIndex);
                    _groupRemovalStatus.Text = curve is null
                        ? "Grouped removal supports up to 64 distinct curves and 100000 total keys."
                        : $"Curve {_groupCurveIndex + 1}/{group!.Curves.Length}: {curve.Curve.Name}. " +
                          $"Marked {marks!.MarkedCount}/{marks.Count}. " +
                          (marked is null ? "No key marked on this curve. " :
                              $"Marked key at {marked.Second.ToString("G9", CultureInfo.InvariantCulture)} seconds. ") +
                          (curve.Track.Count == 0 ? "This curve has no key; removal is unavailable." :
                              $"Select a row, then Mark / Unmark. Showing up to {MaximumKeys} keys.");
                }
                if (!IsRemovalViewCurrent(revision)) return false;
                if (_previousRemovalCurve is not null) _previousRemovalCurve.IsEnabled = curve is not null && _groupCurveIndex > 0;
                if (!IsRemovalViewCurrent(revision)) return false;
                if (_nextRemovalCurve is not null) _nextRemovalCurve.IsEnabled = curve is not null && _groupCurveIndex + 1 < group!.Curves.Length;
                if (!IsRemovalViewCurrent(revision)) return false;
                if (_markRemovalKey is not null) _markRemovalKey.IsEnabled = curve is not null && _groupSelected is not null;
                if (!IsRemovalViewCurrent(revision)) return false;
                if (_removeMarkedKeys is not null) _removeMarkedKeys.IsEnabled = marks is not null && marks.MarkedCount == marks.Count;
                return IsRemovalViewCurrent(revision);
            }

            private bool CanUseRemoval(out InspectorFloatCurveLengthSelection group,
                out InspectorFloatCurveRemovalMarks marks)
            {
                group = _lengthSelection!;
                marks = _removalMarks!;
                return CanBuild && _root.IsActiveInHierarchy && group is not null && marks is not null &&
                    _ownsRemovalTable?.Invoke() == true && _ownsRemovalControls?.Invoke() == true &&
                    ReferenceEquals(group, _presentedRemovalSelection) &&
                    _presentedRemovalRevision == _revision && _presentedRemovalRows == _rows.Revision &&
                    _presentedRemovalIntent == marks.IntentRevision && group.IsCurrent();
            }

            private void ChangeRemovalCurve(int direction)
            {
                if (!CanUseRemoval(out var group, out _)) return;
                int next = _groupCurveIndex + direction;
                if ((uint)next >= (uint)group.Curves.Length) return;
                _groupCurveIndex = next;
                _groupSelected = null;
                RequestRefresh();
            }

            private void ToggleRemovalMark()
            {
                if (!CanUseRemoval(out var group, out var marks) || _groupSelected is not { } key ||
                    _table?.SelectedId is not { } id || !_keys.TryGetValue(id, out var presented) ||
                    !ReferenceEquals(key, presented) || !ReferenceEquals(key.OwningTrack, group.Curves[_groupCurveIndex].Track))
                    return;
                if (ReferenceEquals(marks.GetMarkedKey(_groupCurveIndex), key)) marks.Unmark(_groupCurveIndex);
                else marks.Mark(_groupCurveIndex, key);
                RequestRefresh();
            }

            private void RemoveMarkedKeys()
            {
                if (!CanUseRemoval(out var group, out var marks) ||
                    !marks.TryCapture(out FloatKeyframe[] targets, out ulong intent))
                    return;
                if (InspectorFloatCurveActions.Remove(group, targets) && ReferenceEquals(_removalMarks, marks))
                    marks.ClearIfCurrent(intent);
                RequestRefresh();
            }
        }
    }
}
