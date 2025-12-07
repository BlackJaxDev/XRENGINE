using MemoryPack;
using XREngine.Core.Files;

namespace XREngine
{
    /// <summary>
    /// User settings that are specific to a project and saved in the project directory.
    /// This class wraps UserSettings as an XRAsset so it can be serialized to the project directory.
    /// </summary>
    [MemoryPackable]
    public partial class ProjectUserSettings : XRAsset
    {
        private UserSettings _settings = new();

        [MemoryPackConstructor]
        public ProjectUserSettings() { }

        public ProjectUserSettings(UserSettings settings)
        {
            _settings = settings ?? new UserSettings();
        }

        /// <summary>
        /// The wrapped user settings.
        /// </summary>
        public UserSettings Settings
        {
            get => _settings;
            set => SetField(ref _settings, value ?? new UserSettings());
        }
    }
}
