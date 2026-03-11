using System.ComponentModel;

namespace XREngine
{
    public partial class UserSettings
    {
        private bool _imGuiShowHierarchy = true;
        private bool _imGuiShowEditorSceneHierarchy;
        private bool _imGuiShowInspector = true;
        private bool _imGuiShowAssetExplorer = true;
        private bool _imGuiShowConsole;
        private bool _imGuiShowRenderPipelineGraph;
        private bool _imGuiShowEngineState;
        private bool _imGuiShowProfiler;
        private bool _imGuiShowRenderApiObjects;
        private bool _imGuiShowRenderApiErrors;
        private bool _imGuiShowRenderApiExtensions;
        private bool _imGuiShowMissingAssets;
        private bool _imGuiShowNetworking;
        private bool _imGuiShowAnimationClipEditor;
        private bool _imGuiShowShaderGraph;
        private bool _imGuiShowArchiveInspector;

        [Category("Editor UI")]
        [Description("Show the Hierarchy panel in the ImGui editor.")]
        public bool ImGuiShowHierarchy
        {
            get => _imGuiShowHierarchy;
            set => SetField(ref _imGuiShowHierarchy, value);
        }

        [Category("Editor UI")]
        [Description("Show the hidden editor-scene hierarchy entries in the Hierarchy panel.")]
        public bool ImGuiShowEditorSceneHierarchy
        {
            get => _imGuiShowEditorSceneHierarchy;
            set => SetField(ref _imGuiShowEditorSceneHierarchy, value);
        }

        [Category("Editor UI")]
        [Description("Show the Inspector panel in the ImGui editor.")]
        public bool ImGuiShowInspector
        {
            get => _imGuiShowInspector;
            set => SetField(ref _imGuiShowInspector, value);
        }

        [Category("Editor UI")]
        [Description("Show the Asset Explorer panel in the ImGui editor.")]
        public bool ImGuiShowAssetExplorer
        {
            get => _imGuiShowAssetExplorer;
            set => SetField(ref _imGuiShowAssetExplorer, value);
        }

        [Category("Editor UI")]
        [Description("Show the Console panel in the ImGui editor.")]
        public bool ImGuiShowConsole
        {
            get => _imGuiShowConsole;
            set => SetField(ref _imGuiShowConsole, value);
        }

        [Category("Editor UI")]
        [Description("Show the Render Pipeline Graph panel in the ImGui editor.")]
        public bool ImGuiShowRenderPipelineGraph
        {
            get => _imGuiShowRenderPipelineGraph;
            set => SetField(ref _imGuiShowRenderPipelineGraph, value);
        }

        [Category("Editor UI")]
        [Description("Show the Engine State panel in the ImGui editor.")]
        public bool ImGuiShowEngineState
        {
            get => _imGuiShowEngineState;
            set => SetField(ref _imGuiShowEngineState, value);
        }

        [Category("Editor UI")]
        [Description("Show the Profiler panel in the ImGui editor.")]
        public bool ImGuiShowProfiler
        {
            get => _imGuiShowProfiler;
            set => SetField(ref _imGuiShowProfiler, value);
        }

        [Category("Editor UI")]
        [Description("Show the Render API Objects panel in the ImGui editor.")]
        public bool ImGuiShowRenderApiObjects
        {
            get => _imGuiShowRenderApiObjects;
            set => SetField(ref _imGuiShowRenderApiObjects, value);
        }

        [Category("Editor UI")]
        [Description("Show the Render API Errors panel in the ImGui editor.")]
        public bool ImGuiShowRenderApiErrors
        {
            get => _imGuiShowRenderApiErrors;
            set => SetField(ref _imGuiShowRenderApiErrors, value);
        }

        [Category("Editor UI")]
        [Description("Show the Render API Extensions panel in the ImGui editor.")]
        public bool ImGuiShowRenderApiExtensions
        {
            get => _imGuiShowRenderApiExtensions;
            set => SetField(ref _imGuiShowRenderApiExtensions, value);
        }

        [Category("Editor UI")]
        [Description("Show the Missing Assets panel in the ImGui editor.")]
        public bool ImGuiShowMissingAssets
        {
            get => _imGuiShowMissingAssets;
            set => SetField(ref _imGuiShowMissingAssets, value);
        }

        [Category("Editor UI")]
        [Description("Show the Networking panel in the ImGui editor.")]
        public bool ImGuiShowNetworking
        {
            get => _imGuiShowNetworking;
            set => SetField(ref _imGuiShowNetworking, value);
        }

        [Category("Editor UI")]
        [Description("Show the Animation Clip Editor panel in the ImGui editor.")]
        public bool ImGuiShowAnimationClipEditor
        {
            get => _imGuiShowAnimationClipEditor;
            set => SetField(ref _imGuiShowAnimationClipEditor, value);
        }

        [Category("Editor UI")]
        [Description("Show the Shader Graph panel in the ImGui editor.")]
        public bool ImGuiShowShaderGraph
        {
            get => _imGuiShowShaderGraph;
            set => SetField(ref _imGuiShowShaderGraph, value);
        }

        [Category("Editor UI")]
        [Description("Show the Archive Inspector panel in the ImGui editor.")]
        public bool ImGuiShowArchiveInspector
        {
            get => _imGuiShowArchiveInspector;
            set => SetField(ref _imGuiShowArchiveInspector, value);
        }
    }
}
