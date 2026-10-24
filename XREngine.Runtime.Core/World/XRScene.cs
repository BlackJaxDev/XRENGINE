using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Scene
{
    /// <summary>
    /// Defines a collection of root scene nodes that can be loaded in and out of a world.
    /// </summary>
    [CookedBinaryReflectionOnly]
    [MemoryPackable]
    public partial class XRScene : XRAsset
    {
        [MemoryPackConstructor]
        public XRScene() { }
        public XRScene(string name) : base(name) { }
        public XRScene(params SceneNode[] rootNodes) => SceneRoots.AddRange(rootNodes);
        public XRScene(string name, params SceneNode[] rootNodes) : base(name) => SceneRoots.AddRange(rootNodes);

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

        private SceneRootCollection? _sceneRoots;
        /// <summary>
        /// Serialization view with the existing list name and wire type. Use SceneRoots for runtime edits.
        /// The getter returns a copy; changes to that copy do not change scene ownership.
        /// </summary>
        [Obsolete("RootNodes is a serialization view. Use SceneRoots for scene root access.")]
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
        public List<SceneNode> RootNodes
        {
            get => SceneRoots.ToSerializationList();
            set => ReplaceSceneRoots(value);
        }

        /// <summary>Gets the current collection that owns this scene's root links.</summary>
        [YamlIgnore, MemoryPackIgnore, Browsable(false)]
        public SceneRootCollection SceneRoots
        {
            get
            {
                lock (SceneRootCollection.MutationGate)
                {
                    if (_sceneRoots is null)
                    {
                        if (!CanChangeSceneRoots)
                            throw new InvalidOperationException("The scene cannot acquire root ownership while its lifetime is changing.");
                        SetField(ref _sceneRoots, new(this), publishNotifications: false, nameof(SceneRoots));
                        _sceneRoots!.Attach();
                    }
                    return _sceneRoots;
                }
            }
        }

        /// <summary>Replaces root ownership and invalidates the previous collection incarnation.</summary>
        public void ReplaceSceneRoots(IEnumerable<SceneNode>? roots)
            => ReplaceSceneRootsCore(roots, transferFrom: null);

        /// <summary>Moves a temporary scene's root links without leaving that scene as a second owner.</summary>
        public void TakeSceneRootsFrom(XRScene source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (ReferenceEquals(source, this))
                return;
            SceneRootCollection roots = source.SceneRoots;
            ReplaceSceneRootsCore(roots, roots);
        }

        private void ReplaceSceneRootsCore(IEnumerable<SceneNode>? roots, SceneRootCollection? transferFrom)
        {
            SceneRootCollection previous = SceneRoots;
            ulong revision;
            ulong sourceRevision;
            lock (SceneRootCollection.MutationGate)
            {
                previous.RequireCurrent();
                transferFrom?.RequireCurrent();
                revision = previous.Revision;
                sourceRevision = transferFrom?.Revision ?? 0;
            }
            SceneRootCollection replacement = new(this, roots);
            replacement.ValidatePending();
            List<SceneNode> before = previous.ToSerializationList();
            List<SceneNode> after = replacement.ToSerializationList();
            const string serializedName = "RootNodes";
            bool notify = !ArePropertyNotificationsSuppressed;
            if (notify && !OnPropertyChanging(serializedName, before, after))
                return;
            lock (SceneRootCollection.MutationGate)
            {
                if (!ReferenceEquals(_sceneRoots, previous) || previous.Revision != revision)
                    throw new InvalidOperationException("The scene root owner changed during replacement callbacks.");
                previous.RequireCurrent();
                if (transferFrom is not null)
                {
                    transferFrom.RequireCurrent();
                    if (transferFrom.Revision != sourceRevision)
                        throw new InvalidOperationException("The source scene roots changed during transfer callbacks.");
                }
                replacement.ValidatePending();
                previous.ValidateRelease();
                transferFrom?.ValidateRelease();
                replacement.Attach();
                previous.Retire();
                transferFrom?.Clear();
                SetField(ref _sceneRoots, replacement, publishNotifications: false, nameof(SceneRoots));
            }
            if (notify)
                OnPropertyChanged(serializedName, before, after);
        }

        internal bool HasSceneRoots(SceneRootCollection roots) => ReferenceEquals(_sceneRoots, roots) && CanChangeSceneRoots;

        public override bool Load3rdParty(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
            read.EnsureHostFileAccess("Third-party scene import");
            if (!read.Exists(filePath))
                return false;

            try
            {
                Name = Path.GetFileNameWithoutExtension(filePath);
                IRuntimeSceneImportServices? importer = RuntimeSceneImportServices.Current;
                if (importer is null)
                    return false;

                ReplaceSceneRoots(importer.ImportScene(filePath));
                return true;
            }
            catch (Exception ex)
            {
                string exceptionText = ex.ToString()
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " | ", StringComparison.Ordinal);
                Debug.LogWarning($"Failed to import Unity scene '{filePath}': {exceptionText}");
                return false;
            }
        }
    }
}
