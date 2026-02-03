using System.Collections.Generic;
using System.Reflection;

namespace XREngine.Data.Core
{
    /// <summary>
    /// Helper that tracks overrideable settings and bubbles changes to a target owner.
    /// </summary>
    public sealed class OverrideableSettingsTracker(
        IXRNotifyPropertyChanged owner,
        Action<string, IOverrideableSetting, IXRPropertyChangedEventArgs> onOverrideableSettingChanged)
    {
        private readonly List<IOverrideableSetting> _trackedOverrideableSettings = [];
        private readonly Dictionary<IOverrideableSetting, string> _overrideableSettingPropertyMap = new();

        public IXRNotifyPropertyChanged Owner => owner;

        public void TrackOverrideableSettings(object target)
        {
            UntrackOverrideableSettings();

            var properties = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                if (!typeof(IOverrideableSetting).IsAssignableFrom(property.PropertyType))
                    continue;

                IOverrideableSetting? overrideable;
                try
                {
                    overrideable = property.GetValue(target) as IOverrideableSetting;
                }
                catch
                {
                    continue;
                }

                if (overrideable is null)
                    continue;

                AttachOverrideableSetting(overrideable, property.Name);
            }
        }

        public void HandleOwnerPropertyChanged(string? propName, object? previousValue, object? newValue)
        {
            if (string.IsNullOrWhiteSpace(propName))
                return;

            var prevSetting = previousValue as IOverrideableSetting;
            var newSetting = newValue as IOverrideableSetting;

            if (prevSetting is null && newSetting is null)
                return;

            if (ReferenceEquals(prevSetting, newSetting))
                return;

            if (prevSetting is not null)
                DetachOverrideableSetting(prevSetting);

            if (newSetting is not null)
                AttachOverrideableSetting(newSetting, propName);
        }

        public void UntrackOverrideableSettings()
        {
            for (int i = _trackedOverrideableSettings.Count - 1; i >= 0; i--)
                DetachOverrideableSetting(_trackedOverrideableSettings[i]);
        }

        private void AttachOverrideableSetting(IOverrideableSetting setting, string propertyName)
        {
            if (_overrideableSettingPropertyMap.ContainsKey(setting))
                return;

            _trackedOverrideableSettings.Add(setting);
            _overrideableSettingPropertyMap[setting] = propertyName;

            if (setting is IXRNotifyPropertyChanged notify)
                notify.PropertyChanged += HandleOverrideableSettingChanged;
        }

        private void DetachOverrideableSetting(IOverrideableSetting setting)
        {
            if (setting is IXRNotifyPropertyChanged notify)
                notify.PropertyChanged -= HandleOverrideableSettingChanged;

            _overrideableSettingPropertyMap.Remove(setting);
            _trackedOverrideableSettings.Remove(setting);
        }

        private void HandleOverrideableSettingChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is not IOverrideableSetting setting)
                return;

            if (_overrideableSettingPropertyMap.TryGetValue(setting, out var propertyName))
                onOverrideableSettingChanged(propertyName, setting, e);
        }
    }
}
