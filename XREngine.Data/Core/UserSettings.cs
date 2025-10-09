using XREngine.Data.Core;
using XREngine.Data.Vectors;

namespace XREngine
{
    [Serializable]
    public class UserSettings : XRBase
    {
        private EWindowState _windowState = EWindowState.Windowed;
        private EVSyncMode _vSyncMode = EVSyncMode.Adaptive;
        private EEngineQuality _textureQuality = EEngineQuality.Highest;
        private EEngineQuality _modelQuality = EEngineQuality.Highest;
        private EEngineQuality _soundQuality = EEngineQuality.Highest;

        //Preferred libraries - will use whichever is available if the preferred one is not.
        private ERenderLibrary _renderLibrary = ERenderLibrary.OpenGL;
        private EAudioLibrary _audioLibrary = EAudioLibrary.OpenAL;
        private EPhysicsLibrary _physicsLibrary = EPhysicsLibrary.PhysX;

        private float? _targetFramesPerSecond = 90.0f;
        private IVector2 _windowedResolution = new(1920, 1080);
        private double _debugOutputRecencySeconds = 0.0;
        private bool _disableAudioOnDefocus = false;
        private float? _unfocusedTargetFramesPerSecond = null;

        public EVSyncMode VSync
        {
            get => _vSyncMode;
            set => SetField(ref _vSyncMode, value);
        }
        public EEngineQuality TextureQuality
        {
            get => _textureQuality;
            set => SetField(ref _textureQuality, value);
        }
        public EEngineQuality ModelQuality
        {
            get => _modelQuality;
            set => SetField(ref _modelQuality, value);
        }
        public EEngineQuality SoundQuality
        {
            get => _soundQuality;
            set => SetField(ref _soundQuality, value);
        }
        public ERenderLibrary RenderLibrary
        {
            get => _renderLibrary;
            set => SetField(ref _renderLibrary, value);
        }
        public EAudioLibrary AudioLibrary
        {
            get => _audioLibrary;
            set => SetField(ref _audioLibrary, value);
        }
        public EPhysicsLibrary PhysicsLibrary
        {
            get => _physicsLibrary;
            set => SetField(ref _physicsLibrary, value);
        }
        public float? TargetFramesPerSecond
        {
            get => _targetFramesPerSecond;
            set => SetField(ref _targetFramesPerSecond, value);
        }
        public double DebugOutputRecencySeconds
        {
            get => _debugOutputRecencySeconds;
            set => SetField(ref _debugOutputRecencySeconds, value);
        }
        public bool DisableAudioOnDefocus
        {
            get => _disableAudioOnDefocus;
            set => SetField(ref _disableAudioOnDefocus, value);
        }
        public float AudioDisableFadeSeconds { get; set; } = 0.5f; // Default fade out time for audio when defocusing
        public float? UnfocusedTargetFramesPerSecond
        {
            get => _unfocusedTargetFramesPerSecond;
            set => SetField(ref _unfocusedTargetFramesPerSecond, value);
        }
        public bool GPURenderDispatch { get; set; } = true;
    }
}
