using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XREngine.Audio;

namespace XREngine.Runtime.Bootstrap;

public static class UnitTestingWorldSettingsStore
{
    public const string SettingsFileName = "UnitTestingWorldSettings.jsonc";
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = [new MeshSubmissionStrategyJsonConverter(), new ModelPostImportFlagsJsonConverter()]
    };

    public static UnitTestingWorldSettings Load(bool writeBackAfterRead)
    {
        UnitTestingWorldSettings settings;

        string dir = Environment.CurrentDirectory;
        string filePath = Path.Combine(dir, "Assets", SettingsFileName);

        if (!File.Exists(filePath))
        {
            settings = new UnitTestingWorldSettings();
            string serializedSettings = JsonConvert.SerializeObject(settings, JsonSettings);
            settings.TracksExplicitJsonProperties = true;
            settings.ExplicitJsonProperties = ReadTopLevelPropertyNames(serializedSettings);
            File.WriteAllText(filePath, serializedSettings);
        }
        else
        {
            string? content = File.ReadAllText(filePath);
            if (content is not null)
            {
                settings = ParseJsonc(content);
                if (writeBackAfterRead)
                    File.WriteAllText(filePath, JsonConvert.SerializeObject(settings, JsonSettings));
            }
            else
                settings = new UnitTestingWorldSettings();
        }

        RuntimeBootstrapState.Settings = settings;
        BootstrapRenderSettings.ApplyOpenGLShaderLinkSettings(settings);
        Debug.Out(
            $"[UnitTestingWorldSettings] Loaded '{filePath}' AllowSkinning={settings.AllowSkinning} AllowShaderPipelines={settings.AllowShaderPipelines} Models={settings.ModelsToImport?.Count ?? 0} " +
            $"OpenGLLink(strategy={settings.OpenGLShaderLinkStrategy}, cache={settings.AllowBinaryProgramCaching}, asyncBinaryUpload={settings.AsyncProgramBinaryUpload}, " +
            $"asyncSource={settings.AsyncProgramCompilation}, sharedWorkers={settings.OpenGLProgramCompileLinkWorkerCount}, maxAsyncPerFrame={settings.MaxAsyncShaderProgramsPerFrame}, " +
            $"compilerThreads={settings.OpenGLShaderCompilerThreadCount}, probe={settings.OpenGLParallelShaderCompileProbeEnabled}, probeTimeoutMs={settings.OpenGLParallelShaderCompileProbeTimeoutMs})");
        return settings;
    }

    public static UnitTestingWorldSettings ParseJsonc(string? content)
    {
        UnitTestingWorldSettings settings = string.IsNullOrWhiteSpace(content)
            ? new UnitTestingWorldSettings()
            : JsonConvert.DeserializeObject<UnitTestingWorldSettings>(content, JsonSettings) ?? new UnitTestingWorldSettings();

        settings.TracksExplicitJsonProperties = true;
        settings.ExplicitJsonProperties = ReadTopLevelPropertyNames(content);
        return settings;
    }

    public static bool ApplyUserSettingsOverrides(UserSettings userSettings, UnitTestingWorldSettings settings)
    {
        bool applied = false;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderAPI)))
        {
            userSettings.RenderLibrary = settings.RenderAPI;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.PhysicsAPI)))
        {
            userSettings.PhysicsLibrary = settings.PhysicsAPI;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VSyncOverride))
            && settings.VSyncOverride is EVSyncMode vSyncOverride)
        {
            userSettings.VSync = vSyncOverride;
            applied = true;
        }

        return applied;
    }

    public static void ApplyStartupOverrides(GameStartupSettings startupSettings, UnitTestingWorldSettings settings)
    {
        ApplyUserSettingsOverrides(startupSettings.DefaultUserSettings, settings);

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.GPURenderDispatch)))
            startupSettings.GPURenderDispatch = settings.GPURenderDispatch;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.UpdateFPS)))
            startupSettings.TargetUpdatesPerSecond = settings.UpdateFPS;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderFPS)))
            startupSettings.TargetFramesPerSecond = settings.RenderFPS;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VSyncOverride))
            && settings.VSyncOverride is EVSyncMode vSyncOverride)
            startupSettings.VSyncOverride = new(vSyncOverride, true);

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.FixedFPS)))
            startupSettings.FixedFramesPerSecond = settings.FixedFPS;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioArchitectureV2)))
            startupSettings.AudioArchitectureV2Override = new(settings.AudioArchitectureV2, true);

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioTransport)))
            startupSettings.AudioTransportOverride = new(settings.AudioTransport, true);

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioEffects)))
            startupSettings.AudioEffectsOverride = new(settings.AudioEffects, true);
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
        bool applied = false;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioArchitectureV2)))
        {
            AudioSettings.AudioArchitectureV2 = settings.AudioArchitectureV2;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioTransport)))
        {
            Engine.Audio.DefaultTransport = settings.AudioTransport;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AudioEffects)))
        {
            Engine.Audio.DefaultEffects = settings.AudioEffects;
            applied = true;
        }

        if (applied)
            Debug.Out($"Audio toggles applied: V2={AudioSettings.AudioArchitectureV2}, Transport={Engine.Audio.DefaultTransport}, Effects={Engine.Audio.DefaultEffects}");
        else
            Debug.Out("Audio toggles skipped; no audio properties were specified in UnitTestingWorldSettings.jsonc.");
    }

    private static HashSet<string> ReadTopLevelPropertyNames(string? content)
    {
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return propertyNames;

        using var textReader = new StringReader(content);
        using var jsonReader = new JsonTextReader(textReader)
        {
            DateParseHandling = DateParseHandling.None,
        };

        if (JToken.ReadFrom(jsonReader) is not JObject root)
            return propertyNames;

        foreach (JProperty property in root.Properties())
            propertyNames.Add(property.Name);

        return propertyNames;
    }
}
