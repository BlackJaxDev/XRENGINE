﻿using XREngine.Scene;

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
    /// <summary>
    /// The currently selected scene nodes.
    /// </summary>
    public static SceneNode[] SceneNodes
    {
        get => _sceneNodes;
        set
        {
            _sceneNodes = value;
            SelectionChanged?.Invoke(value);
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
        }
    }

    /// <summary>
    /// The first selected scene node, if any.
    /// </summary>
    public static SceneNode? SceneNode
    {
        get => SceneNodes.Length > 0 ? SceneNodes[0] : null;
        set => SceneNodes = value is not null ? [value] : [];
    }
}
