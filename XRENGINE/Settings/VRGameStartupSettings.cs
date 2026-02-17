using OpenVR.NET.Manifest;

namespace XREngine
{
    public enum EVRRuntime
    {
        /// <summary>
        /// Uses OpenXR when available, otherwise falls back to OpenVR.
        /// </summary>
        Auto,
        /// <summary>
        /// Forces OpenXR initialization.
        /// </summary>
        OpenXR,
        /// <summary>
        /// Forces OpenVR (SteamVR/OpenVR.NET) initialization.
        /// </summary>
        OpenVR,
    }

    public interface IVRGameStartupSettings
    {
        VrManifest? VRManifest { get; set; }
        IActionManifest? ActionManifest { get; }
        EVRRuntime VRRuntime { get; set; }
        bool EnableOpenXrVulkanParallelRendering { get; set; }
        string GameName { get; set; }
        (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths { get; set; }
    }

    public class VRGameStartupSettings<TCategory, TAction> : GameStartupSettings, IVRGameStartupSettings
        where TCategory : struct, Enum
        where TAction : struct, Enum
    {
        private VrManifest? _vrManifest;
        private ActionManifest<TCategory, TAction>? _actionManifest;
        private (Environment.SpecialFolder folder, string relativePath)[] _gameSearchPaths = [];
        private string _gameName = "XREngine Game";
        private EVRRuntime _vrRuntime = EVRRuntime.Auto;
        private bool _enableOpenXrVulkanParallelRendering = true;

        /// <summary>
        /// The name of the process to search for when running in client mode.
        /// </summary>
        public string GameName
        {
            get => _gameName;
            set => SetField(ref _gameName, value);
        }
        /// <summary>
        /// Paths to search for game exe server when running in client mode.
        /// </summary>
        public (Environment.SpecialFolder folder, string relativePath)[] GameSearchPaths
        {
            get => _gameSearchPaths;
            set => SetField(ref _gameSearchPaths, value);
        }
        public VrManifest? VRManifest
        {
            get => _vrManifest;
            set => SetField(ref _vrManifest, value);
        }
        public ActionManifest<TCategory, TAction>? ActionManifest
        {
            get => _actionManifest;
            set => SetField(ref _actionManifest, value);
        }
        IActionManifest? IVRGameStartupSettings.ActionManifest => ActionManifest;

        public EVRRuntime VRRuntime
        {
            get => _vrRuntime;
            set => SetField(ref _vrRuntime, value);
        }

        /// <summary>
        /// If true, OpenXR Vulkan path may run per-eye visibility buffer generation in parallel when the renderer supports multiple graphics queues.
        /// </summary>
        public bool EnableOpenXrVulkanParallelRendering
        {
            get => _enableOpenXrVulkanParallelRendering;
            set => SetField(ref _enableOpenXrVulkanParallelRendering, value);
        }
    }
}