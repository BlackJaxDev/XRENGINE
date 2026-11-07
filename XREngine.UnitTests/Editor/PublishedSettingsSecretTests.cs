using System.Reflection;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Editor.Publishing;
using XREngine.UnitTests.Cutscenes;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A published archive must never hold a secret of a shipped settings asset. A secret is a member marked with
/// <see cref="SecretAttribute"/>; its environment variable companion is a secret too. The tests cover the desktop config
/// archive, the content cook (full and incremental), and the browser startup settings.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class PublishedSettingsSecretTests
{
    private const string Sentinel = "published-secret-sentinel-";

    /// <summary>The settings types that a publish ships.</summary>
    private static readonly Type[] ShippedSettingsTypes = [typeof(GameStartupSettings), typeof(EditorPreferences), typeof(UserSettings)];

    private string _root = null!;
    private string _assets = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedSecrets", Guid.NewGuid().ToString("N"));
        _assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(_assets);
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
        GameStartupSettings startup = Fill(new GameStartupSettings { Name = "Startup" });
        EditorPreferences preferences = Fill(new EditorPreferences { Name = "Preferences" });
        UserSettings user = Fill(new UserSettings { Name = "User" });
        string staging = Path.Combine(_root, "Staging");
        Directory.CreateDirectory(staging);
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));

            ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

            foreach (string file in Directory.GetFiles(staging))
                ContainsSentinel(File.ReadAllBytes(file)).ShouldBeFalse(file);
            EditorPreferences loaded = CookedAssetReader.LoadAsset<EditorPreferences>(
                File.ReadAllBytes(Path.Combine(staging, XRProject.EditorPreferencesFileName))).ShouldNotBeNull();
            foreach ((PropertyInfo secret, _) in PublishedSecrets.GetSecretMembers(typeof(EditorPreferences)))
                ((string?)secret.GetValue(loaded)).ShouldBeNullOrEmpty(secret.Name);
            AssertStillFilled(startup);
            AssertStillFilled(preferences);
        }
        finally
        {
            startup.Destroy(now: true);
            preferences.Destroy(now: true);
            user.Destroy(now: true);
        }
    }

    [Test]
    public void DesktopConfigArchive_HoldsNoEnvironmentVariableCompanion()
    {
        GameStartupSettings startup = new() { Name = "Startup" };
        EditorPreferences preferences = Fill(new EditorPreferences { Name = "Preferences" }, companions: true);
        UserSettings user = new() { Name = "User" };
        PublishedSecrets.GetSecretMembers(typeof(EditorPreferences)).Count(member => member.EnvironmentVariable is not null).ShouldBe(5);
        string staging = Path.Combine(_root, "Staging");
        Directory.CreateDirectory(staging);
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));

            ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

            ContainsSentinel(File.ReadAllBytes(Path.Combine(staging, XRProject.EditorPreferencesFileName))).ShouldBeFalse();
        }
        finally
        {
            startup.Destroy(now: true);
            preferences.Destroy(now: true);
            user.Destroy(now: true);
        }
    }

    [Test]
    public void ContentCook_FullAndIncremental_HoldNoMarkedMember()
    {
        GameStartupSettings startup = Fill(new GameStartupSettings { Name = "Startup" });
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(_assets));
            string source = Path.Combine(_assets, "Data", "Startup.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            string yaml = AssetManager.Serializer.Serialize(startup);
            if (!yaml.Contains("__assetType:", StringComparison.Ordinal))
                yaml = $"__assetType: {typeof(GameStartupSettings).FullName}{Environment.NewLine}{yaml}";
            File.WriteAllText(source, yaml);
            yaml.ShouldContain(Sentinel);

            string cooked = ProjectBuilder.PrepareCookedContentDirectoryForTests(_assets, Path.Combine(_root, "Intermediate"));
            ContainsSentinel(File.ReadAllBytes(Path.Combine(cooked, "Data", "Startup.asset"))).ShouldBeFalse();

            string destination = Path.Combine(_root, "Delta", "Data", "Startup.asset");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ProjectBuilder.TransformIncrementalContentFile(source, destination);
            ContainsSentinel(File.ReadAllBytes(destination)).ShouldBeFalse();
        }
        finally
        {
            startup.Destroy(now: true);
        }
    }

    [Test]
    public void BrowserStartupSettings_HoldNoMarkedMember()
    {
        GameStartupSettings startup = Fill(new GameStartupSettings { Name = "Startup" });
        try
        {
            ProjectBuilder.ClearBrowserRealtimeCredentials(startup);

            foreach ((PropertyInfo secret, _) in PublishedSecrets.GetSecretMembers(typeof(GameStartupSettings)))
                secret.GetValue(startup).ShouldBeNull(secret.Name);
            PublishedCookedAssetRegistry.TrySerialize(startup, out byte[] payload).ShouldBeTrue();
            ContainsSentinel(payload).ShouldBeFalse();
        }
        finally
        {
            startup.Destroy(now: true);
        }
    }

    /// <summary>
    /// Sets a sentinel value in every secret member, or in every environment variable companion. The two are filled apart:
    /// a set companion makes a secret getter read the process environment, which would hide the stored secret.
    /// </summary>
    private static T Fill<T>(T settings, bool companions = false) where T : XRAsset
    {
        foreach ((PropertyInfo secret, PropertyInfo? environmentVariable) in PublishedSecrets.GetSecretMembers(typeof(T)))
        {
            if (!companions)
                secret.SetValue(settings, Sentinel + secret.Name);
            else
                environmentVariable?.SetValue(settings, Sentinel + environmentVariable.Name);
        }
        return settings;
    }

    private static void AssertStillFilled<T>(T settings) where T : XRAsset
    {
        foreach ((PropertyInfo secret, _) in PublishedSecrets.GetSecretMembers(typeof(T)))
            secret.GetValue(settings).ShouldBe(Sentinel + secret.Name);
    }

    private static bool ContainsSentinel(byte[] bytes)
        => Encoding.Latin1.GetString(bytes).Contains(Sentinel, StringComparison.Ordinal)
        || Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).Contains(Sentinel, StringComparison.Ordinal)
        || bytes.Length > 1 && Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1).Contains(Sentinel, StringComparison.Ordinal);
}
