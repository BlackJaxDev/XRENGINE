using System;

namespace XREngine.Rendering
{
    /// <summary>
    /// Interface for implementing custom ImGui editors for camera parameter types.
    /// Implement this interface and reference it via <see cref="CameraParameterEditorAttribute.CustomEditorType"/>
    /// to provide a custom editor UI for your camera parameter type.
    /// </summary>
    /// <example>
    /// <code>
    /// public class MyCustomCameraEditor : ICameraParameterCustomEditor
    /// {
    ///     public void DrawEditor(XRCameraParameters parameters)
    ///     {
    ///         if (parameters is not MyCustomCameraParameters custom)
    ///             return;
    ///         
    ///         // Draw ImGui controls here
    ///         float myValue = custom.MyProperty;
    ///         if (ImGui.DragFloat("My Property", ref myValue))
    ///             custom.MyProperty = myValue;
    ///     }
    /// }
    /// 
    /// [CameraParameterEditor("My Custom Camera", CustomEditorType = typeof(MyCustomCameraEditor))]
    /// public class MyCustomCameraParameters : XRCameraParameters
    /// {
    ///     public float MyProperty { get; set; }
    ///     // ...
    /// }
    /// </code>
    /// </example>
    public interface ICameraParameterCustomEditor
    {
        /// <summary>
        /// Draws the custom ImGui editor UI for the given camera parameters.
        /// This is called every frame when the camera component is selected in the inspector.
        /// </summary>
        /// <param name="parameters">The camera parameters instance to edit. Cast to your specific type.</param>
        void DrawEditor(XRCameraParameters parameters);
    }

    /// <summary>
    /// Static registry for camera parameter custom editors.
    /// This allows the editor to find and instantiate custom editors for parameter types.
    /// </summary>
    public static class CameraParameterEditorRegistry
    {
        private static readonly Dictionary<Type, ICameraParameterCustomEditor> _editorCache = [];
        private static readonly object _lock = new();

        /// <summary>
        /// Gets or creates a cached instance of the custom editor for the given parameter type.
        /// Returns null if no custom editor is registered for the type.
        /// </summary>
        /// <param name="parameterType">The camera parameter type to get an editor for.</param>
        /// <returns>The custom editor instance, or null if none is registered.</returns>
        public static ICameraParameterCustomEditor? GetEditor(Type parameterType)
        {
            lock (_lock)
            {
                if (_editorCache.TryGetValue(parameterType, out var cached))
                    return cached;

                var attr = parameterType.GetCustomAttributes(typeof(CameraParameterEditorAttribute), false)
                    .FirstOrDefault() as CameraParameterEditorAttribute;

                if (attr?.CustomEditorType is null)
                    return null;

                if (!typeof(ICameraParameterCustomEditor).IsAssignableFrom(attr.CustomEditorType))
                {
                    Debug.LogWarning($"Custom editor type {attr.CustomEditorType.Name} does not implement ICameraParameterCustomEditor");
                    return null;
                }

                try
                {
                    var editor = Activator.CreateInstance(attr.CustomEditorType) as ICameraParameterCustomEditor;
                    if (editor is not null)
                        _editorCache[parameterType] = editor;
                    return editor;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to create custom editor {attr.CustomEditorType.Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Manually registers a custom editor for a parameter type.
        /// This can be used to override or add editors at runtime.
        /// </summary>
        /// <param name="parameterType">The camera parameter type.</param>
        /// <param name="editor">The custom editor instance.</param>
        public static void RegisterEditor(Type parameterType, ICameraParameterCustomEditor editor)
        {
            lock (_lock)
            {
                _editorCache[parameterType] = editor;
            }
        }

        /// <summary>
        /// Clears the editor cache, forcing editors to be re-created on next access.
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _editorCache.Clear();
            }
        }
    }
}
