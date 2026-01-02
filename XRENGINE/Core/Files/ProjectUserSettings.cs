using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data.Core;

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
        public ProjectUserSettings()
        {
            AttachSettings(_settings);
        }

        public ProjectUserSettings(UserSettings settings)
        {
            _settings = settings ?? new UserSettings();
            AttachSettings(_settings);
        }

        private void AttachSettings(UserSettings settings)
        {
            if (settings is null)
                return;

            settings.PropertyChanged -= HandleSettingsChanged;
            settings.PropertyChanged += HandleSettingsChanged;
        }

        private void DetachSettings(UserSettings settings)
        {
            if (settings is null)
                return;

            settings.PropertyChanged -= HandleSettingsChanged;
        }

        private void HandleSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!IsDirty)
                MarkDirty();
        }

        /// <summary>
        /// The wrapped user settings.
        /// </summary>
        public UserSettings Settings
        {
            get => _settings;
            set
            {
                var next = value ?? new UserSettings();
                if (ReferenceEquals(_settings, next))
                    return;

                DetachSettings(_settings);
                SetField(ref _settings, next);
                AttachSettings(_settings);
                MarkDirty();
            }
        }
    }
}
