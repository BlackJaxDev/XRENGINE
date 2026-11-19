using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Tracks the current selection of scene nodes in the editor.
/// </summary>
public static class Selection
{
    /// <summary>
    /// Raised when the selected nodes change.
    /// </summary>
    public static event Action<SceneNode[]>? SelectionChanged;

    private static SceneNode[] _sceneNodes = [];
    private static SceneNode? _lastSceneNode;
    /// <summary>
    /// The currently selected scene nodes.
    /// </summary>
    public static SceneNode[] SceneNodes
    {
        get => _sceneNodes;
        set
        {
            _sceneNodes = value;
            if (value.Length > 0)
                _lastSceneNode = value[0];
            InvokeSelectionChangedTimed(value);
            /*
            switch (value.Length)
            {
                case 0:
                    Debug.Out("Selection cleared");
                    break;
                case 1:
                    Debug.Out($"Selection changed to {value[0].Name}");
                    break;
                default:
                    Debug.Out($"Selection changed to {value.Length} nodes: {string.Join(", ", value.Select(n => n.Name))}");
                    break;
            }
            */
        }
    }

    private const string SelectionProbeLogFile = "editor-pick-timing.log";
    private const double SelectionProbeThresholdMilliseconds = 4.0;
    private static int s_selectionProbeSequence;

    /// <summary>
    /// Invokes each selection subscriber separately and logs the subscribers that take
    /// the probe threshold or more, so a slow reaction to a selection change can be found.
    /// </summary>
    private static void InvokeSelectionChangedTimed(SceneNode[] value)
    {
        Action<SceneNode[]>? handlers = SelectionChanged;
        if (handlers is null)
            return;

        int sequence = ++s_selectionProbeSequence;
        long totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Delegate[] subscribers = handlers.GetInvocationList();
        for (int index = 0; index < subscribers.Length; index++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            ((Action<SceneNode[]>)subscribers[index])(value);
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsed >= SelectionProbeThresholdMilliseconds)
            {
                Delegate subscriber = subscribers[index];
                Debug.WriteAuxiliaryLog(
                    SelectionProbeLogFile,
                    $"[SelectionSubscriber] seq={sequence} {subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name} took {elapsed:F2} ms " +
                    $"(selected={value.Length} first='{(value.Length > 0 ? value[0].Name : string.Empty)}')");
            }
        }

        double total = System.Diagnostics.Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds;
        if (total >= SelectionProbeThresholdMilliseconds)
        {
            Debug.WriteAuxiliaryLog(
                SelectionProbeLogFile,
                $"[SelectionChanged] seq={sequence} total={total:F2} ms subscribers={subscribers.Length} selected={value.Length} " +
                $"first='{(value.Length > 0 ? value[0].Name : string.Empty)}'");
        }
    }

    /// <summary>
    /// The most recently selected scene node, even if the current selection is empty.
    /// </summary>
    public static SceneNode? LastSceneNode => _lastSceneNode;

    /// <summary>
    /// The first selected scene node, if any.
    /// </summary>
    public static SceneNode? SceneNode
    {
        get => SceneNodes.Length > 0 ? SceneNodes[0] : null;
        set => SceneNodes = value is not null ? [value] : [];
    }

    /// <summary>
    /// Clears the current selection.
    /// </summary>
    public static void Clear() => SceneNodes = [];
}
