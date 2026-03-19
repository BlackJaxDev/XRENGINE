using Newtonsoft.Json;
using XREngine.Audio;

namespace XREngine.Runtime.Bootstrap;

public static class UnitTestingWorldSettingsStore
{
    public const string SettingsFileName = "UnitTestingWorldSettings.jsonc";

    public static UnitTestingWorldSettings Load(bool writeBackAfterRead)
    {
        UnitTestingWorldSettings settings;

        string dir = Environment.CurrentDirectory;
        string filePath = Path.Combine(dir, "Assets", SettingsFileName);

        if (!File.Exists(filePath))
            File.WriteAllText(filePath, JsonConvert.SerializeObject(settings = new UnitTestingWorldSettings(), Formatting.Indented));
        else
        {
            string? content = File.ReadAllText(filePath);
            if (content is not null)
            {
                settings = JsonConvert.DeserializeObject<UnitTestingWorldSettings>(content) ?? new UnitTestingWorldSettings();
                if (writeBackAfterRead)
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            else
                settings = new UnitTestingWorldSettings();
        }

        RuntimeBootstrapState.Settings = settings;
        return settings;
    }

    public static void ApplyWorldKindOverride(UnitTestingWorldSettings settings)
    {
        string? worldKindEnv = Environment.GetEnvironmentVariable("XRE_UNIT_TEST_WORLD_KIND");
        if (string.IsNullOrWhiteSpace(worldKindEnv))
            return;

        if (Enum.TryParse<UnitTestWorldKind>(worldKindEnv, true, out var kind))
        {
            settings.WorldKind = kind;
            Debug.Out($"Unit test world kind overridden to {kind} via XRE_UNIT_TEST_WORLD_KIND.");
            return;
        }

        Debug.Out($"Invalid XRE_UNIT_TEST_WORLD_KIND value '{worldKindEnv}'. Using {settings.WorldKind}.");
    }

    public static void ApplyAudioOverrides(UnitTestingWorldSettings settings)
    {
        AudioSettings.AudioArchitectureV2 = settings.AudioArchitectureV2;
        Engine.Audio.DefaultTransport = settings.AudioTransport;
        Engine.Audio.DefaultEffects = settings.AudioEffects;

        Debug.Out($"Audio toggles applied: V2={AudioSettings.AudioArchitectureV2}, Transport={Engine.Audio.DefaultTransport}, Effects={Engine.Audio.DefaultEffects}");
    }
}