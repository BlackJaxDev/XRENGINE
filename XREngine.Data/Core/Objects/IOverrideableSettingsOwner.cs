namespace XREngine.Data.Core
{
    /// <summary>
    /// Common contract for objects that track overrideable settings.
    /// </summary>
    public interface IOverrideableSettingsOwner
    {
        void TrackOverrideableSettings();
        void UntrackOverrideableSettings();
    }
}
