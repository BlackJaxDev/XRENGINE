using NUnit.Framework;
using System.Globalization;
using Shouldly;
using XREngine.Editor;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Editor;

[TestFixture]
[NonParallelizable]
public sealed class NativeProfilerNumericSettingBindingTests
{
    [Test]
    public void NumericBindingCommitsToTheExistingPreferenceAndNotifiesItsOwner()
    {
        var global = Engine.GlobalEditorPreferences.Debug;
        var setting = Engine.EditorPreferencesOverrides.Debug.CodeProfilerSnapshotIntervalMsOverride;
        bool hasOverride = setting.HasOverride;
        int original = hasOverride ? setting.Value : global.CodeProfilerSnapshotIntervalMs;
        try
        {
            NativeProfilerNumericSettingBinding binding = new(
                ENativeProfilerSetting.SnapshotInterval, new NativeProfilerSettingOwner());
            int notifications = 0;
            binding.ValueChanged += _ => notifications++;
            binding.SetValue("47");
            Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs.ShouldBe(47);
            if (hasOverride) setting.Value.ShouldBe(47);
            else global.CodeProfilerSnapshotIntervalMs.ShouldBe(47);
            binding.Value.ShouldBe("47");
            notifications.ShouldBe(1);
            binding.SetValue("6000");
            Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs.ShouldBe(5000);
            binding.Value.ShouldBe("5000");
        }
        finally
        {
            if (hasOverride) setting.Value = original;
            else global.CodeProfilerSnapshotIntervalMs = original;
        }
    }

    [Test]
    public void RetiredProjectOwnerCannotReceiveAnOldNumericDraft()
    {
        var originalOverrides = Engine.EditorPreferencesOverrides;
        NativeProfilerNumericSettingBinding binding = new(
            ENativeProfilerSetting.SnapshotInterval, new NativeProfilerSettingOwner());
        try
        {
            Engine.EditorPreferencesOverrides = new EditorPreferencesOverrides();
            int replacementValue = Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs;
            binding.IsCurrent.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => binding.SetValue("127"));
            Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs.ShouldBe(replacementValue);
        }
        finally { Engine.EditorPreferencesOverrides = originalOverrides; }
    }

    [Test]
    public void ReplacedOverrideLeafCannotReceiveAnOldNumericDraft()
    {
        var overrides = Engine.EditorPreferencesOverrides.Debug;
        OverrideableSetting<int> oldSetting = overrides.CodeProfilerSnapshotIntervalMsOverride;
        NativeProfilerNumericSettingBinding binding = new(
            ENativeProfilerSetting.SnapshotInterval, new NativeProfilerSettingOwner());
        try
        {
            overrides.CodeProfilerSnapshotIntervalMsOverride = new OverrideableSetting<int>();
            binding.IsCurrent.ShouldBeFalse();
            int current = Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs;
            Should.Throw<InvalidOperationException>(() => binding.SetValue("333"));
            Engine.EditorPreferences.Debug.CodeProfilerSnapshotIntervalMs.ShouldBe(current);
        }
        finally { overrides.CodeProfilerSnapshotIntervalMsOverride = oldSetting; }
    }

    [Test]
    public void UpdateIntervalCommitsThroughTheExistingOwner()
    {
        var global = Engine.GlobalEditorPreferences.Debug;
        var setting = Engine.EditorPreferencesOverrides.Debug.ProfilerPanelUpdateIntervalSecondsOverride;
        bool hasOverride = setting.HasOverride;
        float original = hasOverride ? setting.Value : global.ProfilerPanelUpdateIntervalSeconds;
        try
        {
            NativeProfilerNumericSettingBinding binding = new(
                ENativeProfilerSetting.UpdateInterval, new NativeProfilerSettingOwner());
            binding.SetValue(0.125f.ToString(CultureInfo.CurrentCulture));
            Engine.EditorPreferences.Debug.ProfilerPanelUpdateIntervalSeconds.ShouldBe(0.125f);
            if (hasOverride) setting.Value.ShouldBe(0.125f);
            else global.ProfilerPanelUpdateIntervalSeconds.ShouldBe(0.125f);
            Should.Throw<FormatException>(() => binding.SetValue("NaN"));
        }
        finally
        {
            if (hasOverride) setting.Value = original;
            else global.ProfilerPanelUpdateIntervalSeconds = original;
        }
    }

    [Test]
    public void ReplacedUdpOwnerRejectsTheOldToggle()
    {
        var overrides = Engine.EditorPreferencesOverrides.Debug;
        OverrideableSetting<bool> original = overrides.EnableProfilerUdpSendingOverride;
        NativeProfilerSettingOwner owner = new();
        try
        {
            overrides.EnableProfilerUdpSendingOverride = new OverrideableSetting<bool>();
            bool current = Engine.EditorPreferences.Debug.EnableProfilerUdpSending;
            owner.IsCurrent.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => NativeProfilerSettingWriter.Set(owner, !current,
                static debug => debug.EnableProfilerUdpSending,
                static leaf => leaf.EnableProfilerUdpSendingOverride,
                static (global, value) => global.EnableProfilerUdpSending = value));
            Engine.EditorPreferences.Debug.EnableProfilerUdpSending.ShouldBe(current);
        }
        finally { overrides.EnableProfilerUdpSendingOverride = original; }
    }
}
