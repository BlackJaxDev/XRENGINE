using MemoryPack;
using XREngine.Core.Files;

namespace XREngine.Scene
{
    /// <summary>
    /// Manages all 3D scene data for a particular consistent instance.
    /// For example, multiple viewports can point to cameras in this world and see the same 3D scene from different viewpoints.
    /// </summary>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRWorld : XRAsset
    {
        private List<XRScene> _scenes = [];
        private GameMode? _defaultGameMode = null;
        private WorldSettings _settings = new();

        public List<XRScene> Scenes
        {
            get => _scenes;
            set => SetField(ref _scenes, value);
        }
        public GameMode? DefaultGameMode
        {
            get => _defaultGameMode;
            set => SetField(ref _defaultGameMode, value);
        }
        public WorldSettings Settings
        {
            get => _settings;
            set => SetField(ref _settings, value);
        }

        public XRWorld() { }
        public XRWorld(string name) : base(name) { }
        public XRWorld(string name, params XRScene[] scenes) : base(name)
        {
            Scenes.AddRange(scenes);
        }
        public XRWorld(string name, List<XRScene> scenes) : base(name)
        {
            Scenes.AddRange(scenes);
        }
        public XRWorld(string name, WorldSettings settings, params XRScene[] scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
        }
        public XRWorld(string name, WorldSettings settings, IEnumerable<XRScene> scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
        }
        public XRWorld(string name, GameMode? defaultGameMode, params XRScene[] scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(string name, GameMode? defaultGameMode, IEnumerable<XRScene> scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(string name, WorldSettings settings, GameMode? defaultGameMode, params XRScene[] scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(string name, WorldSettings settings, GameMode? defaultGameMode, IEnumerable<XRScene> scenes) : base(name)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(params XRScene[] scenes)
        {
            Scenes.AddRange(scenes);
        }
        public XRWorld(List<XRScene> scenes)
        {
            Scenes.AddRange(scenes);
        }
        public XRWorld(WorldSettings settings, params XRScene[] scenes)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
        }
        public XRWorld(WorldSettings settings, IEnumerable<XRScene> scenes)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
        }
        public XRWorld(GameMode? defaultGameMode, params XRScene[] scenes)
        {
            Scenes.AddRange(scenes);
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(GameMode? defaultGameMode, IEnumerable<XRScene> scenes)
        {
            Scenes.AddRange(scenes);
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(WorldSettings settings, GameMode? defaultGameMode, params XRScene[] scenes)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
            DefaultGameMode = defaultGameMode;
        }
        public XRWorld(WorldSettings settings, GameMode? defaultGameMode, IEnumerable<XRScene> scenes)
        {
            Scenes.AddRange(scenes);
            Settings = settings;
            DefaultGameMode = defaultGameMode;
        }
    }
}