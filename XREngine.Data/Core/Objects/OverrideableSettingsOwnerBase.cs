namespace XREngine.Data.Core
{
    /// <summary>
    /// Base class that tracks overrideable settings and bubbles their changes through PropertyChanged.
    /// </summary>
    public abstract class OverrideableSettingsOwnerBase : XRBase, IOverrideableSettingsOwner
    {
        private readonly OverrideableSettingsTracker _overrideableSettingsTracker;

        protected OverrideableSettingsOwnerBase()
        {
            _overrideableSettingsTracker = new OverrideableSettingsTracker(this, OnOverrideableSettingChanged);
            TrackOverrideableSettings();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            _overrideableSettingsTracker.HandleOwnerPropertyChanged(propName, prev, field);
        }

        protected virtual void OnOverrideableSettingChanged(string propertyName, IOverrideableSetting setting, IXRPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(propertyName, setting, setting);
        }

        public void TrackOverrideableSettings()
            => _overrideableSettingsTracker.TrackOverrideableSettings(this);

        public void UntrackOverrideableSettings()
            => _overrideableSettingsTracker.UntrackOverrideableSettings();
    }
}
