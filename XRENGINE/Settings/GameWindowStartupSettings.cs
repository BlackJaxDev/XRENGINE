using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine
{
    [MemoryPackable]
    public partial class GameWindowStartupSettings : XRBase
    {
        private EWindowState _windowState = EWindowState.Windowed;
        private string? _windowTitle;
        private int _width = 1920;
        private int _height = 1080;
        private XRWorld? _targetWorld;
        private ELocalPlayerIndexMask _localPlayers = ELocalPlayerIndexMask.One;
        private int _x = 0;
        private int _y = 0;
        private bool _vsync = false;
        private bool _transparentFramebuffer = false;
        private bool? _outputHDR;
        private bool _useNativeTitleBar = true;
        
        [Category("Players")]
        [Description("Which local player indices render to this window.")]
        public ELocalPlayerIndexMask LocalPlayers
        {
            get => _localPlayers;
            set => SetField(ref _localPlayers, value);
        }

        [Category("Window")]
        [Description("Optional window title override. Null uses the engine/project default.")]
        public string? WindowTitle
        {
            get => _windowTitle;
            set => SetField(ref _windowTitle, value);
        }

        [Category("Resolution")]
        [Description("Initial window width in pixels (used for windowed mode).")]
        public int Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }

        [Category("Resolution")]
        [Description("Initial window height in pixels (used for windowed mode).")]
        public int Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        [Category("World")]
        [Description("World to load/render in this window. Null uses the default/world selection logic.")]
        public XRWorld? TargetWorld
        {
            get => _targetWorld;
            set => SetField(ref _targetWorld, value);
        }

        [Category("Window")]
        [Description("Initial window state at startup.")]
        public EWindowState WindowState
        {
            get => _windowState;
            set => SetField(ref _windowState, value);
        }

        [Category("Position")]
        [Description("Initial window X position in pixels (windowed mode).")]
        public int X
        {
            get => _x;
            set => SetField(ref _x, value);
        }

        [Category("Position")]
        [Description("Initial window Y position in pixels (windowed mode).")]
        public int Y
        {
            get => _y;
            set => SetField(ref _y, value);
        }

        [Category("Display")]
        [Description("Per-window VSync toggle. When false, the engine may still apply global VSync policy.")]
        public bool VSync
        {
            get => _vsync;
            set => SetField(ref _vsync, value);
        }

        [Category("Window")]
        [Description("When true, requests a transparent framebuffer (useful for compositing/overlays).")]
        public bool TransparentFramebuffer
        {
            get => _transparentFramebuffer;
            set => SetField(ref _transparentFramebuffer, value);
        }

        /// <summary>
        /// When false, the engine will suppress the platform's default window chrome so a custom title bar can be rendered.
        /// </summary>
        [Category("Window")]
        [Description("When false, suppresses platform window chrome so a custom title bar can be rendered.")]
        public bool UseNativeTitleBar
        {
            get => _useNativeTitleBar;
            set => SetField(ref _useNativeTitleBar, value);
        }

        /// <summary>
        /// Overrides the global HDR output toggle for this window when set. Null inherits the engine default.
        /// </summary>
        [Category("Display")]
        [Description("Overrides the global HDR output setting for this window. Null inherits engine default.")]
        public bool? OutputHDR
        {
            get => _outputHDR;
            set => SetField(ref _outputHDR, value);
        }
    }
}