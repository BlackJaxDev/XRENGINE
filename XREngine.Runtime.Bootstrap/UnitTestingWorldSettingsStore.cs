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
            NormalizeRenderSettings(settings);
            NormalizeVrSettings(settings);
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
        UnitTestingOpenGLShaderLinkingSettings linkSettings = ResolveOpenGLShaderLinkingSettings(settings);
        Debug.Out(
            $"[UnitTestingWorldSettings] Loaded '{filePath}' AllowSkinning={settings.AllowSkinning} AllowShaderPipelines={settings.AllowShaderPipelines} Models={settings.ModelsToImport?.Count ?? 0} " +
            $"RenderBackend={ResolveRenderBackend(settings)} FallbackPolicy={ResolveBackendFallbackPolicy(settings)} " +
            $"OpenGLLink(strategy={linkSettings.Strategy}, cache={linkSettings.AllowBinaryProgramCaching}, asyncBinaryUpload={linkSettings.AsyncProgramBinaryUpload}, " +
            $"asyncSource={linkSettings.AsyncProgramCompilation}, sharedWorkers={linkSettings.ProgramCompileLinkWorkerCount}, maxAsyncPerFrame={linkSettings.MaxAsyncShaderProgramsPerFrame}, " +
            $"compilerThreads={linkSettings.DriverCompilerThreadCount}, probe={linkSettings.DriverParallelProbeEnabled}, probeTimeoutMs={linkSettings.DriverParallelProbeTimeoutMs})");
        return settings;
    }

    public static UnitTestingWorldSettings ParseJsonc(string? content)
    {
        UnitTestingWorldSettings settings = string.IsNullOrWhiteSpace(content)
            ? new UnitTestingWorldSettings()
            : JsonConvert.DeserializeObject<UnitTestingWorldSettings>(content, JsonSettings) ?? new UnitTestingWorldSettings();

        settings.TracksExplicitJsonProperties = true;
        settings.ExplicitJsonProperties = ReadTopLevelPropertyNames(content);
        NormalizeRenderSettings(settings);
        NormalizeVrSettings(settings);
        return settings;
    }

    public static void NormalizeRenderSettings(UnitTestingWorldSettings settings)
    {
        if (!settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering)) &&
            settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderAPI)))
        {
            settings.Rendering.RenderBackend = settings.RenderAPI;
        }
    }

    public static bool ApplyUserSettingsOverrides(UserSettings userSettings, UnitTestingWorldSettings settings)
    {
        bool applied = false;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering)))
        {
            userSettings.PreferredRenderBackend = settings.Rendering.RenderBackend;
            userSettings.RenderBackendFallbackPolicyOverride = new(settings.Rendering.BackendFallbackPolicy, true);
            applied = true;
        }
        else if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderAPI)))
        {
            userSettings.PreferredRenderBackend = settings.RenderAPI;
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

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering)))
        {
            startupSettings.RenderBackendFallbackPolicyOverride = new(settings.Rendering.BackendFallbackPolicy, true);
            startupSettings.VulkanRenderTargetModeOverride = new(settings.Rendering.Vulkan.RenderTargetMode, true);
        }

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

    private static ERenderLibrary ResolveRenderBackend(UnitTestingWorldSettings settings)
        => settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering))
            ? settings.Rendering.RenderBackend
            : settings.RenderAPI;

    private static RenderBackendFallbackPolicy ResolveBackendFallbackPolicy(UnitTestingWorldSettings settings)
        => settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering))
            ? settings.Rendering.BackendFallbackPolicy
            : RenderBackendFallbackPolicy.RequireRequested;

    private static UnitTestingOpenGLShaderLinkingSettings ResolveOpenGLShaderLinkingSettings(UnitTestingWorldSettings settings)
        => settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering))
            ? settings.Rendering.OpenGL.ShaderLinking
            : new UnitTestingOpenGLShaderLinkingSettings
            {
                Strategy = settings.OpenGLShaderLinkStrategy,
                AllowBinaryProgramCaching = settings.AllowBinaryProgramCaching,
                AsyncProgramBinaryUpload = settings.AsyncProgramBinaryUpload,
                AsyncProgramCompilation = settings.AsyncProgramCompilation,
                ProgramCompileLinkWorkerCount = settings.OpenGLProgramCompileLinkWorkerCount,
                MaxAsyncShaderProgramsPerFrame = settings.MaxAsyncShaderProgramsPerFrame,
                DriverCompilerThreadCount = settings.OpenGLShaderCompilerThreadCount,
                DriverParallelProbeEnabled = settings.OpenGLParallelShaderCompileProbeEnabled,
                DriverParallelProbeTimeoutMs = settings.OpenGLParallelShaderCompileProbeTimeoutMs,
            };

    public static void ApplyWorldKindOverride(UnitTestingWorldSettings settings)
    {
        string? worldKindEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestWorldKind);
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

    public static void ApplyOpenXrLaneOverrides(UnitTestingWorldSettings settings)
        => ApplyVrLaunchOverrides(settings);

    public static void ApplyVrLaunchOverrides(UnitTestingWorldSettings settings)
    {
        bool applied = false;
        bool legacyVrOverrideApplied = false;
        bool vrModeOverrideApplied = false;

        if (TryGetEnumEnv(XREngineEnvironmentVariables.UnitTestVrMode, out UnitTestingVrLaunchMode vrMode))
        {
            settings.VR.Mode = vrMode;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            applied = true;
            vrModeOverrideApplied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestVrPawn, out bool vrPawn))
        {
            settings.VRPawn = vrPawn;
            applied = true;
            legacyVrOverrideApplied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestUseOpenXr, out bool useOpenXr))
        {
            settings.UseOpenXR = useOpenXr;
            applied = true;
            legacyVrOverrideApplied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestSceneOnlyVrPawn, out bool sceneOnlyVrPawn))
        {
            settings.SceneOnlyVRPawn = sceneOnlyVrPawn;
            applied = true;
            legacyVrOverrideApplied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestPreviewVrStereoViews, out bool previewVrStereoViews))
        {
            settings.VR.PreviewStereoViews = previewVrStereoViews;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            applied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestAllowDesktopEditingInVr, out bool allowDesktopEditing))
        {
            settings.VR.AllowDesktopEditing = allowDesktopEditing;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            applied = true;
        }

        string? runtimeJsonEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson);
        if (!string.IsNullOrWhiteSpace(runtimeJsonEnv))
        {
            settings.VR.OpenXrRuntimeJson = runtimeJsonEnv;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            applied = true;
        }

        if (legacyVrOverrideApplied && !vrModeOverrideApplied)
        {
            settings.VR = CreateVrSettingsFromLegacyFields(settings);
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
        }

        string? renderApiEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestRenderApi);
        if (!string.IsNullOrWhiteSpace(renderApiEnv))
        {
            if (Enum.TryParse(renderApiEnv, true, out ERenderLibrary renderApi))
            {
                settings.RenderAPI = renderApi;
                settings.Rendering.RenderBackend = renderApi;
                MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.RenderAPI));
                MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.Rendering));
                applied = true;
            }
            else
            {
                Debug.Out($"Invalid XRE_UNIT_TEST_RENDER_API value '{renderApiEnv}'. Using {ResolveRenderBackend(settings)}.");
            }
        }

        if (applied)
        {
            NormalizeVrSettings(settings);
            Debug.Out(
                "[UnitTestingWorldSettings] Applied VR launch env overrides: " +
                $"VR.Mode={settings.VR.Mode}, VRPawn={settings.VRPawn}, UseOpenXR={settings.UseOpenXR}, " +
                $"SceneOnlyVRPawn={settings.SceneOnlyVRPawn}, PreviewVRStereoViews={settings.PreviewVRStereoViews}, " +
                $"RenderBackend={ResolveRenderBackend(settings)}.");
        }
    }

    public static void NormalizeVrSettings(UnitTestingWorldSettings settings)
    {
        if (!settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VR)) && HasExplicitLegacyVrSettings(settings))
            settings.VR = CreateVrSettingsFromLegacyFields(settings);

        ApplyVrModeToFlatFields(settings);
        ApplyOpenXrRuntimeJson(settings);
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

    private static bool TryGetBoolEnv(string name, out bool value)
    {
        value = default;
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        Debug.Out($"Invalid {name} value '{raw}'. Expected true/false or 1/0.");
        return false;
    }

    private static bool TryGetEnumEnv<TEnum>(string name, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (Enum.TryParse(raw, true, out value))
            return true;

        Debug.Out($"Invalid {name} value '{raw}'. Expected one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return false;
    }

    private static bool HasExplicitLegacyVrSettings(UnitTestingWorldSettings settings)
        => HasExplicitJsonProperty(settings, "VRPawn")
        || HasExplicitJsonProperty(settings, "UseOpenXR")
        || HasExplicitJsonProperty(settings, "SceneOnlyVRPawn")
        || HasExplicitJsonProperty(settings, "PreviewVRStereoViews")
        || HasExplicitJsonProperty(settings, "AllowEditingInVR");

    private static bool HasExplicitJsonProperty(UnitTestingWorldSettings settings, string propertyName)
        => !settings.TracksExplicitJsonProperties || settings.ExplicitJsonProperties.Contains(propertyName);

    private static UnitTestingVrSettings CreateVrSettingsFromLegacyFields(UnitTestingWorldSettings settings)
        => new()
        {
            Mode = ResolveLegacyVrMode(settings),
            PreviewStereoViews = settings.PreviewVRStereoViews,
            AllowDesktopEditing = settings.AllowEditingInVR,
            OpenXrRuntimeJson = settings.VR.OpenXrRuntimeJson,
        };

    private static UnitTestingVrLaunchMode ResolveLegacyVrMode(UnitTestingWorldSettings settings)
    {
        if (!settings.VRPawn)
            return UnitTestingVrLaunchMode.Desktop;

        if (settings.SceneOnlyVRPawn)
            return UnitTestingVrLaunchMode.Emulated;

        return settings.UseOpenXR
            ? UnitTestingVrLaunchMode.OpenXR
            : UnitTestingVrLaunchMode.OpenVR;
    }

    private static void ApplyVrModeToFlatFields(UnitTestingWorldSettings settings)
    {
        settings.VRPawn = settings.VR.Mode != UnitTestingVrLaunchMode.Desktop;
        settings.SceneOnlyVRPawn = settings.VR.Mode == UnitTestingVrLaunchMode.Emulated;
        settings.UseOpenXR = settings.VR.Mode is UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR;
        settings.PreviewVRStereoViews = settings.VR.PreviewStereoViews;
        settings.AllowEditingInVR = settings.VR.AllowDesktopEditing;
    }

    private static void ApplyOpenXrRuntimeJson(UnitTestingWorldSettings settings)
    {
        if (settings.VR.Mode is not (UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR))
            return;

        string? configuredRuntimeJson = settings.VR.OpenXrRuntimeJson;
        string? processRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (!string.IsNullOrWhiteSpace(processRuntimeJson))
        {
            if (!string.IsNullOrWhiteSpace(configuredRuntimeJson)
                && !string.Equals(processRuntimeJson, configuredRuntimeJson, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Out("[UnitTestingWorldSettings] Existing XR_RUNTIME_JSON process environment value wins over VR.OpenXrRuntimeJson.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(configuredRuntimeJson))
        {
            if (settings.VR.Mode == UnitTestingVrLaunchMode.MonadoOpenXR)
            {
                configuredRuntimeJson = TryAutoDetectMonadoRuntimeJson();
                if (!string.IsNullOrWhiteSpace(configuredRuntimeJson))
                {
                    settings.VR.OpenXrRuntimeJson = configuredRuntimeJson;
                }
                else
                {
                    Debug.Out("[UnitTestingWorldSettings] VR.Mode=MonadoOpenXR but VR.OpenXrRuntimeJson, XR_RUNTIME_JSON, and Monado auto-detection are empty. OpenXR startup will rely on the active loader/runtime configuration.");
                    return;
                }
            }
            else
            {
                return;
            }
        }

        string resolvedRuntimeJson = ResolveSettingsPath(configuredRuntimeJson);
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson, resolvedRuntimeJson, EnvironmentVariableTarget.Process);
        Debug.Out($"[UnitTestingWorldSettings] Set process XR_RUNTIME_JSON from VR.OpenXrRuntimeJson: {resolvedRuntimeJson}");
    }

    private static string? TryAutoDetectMonadoRuntimeJson()
    {
        List<string> manifestErrors = [];
        foreach (string candidate in EnumerateMonadoRuntimeJsonCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;

            if (TryValidateOpenXrRuntimeManifest(candidate, out string resolvedManifest, out string? runtimeName, out string? error))
            {
                Debug.Out($"[UnitTestingWorldSettings] Auto-detected Monado OpenXR runtime manifest: {resolvedManifest} ({runtimeName ?? "unknown runtime"}).");
                return resolvedManifest;
            }

            if (!string.IsNullOrWhiteSpace(error))
                manifestErrors.Add($"{candidate}: {error}");
        }

        if (manifestErrors.Count > 0)
        {
            Debug.Out(
                "[UnitTestingWorldSettings] Monado OpenXR runtime manifest candidates were found but invalid: " +
                string.Join("; ", manifestErrors));
        }

        return null;
    }

    private static IEnumerable<string> EnumerateMonadoRuntimeJsonCandidates()
    {
        string? monadoRuntimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoRuntimeJson);
        if (!string.IsNullOrWhiteSpace(monadoRuntimeJson))
            yield return ResolveSettingsPath(monadoRuntimeJson);

        string? monadoInstallDir = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir);
        if (!string.IsNullOrWhiteSpace(monadoInstallDir))
        {
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "share", "openxr", "1", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "bin", "openxr_monado.json"));
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "share", "openxr", "1", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "openxr_monado.json"));
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Monado", "share", "openxr", "1", "openxr_monado.json"));

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return ResolveSettingsPath(Path.Combine(localAppData, "Monado", "openxr_monado.json"));

        yield return ResolveSettingsPath(Path.Combine("Build", "Submodules", "monado", "build", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("ThirdParty", "Monado", "openxr_monado.json"));
    }

    private static bool TryValidateOpenXrRuntimeManifest(
        string manifestPath,
        out string resolvedManifestPath,
        out string? runtimeName,
        out string? error)
    {
        resolvedManifestPath = ResolveSettingsPath(manifestPath);
        runtimeName = null;
        error = null;

        try
        {
            JObject manifest = JObject.Parse(File.ReadAllText(resolvedManifestPath));
            JObject? runtime = manifest["runtime"] as JObject;
            if (runtime is null)
            {
                error = "Manifest does not contain a runtime object.";
                return false;
            }

            runtimeName = runtime["name"]?.Value<string>();
            string? libraryPath = runtime["library_path"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                error = "Manifest runtime.library_path is empty.";
                return false;
            }

            string resolvedLibraryPath = ResolveRuntimeLibraryPath(resolvedManifestPath, libraryPath);
            if (!File.Exists(resolvedLibraryPath))
            {
                error = $"Resolved runtime library does not exist: {resolvedLibraryPath}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ResolveRuntimeLibraryPath(string manifestPath, string libraryPath)
    {
        string expanded = Environment.ExpandEnvironmentVariables(libraryPath);
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        string? manifestDirectory = Path.GetDirectoryName(manifestPath);
        return Path.GetFullPath(Path.Combine(manifestDirectory ?? Environment.CurrentDirectory, expanded));
    }

    private static string ResolveSettingsPath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path);
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, expanded));
    }

    private static void MarkJsonPropertySpecified(UnitTestingWorldSettings settings, string propertyName)
    {
        if (!settings.TracksExplicitJsonProperties)
            return;

        var properties = new HashSet<string>(settings.ExplicitJsonProperties, StringComparer.OrdinalIgnoreCase)
        {
            propertyName
        };
        settings.ExplicitJsonProperties = properties;
    }
}
