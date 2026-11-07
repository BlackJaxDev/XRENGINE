using System.Reflection;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Attributes;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.UnitTests.Cutscenes;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// A published config archive must never hold a secret of the editor preferences: the MCP token, the assistant API keys,
/// any member marked <see cref="PasswordAttribute"/>, or the names of the environment variables that hold them.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class PublishedEditorPreferencesSecretTests
{
    private const string SecretPrefix = "published-secret-sentinel-";
    private const string EnvironmentPrefix = "PUBLISHED_SECRET_SENTINEL_";

    [Test]
    public void ConfigArchive_HoldsNoSecretOfTheEditorPreferences_AndDoesNotChangeTheLivePreferences()
    {
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedSecrets", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(root, "Staging");
        Directory.CreateDirectory(staging);
        EditorPreferences preferences = new() { Name = "Preferences" };
        GameStartupSettings startup = new() { Name = "Startup" };
        UserSettings user = new() { Name = "User" };
        PropertyInfo[] secrets = GetSecretProperties();
        secrets.Length.ShouldBeGreaterThan(0);
        foreach (PropertyInfo secret in secrets)
            secret.SetValue(preferences, SecretPrefix + secret.Name);
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Path.Combine(root, "Assets")));

            ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

            byte[] cooked = File.ReadAllBytes(Path.Combine(staging, XRProject.EditorPreferencesFileName));
            ContainsText(cooked, SecretPrefix).ShouldBeFalse();
            EditorPreferences loaded = CookedAssetReader.LoadAsset<EditorPreferences>(cooked).ShouldNotBeNull();
            foreach (PropertyInfo secret in secrets)
                ((string?)secret.GetValue(loaded)).ShouldBeNullOrEmpty(secret.Name);
            foreach (PropertyInfo secret in secrets)
                secret.GetValue(preferences).ShouldBe(SecretPrefix + secret.Name);
        }
        finally
        {
            preferences.Destroy(now: true);
            startup.Destroy(now: true);
            user.Destroy(now: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ConfigArchive_HoldsNoSecretEnvironmentVariableName()
    {
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "PublishedSecrets", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(root, "Staging");
        Directory.CreateDirectory(staging);
        EditorPreferences preferences = new() { Name = "Preferences" };
        GameStartupSettings startup = new() { Name = "Startup" };
        UserSettings user = new() { Name = "User" };
        PropertyInfo[] names = GetSecretEnvironmentVariableProperties();
        names.Length.ShouldBe(GetSecretProperties().Length);
        foreach (PropertyInfo name in names)
            name.SetValue(preferences, EnvironmentPrefix + name.Name);
        try
        {
            using IDisposable services = AssetSerializationServices.Install(new FakeAssetRootServices(Path.Combine(root, "Assets")));

            ProjectBuilder.WriteConfigArchiveAssets(staging, startup, preferences, user, aotMetadata: null);

            byte[] cooked = File.ReadAllBytes(Path.Combine(staging, XRProject.EditorPreferencesFileName));
            ContainsText(cooked, EnvironmentPrefix).ShouldBeFalse();
        }
        finally
        {
            preferences.Destroy(now: true);
            startup.Destroy(now: true);
            user.Destroy(now: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Every public string member of the editor preferences that is marked as a password.</summary>
    private static PropertyInfo[] GetSecretProperties()
        => [.. typeof(EditorPreferences).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType == typeof(string) && property.GetCustomAttribute<PasswordAttribute>() is not null)];

    /// <summary>The environment-variable name member of each secret (<c>&lt;Secret&gt;EnvVar</c>).</summary>
    private static PropertyInfo[] GetSecretEnvironmentVariableProperties()
        => [.. GetSecretProperties()
            .Select(secret => typeof(EditorPreferences).GetProperty(secret.Name + "EnvVar", BindingFlags.Instance | BindingFlags.Public))
            .OfType<PropertyInfo>()];

    private static bool ContainsText(byte[] bytes, string text)
        => Encoding.Latin1.GetString(bytes).Contains(text, StringComparison.Ordinal)
        || Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).Contains(text, StringComparison.Ordinal)
        || bytes.Length > 1 && Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1).Contains(text, StringComparison.Ordinal);
}
