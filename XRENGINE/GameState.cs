using MemoryPack;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class GameState : XRAsset
    {
        private List<GameWindowStartupSettings>? _windows = [];

        [MemoryPackIgnore]
        private List<XRWorldInstance>? _worlds = [];

        public List<GameWindowStartupSettings>? Windows
        {
            get => _windows;
            set => SetField(ref _windows, value);
        }

        [MemoryPackIgnore]
        public List<XRWorldInstance>? Worlds
        {
            get => _worlds;
            set => SetField(ref _worlds, value);
        }
    }
}