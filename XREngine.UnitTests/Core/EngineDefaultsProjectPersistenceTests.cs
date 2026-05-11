using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using XREngine.Core.Files;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class EngineDefaultsProjectPersistenceTests
{
    [Test]
    public void SaveProjectEngineDefaults_ReloadProject_PersistsActiveEngineDefaults()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "EngineDefaultsProject", Guid.NewGuid().ToString("N"));
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
        var previousGlobalEngineDefaults = Engine.Rendering.GlobalDefaultSettings;
        var previousProjectEngineDefaults = Engine.Rendering.ProjectDefaultSettings;
        var previousActiveEngineDefaults = Engine.Rendering.Settings;

        try
        {
            Engine.Assets.MonitorGameAssetsForChanges = false;

            XRProject project = XRProject.CreateNew(projectFolder, "SampleProject");
            Engine.LoadProject(project).ShouldBeTrue();

            Engine.Rendering.ProjectDefaultSettings.ShouldNotBeNull();
            Engine.Rendering.Settings.ShouldBeSameAs(Engine.Rendering.ProjectDefaultSettings);

            Engine.Rendering.Settings.EnableFrameLogging = false;
            Engine.Rendering.Settings.JobWorkers = 7;
            Engine.Rendering.Settings.DefaultFontFolder = "ProjectFonts";

            Engine.SaveProjectEngineDefaults();

            File.Exists(project.EngineDefaultsPath).ShouldBeTrue();

            Engine.Rendering.Settings.EnableFrameLogging = true;
            Engine.Rendering.Settings.JobWorkers = null;
            Engine.Rendering.Settings.DefaultFontFolder = "TransientFonts";

            Engine.LoadProject(project).ShouldBeTrue();

            Engine.Rendering.Settings.ShouldBeSameAs(Engine.Rendering.ProjectDefaultSettings);
            Engine.Rendering.Settings.ShouldNotBeSameAs(Engine.Rendering.GlobalDefaultSettings);
            Engine.Rendering.Settings.EnableFrameLogging.ShouldBeFalse();
            Engine.Rendering.Settings.JobWorkers.ShouldBe(7);
            Engine.Rendering.Settings.DefaultFontFolder.ShouldBe("ProjectFonts");
        }
        finally
        {
            Engine.UnloadProject();
            Engine.GameSettings = previousGameSettings;
            Engine.UserSettings = previousUserSettings;
            Engine.GlobalEditorPreferences = previousGlobalEditorPreferences;
            Engine.EditorPreferencesOverrides = previousEditorPreferencesOverrides;
            Engine.Rendering.GlobalDefaultSettings = previousGlobalEngineDefaults;
            Engine.Rendering.ProjectDefaultSettings = previousProjectEngineDefaults;
            if (!ReferenceEquals(previousActiveEngineDefaults, previousGlobalEngineDefaults) &&
                !ReferenceEquals(previousActiveEngineDefaults, previousProjectEngineDefaults))
            {
                Engine.Rendering.Settings = previousActiveEngineDefaults;
            }
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
