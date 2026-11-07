using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Editor.Publishing;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A published archive must never hold a secret of a shipped settings asset. A secret is a member marked with
/// <see cref="SecretAttribute"/>. Its environment variable companion is a secret too. The tests cover the desktop config
/// archive and the browser startup settings.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class PublishedSettingsSecretTests
{
    private const string Sentinel = "published-secret-sentinel-";

    /// <summary>The settings types that the desktop config archive ships.</summary>
    private static readonly Type[] ShippedSettingsTypes = [typeof(GameStartupSettings), typeof(EditorPreferences), typeof(UserSettings)];

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedSecrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void KnownSecretMembers_CarryTheSecretAttribute()
    {
        string[] known =
        [
            "GameStartupSettings.MultiplayerSessionToken",
            "GameStartupSettings.MultiplayerAdmissionSecret",
            "GameStartupSettings.MultiplayerAccountId",
            "GameStartupSettings.MultiplayerReservationId",
            "GameStartupSettings.MultiplayerClientId",
            "EditorPreferences.McpServerAuthToken",
            "EditorPreferences.McpAssistantOpenAiApiKey",
            "EditorPreferences.McpAssistantAnthropicApiKey",
            "EditorPreferences.McpAssistantGeminiApiKey",
            "EditorPreferences.McpAssistantGitHubModelsToken",
        ];

        foreach (string name in known)
        {
            string[] parts = name.Split('.');
            Type type = ShippedSettingsTypes.Single(t => t.Name == parts[0]);
            PropertyInfo property = type.GetProperty(parts[1]).ShouldNotBeNull(name);
            property.GetCustomAttribute<SecretAttribute>().ShouldNotBeNull(name);
        }

        string[] marked = [.. ShippedSettingsTypes.SelectMany(type => PublishedSecrets.GetSecretMembers(type)
            .Select(member => $"{type.Name}.{member.Secret.Name}"))];
        marked.ShouldBe(known, ignoreOrder: true);

        PublishedSecrets.GetSecretMembers(typeof(EditorPreferences))
            .Count(member => member.EnvironmentVariable is not null).ShouldBe(5);
    }

    [Test]
    public void EveryPasswordMemberOfAShippedSettingsType_IsASecret()
    {
        foreach (Type type in ShippedSettingsTypes)
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (property.GetCustomAttribute<PasswordAttribute>() is not null)
                    property.GetCustomAttribute<SecretAttribute>().ShouldNotBeNull($"{type.Name}.{property.Name}");
    }

    [Test]
    public void DesktopConfigArchive_HoldsNoMarkedMember_AndTheLiveSettingsKeepTheirSecrets()
    {
        GameStartupSettings startup = FillSecrets(new GameStartupSettings { Name = "Startup" });
        EditorPreferences preferences = FillSecrets(new EditorPreferences { Name = "Preferences" });
        UserSettings user = FillSecrets(new UserSettings { Name = "User" });
        string staging = Path.Combine(_root, "Staging");
        Directory.CreateDirectory(staging);

        ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

        string[] files = Directory.GetFiles(staging);
        files.Length.ShouldBe(3);
        foreach (string file in files)
            ContainsSentinel(File.ReadAllBytes(file)).ShouldBeFalse(file);
        AssertSecretsCleared(LoadCooked<GameStartupSettings>(staging, ProjectBuilder.StartupAssetName));
        AssertSecretsCleared(LoadCooked<EditorPreferences>(staging, XRProject.EditorPreferencesFileName));
        AssertSecretsCleared(LoadCooked<UserSettings>(staging, XRProject.UserSettingsFileName));

        AssertSecretsKept(startup);
        AssertSecretsKept(preferences);
        AssertSecretsKept(user);
    }

    [Test]
    public void DesktopConfigArchive_HoldsNoEnvironmentVariableCompanion()
    {
        EditorPreferences preferences = new() { Name = "Preferences" };
        foreach (PublishedSecrets.SecretMember member in PublishedSecrets.GetSecretMembers(typeof(EditorPreferences)))
            member.EnvironmentVariable?.SetValue(preferences, Sentinel + member.Secret.Name);
        string staging = Path.Combine(_root, "Staging");
        Directory.CreateDirectory(staging);

        ProjectBuilder.WriteConfigArchiveAssets(staging, new GameStartupSettings(), preferences, new UserSettings(), aotMetadata: null);

        ContainsSentinel(File.ReadAllBytes(Path.Combine(staging, XRProject.EditorPreferencesFileName))).ShouldBeFalse();
        EditorPreferences loaded = LoadCooked<EditorPreferences>(staging, XRProject.EditorPreferencesFileName);
        foreach (PublishedSecrets.SecretMember member in PublishedSecrets.GetSecretMembers(typeof(EditorPreferences)))
            ((string?)member.EnvironmentVariable?.GetValue(loaded)).ShouldBeNullOrEmpty(member.Secret.Name);
    }

    [Test]
    public void BrowserStartupSettings_HoldNoMarkedMember()
    {
        GameStartupSettings copy = FillSecrets(new GameStartupSettings { Name = "Startup" });

        ProjectBuilder.ClearBrowserRealtimeCredentials(copy);

        AssertSecretsCleared(copy);
    }

    private static T FillSecrets<T>(T settings) where T : class
    {
        foreach (PublishedSecrets.SecretMember member in PublishedSecrets.GetSecretMembers(typeof(T)))
            member.Secret.SetValue(settings, Sentinel + member.Secret.Name);
        return settings;
    }

    private static void AssertSecretsCleared(object settings)
    {
        foreach (PublishedSecrets.SecretMember member in PublishedSecrets.GetSecretMembers(settings.GetType()))
            (member.Secret.GetValue(settings) as string).ShouldBeNullOrEmpty($"{settings.GetType().Name}.{member.Secret.Name}");
    }

    private static void AssertSecretsKept(object settings)
    {
        foreach (PublishedSecrets.SecretMember member in PublishedSecrets.GetSecretMembers(settings.GetType()))
            member.Secret.GetValue(settings).ShouldBe(Sentinel + member.Secret.Name, $"{settings.GetType().Name}.{member.Secret.Name}");
    }

    private static T LoadCooked<T>(string staging, string fileName) where T : class
        => CookedAssetReader.LoadAsset<T>(File.ReadAllBytes(Path.Combine(staging, fileName))).ShouldNotBeNull(fileName);

    /// <summary>Finds the sentinel in UTF-8 or UTF-16 text at either byte alignment.</summary>
    private static bool ContainsSentinel(byte[] bytes)
        => Encoding.Latin1.GetString(bytes).Contains(Sentinel, StringComparison.Ordinal)
        || Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).Contains(Sentinel, StringComparison.Ordinal)
        || bytes.Length > 1 && Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1).Contains(Sentinel, StringComparison.Ordinal);
}
