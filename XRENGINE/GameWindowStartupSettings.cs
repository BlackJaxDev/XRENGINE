using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine
{
    public class GameWindowStartupSettings : XRBase
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

        public ELocalPlayerIndexMask LocalPlayers
        {
            get => _localPlayers;
            set => SetField(ref _localPlayers, value);
        }
        public string? WindowTitle
        {
            get => _windowTitle;
            set => SetField(ref _windowTitle, value);
        }
        public int Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }
        public int Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }
        public XRWorld? TargetWorld
        {
            get => _targetWorld;
            set => SetField(ref _targetWorld, value);
        }
        public EWindowState WindowState
        {
            get => _windowState;
            set => SetField(ref _windowState, value);
        }
        public int X
        {
            get => _x;
            set => SetField(ref _x, value);
        }
        public int Y
        {
            get => _y;
            set => SetField(ref _y, value);
        }
        public bool VSync
        {
            get => _vsync;
            set => SetField(ref _vsync, value);
        }
        public bool TransparentFramebuffer
        {
            get => _transparentFramebuffer;
            set => SetField(ref _transparentFramebuffer, value);
        }

        /// <summary>
        /// Overrides the global HDR output toggle for this window when set. Null inherits the engine default.
        /// </summary>
        public bool? OutputHDR
        {
            get => _outputHDR;
            set => SetField(ref _outputHDR, value);
        }
    }
}