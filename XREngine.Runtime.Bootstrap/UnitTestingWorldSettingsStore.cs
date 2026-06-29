using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XREngine.Audio;

namespace XREngine.Runtime.Bootstrap;

public static class UnitTestingWorldSettingsStore
{
    public const string SettingsFileName = "UnitTestingWorldSettings.jsonc";
    private const string MonadoServiceProcessName = "monado-service";
    private const string MonadoServiceExeName = "monado-service.exe";
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
            settings.ExplicitJsonPropertyPaths = ReadJsonPropertyPaths(serializedSettings);
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
        settings.ExplicitJsonPropertyPaths = ReadJsonPropertyPaths(content);
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

        if (RequiresGpuRenderDispatchForOpenXrVulkan(settings))
            startupSettings.GPURenderDispatch = true;

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

    private static bool RequiresGpuRenderDispatchForOpenXrVulkan(UnitTestingWorldSettings settings)
        => settings.VR.Mode is UnitTestingVrLaunchMode.MonadoOpenXR or UnitTestingVrLaunchMode.OpenXR
        && ResolveRenderBackend(settings) == ERenderLibrary.Vulkan;

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

        if (TryGetEnumEnv(XREngineEnvironmentVariables.UnitTestVrViewRenderMode, out EVrViewRenderMode viewRenderMode))
        {
            settings.VR.ViewRenderMode = viewRenderMode;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.ViewRenderMode));
            applied = true;
        }

        if (TryGetEnumEnv(XREngineEnvironmentVariables.UnitTestVrFoveationMode, out EVrFoveationMode foveationMode))
        {
            settings.VR.Foveation.Mode = foveationMode;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation), nameof(UnitTestingVrFoveationSettings.Mode));
            applied = true;
        }

        if (TryGetEnumEnv(XREngineEnvironmentVariables.UnitTestVrFoveationQualityPreset, out EVrFoveationQualityPreset foveationQualityPreset))
        {
            settings.VR.Foveation.QualityPreset = foveationQualityPreset;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation), nameof(UnitTestingVrFoveationSettings.QualityPreset));
            applied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestVrFoveationRequireRequested, out bool foveationRequireRequested))
        {
            settings.VR.Foveation.RequireRequested = foveationRequireRequested;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation));
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.Foveation), nameof(UnitTestingVrFoveationSettings.RequireRequested));
            applied = true;
        }

        if (TryGetBoolEnv(XREngineEnvironmentVariables.UnitTestRenderWindowsWhileInVr, out bool renderWindowsWhileInVr))
        {
            settings.RenderWindowsWhileInVR = renderWindowsWhileInVr;
            MarkJsonPropertySpecified(settings, nameof(UnitTestingWorldSettings.RenderWindowsWhileInVR));
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
                $"RenderWindowsWhileInVR={settings.RenderWindowsWhileInVR}, RenderBackend={ResolveRenderBackend(settings)}, " +
                $"ViewRenderMode={settings.VR.ViewRenderMode}, Foveation={settings.VR.Foveation.Mode}/{settings.VR.Foveation.QualityPreset}, " +
                $"FoveationRequireRequested={settings.VR.Foveation.RequireRequested}.");
        }
    }

    public static void NormalizeVrSettings(UnitTestingWorldSettings settings)
    {
        if (!settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VR)) && HasExplicitLegacyVrSettings(settings))
            settings.VR = CreateVrSettingsFromLegacyFields(settings);

        ApplyLegacySinglePassStereoMigration(settings);
        ApplyVrModeToFlatFields(settings);
        ApplyOpenXrRuntimeJson(settings);
        ApplyOpenXrLoaderPath(settings);
        ApplyMonadoServiceStartup(settings);
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

    private static HashSet<string> ReadJsonPropertyPaths(string? content)
    {
        var propertyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return propertyPaths;

        using var textReader = new StringReader(content);
        using var jsonReader = new JsonTextReader(textReader)
        {
            DateParseHandling = DateParseHandling.None,
        };

        if (JToken.ReadFrom(jsonReader) is not JObject root)
            return propertyPaths;

        AddJsonPropertyPaths(root, null, propertyPaths);
        return propertyPaths;
    }

    private static void AddJsonPropertyPaths(JObject obj, string? prefix, HashSet<string> propertyPaths)
    {
        foreach (JProperty property in obj.Properties())
        {
            string path = string.IsNullOrEmpty(prefix)
                ? property.Name
                : $"{prefix}.{property.Name}";
            propertyPaths.Add(path);

            if (property.Value is JObject child)
                AddJsonPropertyPaths(child, path, propertyPaths);
        }
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
        || HasExplicitJsonProperty(settings, "AllowEditingInVR")
        || HasExplicitJsonProperty(settings, nameof(UnitTestingWorldSettings.SinglePassStereoVR));

    private static bool HasExplicitJsonProperty(UnitTestingWorldSettings settings, string propertyName)
        => !settings.TracksExplicitJsonProperties || settings.ExplicitJsonProperties.Contains(propertyName);

    private static UnitTestingVrSettings CreateVrSettingsFromLegacyFields(UnitTestingWorldSettings settings)
        => new()
        {
            Mode = ResolveLegacyVrMode(settings),
            ViewRenderMode = settings.SinglePassStereoVR
                ? EVrViewRenderMode.SinglePassStereo
                : EVrViewRenderMode.SequentialViews,
            PreviewStereoViews = settings.PreviewVRStereoViews,
            AllowDesktopEditing = settings.AllowEditingInVR,
            Foveation = settings.VR.Foveation,
            OpenXrRuntimeJson = settings.VR.OpenXrRuntimeJson,
        };

    private static void ApplyLegacySinglePassStereoMigration(UnitTestingWorldSettings settings)
    {
        if (!HasExplicitJsonProperty(settings, nameof(UnitTestingWorldSettings.SinglePassStereoVR)))
            return;

        if (settings.IsJsonPropertyPathSpecified(nameof(UnitTestingWorldSettings.VR), nameof(UnitTestingVrSettings.ViewRenderMode)))
            return;

        settings.VR.ViewRenderMode = settings.SinglePassStereoVR
            ? EVrViewRenderMode.SinglePassStereo
            : EVrViewRenderMode.SequentialViews;
    }

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
        settings.SinglePassStereoVR = settings.VR.ViewRenderMode == EVrViewRenderMode.SinglePassStereo;
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

    private static void ApplyOpenXrLoaderPath(UnitTestingWorldSettings settings)
    {
        if (settings.VR.Mode != UnitTestingVrLaunchMode.MonadoOpenXR)
            return;

        string? loaderPath = TryAutoDetectOpenXrLoader();
        if (string.IsNullOrWhiteSpace(loaderPath))
        {
            Debug.Out("[UnitTestingWorldSettings] VR.Mode=MonadoOpenXR but openxr_loader.dll was not auto-detected. OpenXR startup will rely on the app directory and process PATH.");
            return;
        }

        string? loaderDirectory = Path.GetDirectoryName(loaderPath);
        if (string.IsNullOrWhiteSpace(loaderDirectory))
            return;

        PrependProcessPath(loaderDirectory);
        Debug.Out($"[UnitTestingWorldSettings] Added OpenXR loader directory to process PATH: {loaderDirectory}");
    }

    private static void ApplyMonadoServiceStartup(UnitTestingWorldSettings settings)
        => _ = TryEnsureMonadoService(settings, "initial UnitTestingWorld settings normalization");

    public static bool TryEnsureMonadoServiceForCurrentProcess(string reason)
    {
        UnitTestingWorldSettings? settings = RuntimeBootstrapState.Settings;
        if (settings is null)
            return false;

        return TryEnsureMonadoService(settings, reason);
    }

    private static bool TryEnsureMonadoService(UnitTestingWorldSettings settings, string reason)
    {
        if (settings.VR.Mode != UnitTestingVrLaunchMode.MonadoOpenXR)
            return false;

        if (TryGetRunningMonadoService(out int existingPid, out string? existingPath))
        {
            Debug.Out($"[UnitTestingWorldSettings] Reusing running Monado service pid={existingPid} path='{existingPath ?? "<unknown>"}'. Reason={reason}");
            return true;
        }

        string? runtimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (string.IsNullOrWhiteSpace(runtimeJson))
            runtimeJson = settings.VR.OpenXrRuntimeJson;
        if (string.IsNullOrWhiteSpace(runtimeJson))
            runtimeJson = TryAutoDetectMonadoRuntimeJson();

        if (string.IsNullOrWhiteSpace(runtimeJson))
        {
            Debug.Out("[UnitTestingWorldSettings] VR.Mode=MonadoOpenXR but no Monado runtime manifest was available for service startup.");
            return false;
        }

        if (!TryReadOpenXrRuntimeManifest(runtimeJson, out string resolvedRuntimeJson, out string? runtimeName, out string? runtimeLibraryPath, out string? manifestError))
        {
            Debug.Out($"[UnitTestingWorldSettings] Could not start Monado service because the OpenXR runtime manifest is invalid: {manifestError}");
            return false;
        }

        if (!LooksLikeMonadoRuntime(resolvedRuntimeJson, runtimeName))
        {
            Debug.Out($"[UnitTestingWorldSettings] VR.Mode=MonadoOpenXR selected, but active XR runtime '{runtimeName ?? "<unknown>"}' does not look like Monado. Service startup skipped.");
            return false;
        }

        string? servicePath = EnumerateMonadoServiceCandidates(resolvedRuntimeJson, runtimeLibraryPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(servicePath))
        {
            Debug.Out($"[UnitTestingWorldSettings] Could not locate {MonadoServiceExeName} near Monado runtime manifest '{resolvedRuntimeJson}'. OpenXR startup may fail with ErrorRuntimeUnavailable.");
            return false;
        }

        string? serviceDirectory = Path.GetDirectoryName(servicePath);
        string? runtimeLibraryDirectory = Path.GetDirectoryName(runtimeLibraryPath);
        if (!string.IsNullOrWhiteSpace(runtimeLibraryDirectory))
            PrependProcessPath(runtimeLibraryDirectory);
        if (!string.IsNullOrWhiteSpace(serviceDirectory))
            PrependProcessPath(serviceDirectory);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = servicePath,
                WorkingDirectory = serviceDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.Environment[XREngineEnvironmentVariables.XrRuntimeJson] = resolvedRuntimeJson;
            startInfo.Environment[XREngineEnvironmentVariables.Path] = BuildProcessPathWithPrependedDirectories(
                Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path),
                runtimeLibraryDirectory,
                serviceDirectory);

            System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                Debug.Out($"[UnitTestingWorldSettings] Failed to start Monado service from '{servicePath}': Process.Start returned null.");
                return false;
            }

            using (process)
            {
                process.WaitForExit(750);
                if (process.HasExited)
                {
                    Debug.Out($"[UnitTestingWorldSettings] Monado service exited immediately with code {process.ExitCode}: {servicePath}");
                    return false;
                }

                Debug.Out($"[UnitTestingWorldSettings] Started Monado service pid={process.Id}: {servicePath}. Reason={reason}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.Out($"[UnitTestingWorldSettings] Failed to start Monado service from '{servicePath}': {ex.Message}");
            return false;
        }
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
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "share", "openxr", "1", "openxr_monado-dev.json"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "openxr_monado-dev.json"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "bin", "openxr_monado.json"));
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "share", "openxr", "1", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "share", "openxr", "1", "openxr_monado-dev.json"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "openxr_monado-dev.json"));
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Monado", "share", "openxr", "1", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Monado", "share", "openxr", "1", "openxr_monado-dev.json"));
        }

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return ResolveSettingsPath(Path.Combine(localAppData, "Monado", "openxr_monado.json"));
            yield return ResolveSettingsPath(Path.Combine(localAppData, "Monado", "openxr_monado-dev.json"));
        }

        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "openxr_monado-dev.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "share", "openxr", "1", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "share", "openxr", "1", "openxr_monado-dev.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Submodules", "monado", "build", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Submodules", "monado", "build", "openxr_monado-dev.json"));
        yield return ResolveSettingsPath(Path.Combine("ThirdParty", "Monado", "openxr_monado.json"));
        yield return ResolveSettingsPath(Path.Combine("ThirdParty", "Monado", "openxr_monado-dev.json"));
    }

    private static string? TryAutoDetectOpenXrLoader()
    {
        foreach (string candidate in EnumerateOpenXrLoaderCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateOpenXrLoaderCandidates()
    {
        string? monadoInstallDir = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.MonadoInstallDir);
        if (!string.IsNullOrWhiteSpace(monadoInstallDir))
        {
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "bin", "openxr_loader.dll"));
            yield return ResolveSettingsPath(Path.Combine(monadoInstallDir, "openxr_loader.dll"));
        }

        yield return ResolveSettingsPath(Path.Combine("Build", "Dependencies", "vcpkg", "installed", "x64-windows", "bin", "openxr_loader.dll"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Submodules", "monado", "build", "vcpkg_installed", "x64-windows", "bin", "openxr_loader.dll"));
        yield return ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado", "bin", "openxr_loader.dll"));

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "bin", "openxr_loader.dll"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Monado", "openxr_loader.dll"));
            yield return ResolveSettingsPath(Path.Combine(programFiles, "Oculus", "Support", "oculus-runtime", "openxr_loader.dll"));
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Monado", "bin", "openxr_loader.dll"));
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Monado", "openxr_loader.dll"));
            yield return ResolveSettingsPath(Path.Combine(programFilesX86, "Steam", "steamapps", "common", "SteamVR", "bin", "win64", "openxr_loader.dll"));
        }

        string? path = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        foreach (string pathEntry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? candidate = TryResolveSettingsPath(Path.Combine(pathEntry, "openxr_loader.dll"));
            if (!string.IsNullOrWhiteSpace(candidate))
                yield return candidate;
        }
    }

    private static void PrependProcessPath(string directory)
    {
        string resolvedDirectory = Path.GetFullPath(directory);
        string? currentPath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.Path);
        List<string> entries = string.IsNullOrWhiteSpace(currentPath)
            ? []
            : currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (entries.Any(entry => IsSameDirectory(entry, resolvedDirectory)))
            return;

        string updatedPath = entries.Count == 0
            ? resolvedDirectory
            : resolvedDirectory + Path.PathSeparator + string.Join(Path.PathSeparator, entries);
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.Path, updatedPath, EnvironmentVariableTarget.Process);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidateOpenXrRuntimeManifest(
        string manifestPath,
        out string resolvedManifestPath,
        out string? runtimeName,
        out string? error)
    {
        return TryReadOpenXrRuntimeManifest(manifestPath, out resolvedManifestPath, out runtimeName, out _, out error);
    }

    private static bool TryReadOpenXrRuntimeManifest(
        string manifestPath,
        out string resolvedManifestPath,
        out string? runtimeName,
        out string? resolvedLibraryPath,
        out string? error)
    {
        resolvedManifestPath = ResolveSettingsPath(manifestPath);
        runtimeName = null;
        resolvedLibraryPath = null;
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

            resolvedLibraryPath = ResolveRuntimeLibraryPath(resolvedManifestPath, libraryPath);
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

    private static bool LooksLikeMonadoRuntime(string runtimeJson, string? runtimeName)
        => (!string.IsNullOrWhiteSpace(runtimeName) && runtimeName.Contains("Monado", StringComparison.OrdinalIgnoreCase))
        || Path.GetFileName(runtimeJson).Contains("monado", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateMonadoServiceCandidates(string runtimeJson, string? runtimeLibraryPath)
    {
        string? manifestDirectory = Path.GetDirectoryName(runtimeJson);
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            yield return Path.Combine(manifestDirectory, MonadoServiceExeName);
            yield return Path.Combine(manifestDirectory, "bin", MonadoServiceExeName);
        }

        string? runtimeLibraryDirectory = string.IsNullOrWhiteSpace(runtimeLibraryPath) ? null : Path.GetDirectoryName(runtimeLibraryPath);
        if (!string.IsNullOrWhiteSpace(runtimeLibraryDirectory))
        {
            yield return Path.Combine(runtimeLibraryDirectory, MonadoServiceExeName);

            string? runtimeInstallDirectory = Path.GetDirectoryName(runtimeLibraryDirectory);
            if (!string.IsNullOrWhiteSpace(runtimeInstallDirectory))
                yield return Path.Combine(runtimeInstallDirectory, "bin", MonadoServiceExeName);
        }

        string stagedInstallRoot = ResolveSettingsPath(Path.Combine("Build", "Deps", "Monado"));
        if (IsPathUnderDirectory(runtimeJson, stagedInstallRoot))
            yield return Path.Combine(stagedInstallRoot, "bin", MonadoServiceExeName);

        string buildRoot = ResolveSettingsPath(Path.Combine("Build", "Submodules", "monado", "build"));
        if (IsPathUnderDirectory(runtimeJson, buildRoot))
            yield return Path.Combine(buildRoot, "src", "xrt", "targets", "service", MonadoServiceExeName);
    }

    private static bool TryGetRunningMonadoService(out int pid, out string? processPath)
    {
        pid = 0;
        processPath = null;

        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName(MonadoServiceProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    pid = process.Id;
                    processPath = TryGetProcessPath(process);
                    return true;
                }
                catch
                {
                    // Process may exit between enumeration and inspection.
                }
            }
        }

        return false;
    }

    private static string? TryGetProcessPath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildProcessPathWithPrependedDirectories(string? currentPath, params string?[] directories)
    {
        List<string> entries = string.IsNullOrWhiteSpace(currentPath)
            ? []
            : currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        List<string> prepended = [];
        foreach (string? directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string resolvedDirectory = Path.GetFullPath(directory);
            if (!prepended.Any(entry => IsSameDirectory(entry, resolvedDirectory)))
                prepended.Add(resolvedDirectory);
        }

        foreach (string entry in entries)
        {
            if (!prepended.Any(prependedEntry => IsSameDirectory(prependedEntry, entry)))
                prepended.Add(entry);
        }

        return string.Join(Path.PathSeparator, prepended);
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

    private static string? TryResolveSettingsPath(string path)
    {
        try
        {
            return ResolveSettingsPath(path);
        }
        catch
        {
            return null;
        }
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
        MarkJsonPropertySpecified(settings, [propertyName]);
    }

    private static void MarkJsonPropertySpecified(UnitTestingWorldSettings settings, params string[] propertyPath)
    {
        if (!settings.TracksExplicitJsonProperties)
            return;

        var paths = new HashSet<string>(settings.ExplicitJsonPropertyPaths, StringComparer.OrdinalIgnoreCase)
        {
            string.Join('.', propertyPath)
        };
        settings.ExplicitJsonPropertyPaths = paths;
    }
}
