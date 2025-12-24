using MemoryPack;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Scene
{
    /// <summary>
    /// Defines a collection of root scene nodes that can be loaded in and out of a world.
    /// </summary>
    [MemoryPackable]
    public partial class XRScene : XRAsset
    {
        [MemoryPackConstructor]
        public XRScene() { }
        public XRScene(string name) : base(name) { }
        public XRScene(params SceneNode[] rootNodes) => RootNodes.AddRange(rootNodes);
        public XRScene(string name, params SceneNode[] rootNodes) : base(name) => RootNodes.AddRange(rootNodes);

        private bool _isVisible = true;
        /// <summary>
        /// If the scene is currently visible in the world.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetField(ref _isVisible, value);
        }

        private bool _isEditorOnly = false;
        /// <summary>
        /// If true, this scene is used for editor-only content (gizmos, UI, tools) and will not be 
        /// saved to the world file or shown in the hierarchy panel.
        /// </summary>
        [MemoryPackIgnore]
        public bool IsEditorOnly
        {
            get => _isEditorOnly;
            set => SetField(ref _isEditorOnly, value);
        }

        private List<SceneNode> _rootObjects = [];
        /// <summary>
        /// All nodes that are at the root of the scene.
        /// Nodes can have any number of children, recursively.
        /// </summary>
        public List<SceneNode> RootNodes
        {
            get => _rootObjects;
            set => SetField(ref _rootObjects, value);
        }
    }
}
