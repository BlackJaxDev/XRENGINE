using System.Reflection;
using XREngine.Scene;

namespace XREngine.Components.Capture.Lights
{
    public sealed class EditorSelectionAccessor
    {
        public static readonly Lazy<EditorSelectionAccessor?> Instance = new(Create, true);

        private readonly PropertyInfo _sceneNodesProperty;

        private EditorSelectionAccessor(PropertyInfo sceneNodesProperty)
        {
            _sceneNodesProperty = sceneNodesProperty;
        }

        public bool IsNodeSelected(SceneNode node)
        {
            if (_sceneNodesProperty.GetValue(null) is not Array selection)
                return false;

            for (int i = 0; i < selection.Length; ++i)
            {
                if (ReferenceEquals(selection.GetValue(i), node))
                    return true;
            }

            return false;
        }

        public static EditorSelectionAccessor? Create()
        {
            var selectionType = Type.GetType("XREngine.Editor.Selection, XREngine.Editor", throwOnError: false);
            if (selectionType is null)
                return null;

            var sceneNodesProperty = selectionType.GetProperty("SceneNodes", BindingFlags.Public | BindingFlags.Static);
            return sceneNodesProperty is null ? null : new EditorSelectionAccessor(sceneNodesProperty);
        }
    }
}
