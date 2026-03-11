using System.ComponentModel;

namespace XREngine
{
    public partial class UserSettings
    {
        private bool _imGuiShowEditorSceneHierarchy;

        [Category("Editor UI")]
        [Description("Show the hidden editor-scene hierarchy entries in the Hierarchy panel.")]
        public bool ImGuiShowEditorSceneHierarchy
        {
            get => _imGuiShowEditorSceneHierarchy;
            set => SetField(ref _imGuiShowEditorSceneHierarchy, value);
        }
    }
}
