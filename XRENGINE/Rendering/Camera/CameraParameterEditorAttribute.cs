using System;

namespace XREngine.Rendering
{
    /// <summary>
    /// Attribute to specify metadata for camera parameter types in the editor.
    /// Apply this to custom <see cref="XRCameraParameters"/> implementations to control
    /// how they appear in the camera editor dropdown and optionally provide a custom editor.
    /// </summary>
    /// <example>
    /// <code>
    /// [CameraParameterEditor("My Custom Camera", SortOrder = 5)]
    /// public class MyCustomCameraParameters : XRCameraParameters
    /// {
    ///     // ...
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CameraParameterEditorAttribute : Attribute
    {
        /// <summary>
        /// Creates a new camera parameter editor attribute with default settings.
        /// The display name will be auto-generated from the type name.
        /// </summary>
        public CameraParameterEditorAttribute()
        {
        }

        /// <summary>
        /// Creates a new camera parameter editor attribute with a custom display name.
        /// </summary>
        /// <param name="displayName">The name to show in the camera type dropdown.</param>
        public CameraParameterEditorAttribute(string displayName)
        {
            DisplayName = displayName;
        }

        /// <summary>
        /// The name to display in the camera type dropdown.
        /// If null or empty, a name will be auto-generated from the type name
        /// by removing "XR" prefix and "CameraParameters" suffix.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// The sort order for this camera type in the dropdown.
        /// Lower values appear first. Default is 100.
        /// Built-in types use: Perspective=0, Orthographic=1, Physical=2, OpenXRFov=3, OVR=4.
        /// </summary>
        public int SortOrder { get; set; } = 100;

        /// <summary>
        /// Optional type implementing <see cref="ICameraParameterCustomEditor"/> to provide
        /// a custom ImGui editor for this parameter type.
        /// If null, the default reflection-based inspector will be used.
        /// </summary>
        public Type? CustomEditorType { get; set; }

        /// <summary>
        /// If true, this camera type will be hidden from the editor dropdown.
        /// Useful for internal/abstract parameter types that shouldn't be directly selectable.
        /// </summary>
        public bool Hidden { get; set; } = false;

        /// <summary>
        /// Optional category name for grouping camera types in the dropdown.
        /// Types with the same category will be grouped together with a separator.
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// Optional tooltip/description shown when hovering over this type in the dropdown.
        /// </summary>
        public string? Description { get; set; }
    }
}
