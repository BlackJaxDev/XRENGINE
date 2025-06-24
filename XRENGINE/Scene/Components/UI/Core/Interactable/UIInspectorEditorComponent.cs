using System.Reflection;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Base class for UI components that edit properties in the inspector.
    /// </summary>
    public abstract class UIInspectorEditorComponent : UIInteractableComponent
    {
        private PropertyInfo? _property;
        /// <summary>
        /// This is the property that this component is editing.
        /// </summary>
        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }

        private object?[]? _targets;
        /// <summary>
        /// These are the targets that this component is editing. The property will be set on each of these targets.
        /// </summary>
        public object?[]? Targets
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }
    }
}
