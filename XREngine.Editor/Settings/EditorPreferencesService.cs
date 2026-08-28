namespace XREngine.Editor.Settings;

/// <summary>
/// Editor-owned access point for persisted editor preferences, project overrides,
/// and session-only runtime environment state.
/// </summary>
/// <remarks>
/// The properties intentionally expose the Phase 6 compatibility identities until
/// <c>Engine.Settings</c> is migrated off the facade. New editor code should depend
/// on this service rather than reaching into the runtime facade directly.
/// </remarks>
public sealed class EditorPreferencesService
{
    /// <summary>Gets the process-wide editor preference service.</summary>
    public static EditorPreferencesService Current { get; } = new();

    /// <summary>Gets the global persisted editor preference asset.</summary>
    public global::XREngine.EditorPreferences GlobalPreferences
        => global::XREngine.Engine.GlobalEditorPreferences;

    /// <summary>Gets the active project or sandbox override asset.</summary>
    public global::XREngine.EditorPreferencesOverrides ProjectOverrides
        => global::XREngine.Engine.EditorPreferencesOverrides;

    /// <summary>Gets the effective global-plus-project editor preferences.</summary>
    public global::XREngine.EditorPreferences EffectivePreferences
        => global::XREngine.Engine.EditorPreferences;

    /// <summary>Gets the editor's session-only environment preference view.</summary>
    public global::XREngine.EditorRuntimeEnvironmentPreferences RuntimeEnvironment
        => EffectivePreferences.RuntimeEnvironment;

    /// <summary>Reloads launch-backed environment values and reapplies effective editor settings.</summary>
    public void RefreshRuntimeEnvironment()
    {
        global::XREngine.XREnvironment.RefreshFromProcess();
        EffectivePreferences.ApplyRuntimeSideEffects();
    }

    /// <summary>Persists the global editor preferences at their established location.</summary>
    public void SaveGlobalPreferences()
        => global::XREngine.Engine.SaveGlobalEditorPreferences();

    /// <summary>Persists project or sandbox editor overrides at their established location.</summary>
    public void SaveProjectOverrides()
        => global::XREngine.Engine.SaveProjectEditorPreferencesOverrides();
}
