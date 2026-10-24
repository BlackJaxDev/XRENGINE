using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Writes profiler preferences to the active override or global owner.</summary>
internal static class NativeProfilerSettingWriter
{
    public static void Set<T>(NativeProfilerSettingOwner owner, T value,
        Func<EditorDebugOptions, T> effectiveSelector,
        Func<EditorDebugOverrides, OverrideableSetting<T>> overrideSelector,
        Action<EditorDebugOptions, T> globalSetter)
    {
        if (!owner.IsCurrent)
            throw new InvalidOperationException("The profiler settings owner changed. Reopen the field.");
        if (EqualityComparer<T>.Default.Equals(effectiveSelector(owner.EffectiveDebug), value))
            return;
        if (!owner.IsCurrent)
            throw new InvalidOperationException("The profiler settings owner changed. Reopen the field.");
        OverrideableSetting<T> setting = overrideSelector(owner.OverrideDebug);
        if (setting.HasOverride)
        {
            setting.Value = value;
            return;
        }
        globalSetter(owner.GlobalDebug, value);
    }
}
