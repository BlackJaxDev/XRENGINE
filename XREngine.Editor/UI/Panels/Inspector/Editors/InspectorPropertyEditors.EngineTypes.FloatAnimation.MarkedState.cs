using XREngine.Animation;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    public static partial class EngineTypes
    {
        private sealed partial class FloatAnimationDetail
        {
            private readonly List<UIButtonComponent> _markedStateButtons = [];

            private bool BuildMarkedStateControls(SceneNode parent)
            {
                if (!AddMarkedStateButton(parent, "Set marked key times", SetMarkedTime) ||
                    !AddMarkedStateButton(parent, "Set marked incoming values", SetMarkedIncomingValue) ||
                    !AddMarkedStateButton(parent, "Set marked incoming tangents", () => SetMarkedTangent(false)) ||
                    !AddMarkedStateButton(parent, "Set marked outgoing tangents", () => SetMarkedTangent(true)) ||
                    !AddMarkedStateButton(parent, "Set marked incoming weights", () => SetMarkedWeight(false)) ||
                    !AddMarkedStateButton(parent, "Set marked outgoing weights", () => SetMarkedWeight(true)))
                    return false;

                foreach (bool outgoing in new[] { false, true })
                {
                    string side = outgoing ? "outgoing" : "incoming";
                    foreach (EVectorInterpType mode in Enum.GetValues<EVectorInterpType>())
                        if (!AddMarkedStateButton(parent, $"Set marked {side} interpolation: {mode}",
                                () => SetMarkedInterpolation(outgoing, mode))) return false;
                    foreach (bool enabled in new[] { false, true })
                        if (!AddMarkedStateButton(parent,
                                $"Set marked {side} weighted mode: {(enabled ? "on" : "off")}",
                                () => SetMarkedWeightedSide(outgoing, enabled))) return false;
                }
                foreach (bool enabled in new[] { false, true })
                {
                    string state = enabled ? "on" : "off";
                    if (!AddMarkedStateButton(parent, $"Set marked value links: {state}",
                            () => SetMarkedSyncValues(enabled)) ||
                        !AddMarkedStateButton(parent, $"Set marked tangent direction links: {state}",
                            () => SetMarkedSyncDirections(enabled)) ||
                        !AddMarkedStateButton(parent, $"Set marked tangent magnitude links: {state}",
                            () => SetMarkedSyncMagnitudes(enabled))) return false;
                }
                return ContinueBuild();
            }

            private bool AddMarkedStateButton(SceneNode parent, string label, Action action)
            {
                if (!ContinueBuild()) return false;
                UIButtonComponent button = CreateInspectorButton(parent, label, action);
                if (!ContinueBuild()) return false;
                _markedStateButtons.Add(button);
                return true;
            }

            private void SetMarkedIncomingValue()
                => SetMarkedNumeric(InspectorFloatCurveActions.SetIncomingValue);

            private void SetMarkedTime()
                => SetMarkedNumeric(InspectorFloatCurveActions.SetTime);

            private void SetMarkedTangent(bool outgoing)
                => SetMarkedNumeric((selection, keys, value) =>
                    InspectorFloatCurveActions.SetTangent(selection, keys, outgoing, value));

            private void SetMarkedWeight(bool outgoing)
                => SetMarkedNumeric((selection, keys, value) =>
                    InspectorFloatCurveActions.SetWeight(selection, keys, outgoing, value));

            private void SetMarkedInterpolation(bool outgoing, EVectorInterpType mode)
                => ApplyMarkedState((selection, keys) =>
                    InspectorFloatCurveActions.SetInterpolation(selection, keys, outgoing, mode));

            private void SetMarkedWeightedSide(bool outgoing, bool enabled)
                => ApplyMarkedState((selection, keys) =>
                    InspectorFloatCurveActions.SetWeightedSide(selection, keys, outgoing, enabled));

            private void SetMarkedSyncValues(bool enabled)
                => ApplyMarkedState((selection, keys) =>
                    InspectorFloatCurveActions.SetSyncValues(selection, keys, enabled));

            private void SetMarkedSyncDirections(bool enabled)
                => ApplyMarkedState((selection, keys) =>
                    InspectorFloatCurveActions.SetSyncDirections(selection, keys, enabled));

            private void SetMarkedSyncMagnitudes(bool enabled)
                => ApplyMarkedState((selection, keys) =>
                    InspectorFloatCurveActions.SetSyncMagnitudes(selection, keys, enabled));

            private void ApplyMarkedState(Func<InspectorFloatCurveLengthSelection,
                IReadOnlyList<FloatKeyframe>, bool> apply)
            {
                if (!CanUseRemoval(out var selection, out var marks) ||
                    !marks.TryCapture(out FloatKeyframe[] targets, out ulong intent) ||
                    !ReferenceEquals(_removalMarks, marks) || marks.IntentRevision != intent ||
                    !IsRemovalViewCurrent(_presentedRemovalRevision)) return;
                if (apply(selection, targets) && ReferenceEquals(_removalMarks, marks))
                    marks.ClearIfCurrent(intent);
                RequestRefresh();
            }
        }
    }
}
