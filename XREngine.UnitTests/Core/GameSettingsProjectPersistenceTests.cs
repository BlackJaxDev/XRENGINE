using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class GameSettingsProjectPersistenceTests
{
    [Test]
    public void SaveProjectGameSettings_ReloadProject_PersistsValues()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GameSettingsProject", Guid.NewGuid().ToString("N"));
        string projectFolder = Path.Combine(tempRoot, "SampleProjectRoot");
        Directory.CreateDirectory(projectFolder);

        bool previousMonitorSetting = Engine.Assets.MonitorGameAssetsForChanges;
        string? previousGameAssetsPath = Engine.Assets.GameAssetsPath;
        string? previousMetadataPath = Engine.Assets.GameMetadataPath;
        string? previousPackagesPath = Engine.Assets.PackagesPath;
        string? previousLibrariesPath = Engine.Assets.LibrariesPath;
        string? previousCachePath = Engine.Assets.GameCachePath;
        var previousGameSettings = Engine.GameSettings;
        var previousUserSettings = Engine.UserSettings;
        var previousGlobalEditorPreferences = Engine.GlobalEditorPreferences;
        var previousEditorPreferencesOverrides = Engine.EditorPreferencesOverrides;

        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;

            XRProject project = XRProject.CreateNew(projectFolder, "SampleProject");
            Engine.LoadProject(project).ShouldBeTrue();

            Engine.GameSettings.ServerIP = "10.20.30.40";
            Engine.GameSettings.TargetFramesPerSecond = 144.0f;
            Engine.GameSettings.CalculateSkinningInComputeShaderOverride = new OverrideableSetting<bool>(false, true);
            Engine.GameSettings.UseDetailPreservingComputeMipmapsOverride = new OverrideableSetting<bool>(true, true);

            Engine.SaveProjectGameSettings();

            File.Exists(project.GameSettingsPath).ShouldBeTrue();

            Engine.GameSettings.ServerIP = "127.0.0.1";
            Engine.GameSettings.TargetFramesPerSecond = 60.0f;
            Engine.GameSettings.CalculateSkinningInComputeShaderOverride = new OverrideableSetting<bool>(true, false);
            Engine.GameSettings.UseDetailPreservingComputeMipmapsOverride = new OverrideableSetting<bool>(false, false);

            Engine.LoadProject(project).ShouldBeTrue();

            Engine.GameSettings.ServerIP.ShouldBe("10.20.30.40");
            Engine.GameSettings.TargetFramesPerSecond.ShouldBe(144.0f);
            Engine.GameSettings.CalculateSkinningInComputeShaderOverride.HasOverride.ShouldBeTrue();
            Engine.GameSettings.CalculateSkinningInComputeShaderOverride.Value.ShouldBeFalse();
            Engine.GameSettings.UseDetailPreservingComputeMipmapsOverride.HasOverride.ShouldBeTrue();
            Engine.GameSettings.UseDetailPreservingComputeMipmapsOverride.Value.ShouldBeTrue();
        }
        finally
        {
            Engine.UnloadProject();
            Engine.GameSettings = previousGameSettings;
            Engine.UserSettings = previousUserSettings;
            Engine.GlobalEditorPreferences = previousGlobalEditorPreferences;
            Engine.EditorPreferencesOverrides = previousEditorPreferencesOverrides;
            Engine.Assets.GameAssetsPath = previousGameAssetsPath;
            Engine.Assets.GameMetadataPath = previousMetadataPath;
            Engine.Assets.PackagesPath = previousPackagesPath;
            Engine.Assets.LibrariesPath = previousLibrariesPath;
            Engine.Assets.GameCachePath = previousCachePath;
            Engine.Assets.MonitorGameAssetsForChanges = previousMonitorSetting;

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Best-effort cleanup for temp project assets.
            }
        }
    }
}