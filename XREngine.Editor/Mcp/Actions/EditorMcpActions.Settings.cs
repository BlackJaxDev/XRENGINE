using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // Phase 5 — Project Configuration & Settings
        // ═══════════════════════════════════════════════════════════════════

        // ───────────────────────────────────────────────────────────────────
        // P5.1 — Game Settings
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the current <see cref="GameStartupSettings"/> (networking, windows, timing, etc.).
        /// </summary>
        [XRMcp(Name = "get_game_settings", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read the current game startup settings (networking, windows, timing, build, etc.).")]
        public static Task<McpToolResponse> GetGameSettingsAsync(
            McpToolContext context,
            [McpName("category"), Description("Optional category filter (e.g. 'Networking', 'Rendering'). Omit for all settings.")]
            string? category = null,
            [McpName("include_build_settings"), Description("Also include nested BuildSettings properties.")]
            bool includeBuildSettings = false)
        {
            var settings = Engine.GameSettings;
            var properties = ReadSettingsProperties(settings, category);

            object? buildSettingsData = null;
            if (includeBuildSettings && settings.BuildSettings is { } buildSettings)
                buildSettingsData = ReadSettingsProperties(buildSettings, category: null);

            return Task.FromResult(new McpToolResponse(
                $"Game settings: {properties.Count} properties.",
                new
                {
                    settingsType = settings.GetType().FullName ?? settings.GetType().Name,
                    settingsId = settings.ID,
                    properties,
                    buildSettings = buildSettingsData
                }));
        }

        /// <summary>
        /// Modifies a property on <see cref="GameStartupSettings"/> (or its nested <see cref="BuildSettings"/>).
        /// </summary>
        [XRMcp(Name = "set_game_setting", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies game startup settings.")]
        [Description("Set a game startup setting by property name.")]
        public static Task<McpToolResponse> SetGameSettingAsync(
            McpToolContext context,
            [McpName("property_name"), Description("Property name (case-insensitive). Prefix with 'BuildSettings.' to target build settings.")]
            string propertyName,
            [McpName("value"), Description("JSON value to assign to the property.")]
            object value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return Task.FromResult(new McpToolResponse("property_name is required.", isError: true));

            // Support 'BuildSettings.PropertyName' for nested build settings
            XRBase target;
            string actualPropertyName;

            if (propertyName.StartsWith("BuildSettings.", StringComparison.OrdinalIgnoreCase))
            {
                if (Engine.GameSettings.BuildSettings is null)
                    return Task.FromResult(new McpToolResponse("BuildSettings is not initialized.", isError: true));

                target = Engine.GameSettings.BuildSettings;
                actualPropertyName = propertyName["BuildSettings.".Length..];
            }
            else
            {
                target = Engine.GameSettings;
                actualPropertyName = propertyName;
            }

            return SetSettingsProperty(target, actualPropertyName, value);
        }

        // ───────────────────────────────────────────────────────────────────
        // P5.2 — Editor Preferences
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the effective editor preferences (global + project overrides merged).
        /// </summary>
        [XRMcp(Name = "get_editor_preferences", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read all editor preferences (effective view: global base + project overrides merged).")]
        public static Task<McpToolResponse> GetEditorPreferencesAsync(
            McpToolContext context,
            [McpName("category"), Description("Optional category filter (e.g. 'MCP Server', 'Theme'). Omit for all.")]
            string? category = null,
            [McpName("show_source"), Description("Show whether each value comes from global defaults or project overrides.")]
            bool showSource = false)
        {
            var effective = Engine.EditorPreferences;
            var global = Engine.GlobalEditorPreferences;
            var overrides = Engine.EditorPreferencesOverrides;

            var effectiveProps = ReadSettingsProperties(effective, category);

            List<object>? sourceInfo = null;
            if (showSource)
            {
                sourceInfo = [];
                var globalType = global.GetType();
                var overridesType = overrides.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public;

                foreach (var prop in effectiveProps)
                {
                    string name = ((dynamic)prop).name;
                    var overrideProp = overridesType.GetProperty(name, flags);
                    bool isOverridden = false;

                    // Check if the overrides object has a non-default value for this property
                    if (overrideProp is not null && overrideProp.CanRead)
                    {
                        try
                        {
                            var overrideVal = overrideProp.GetValue(overrides);
                            var globalProp = globalType.GetProperty(name, flags);
                            if (globalProp is not null && globalProp.CanRead)
                            {
                                var globalVal = globalProp.GetValue(global);
                                isOverridden = !Equals(overrideVal, globalVal);
                            }
                        }
                        catch { /* Ignore reflection errors */ }
                    }

                    sourceInfo.Add(new
                    {
                        name,
                        source = isOverridden ? "project_override" : "global_default"
                    });
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Editor preferences: {effectiveProps.Count} properties.",
                new
                {
                    preferencesType = effective.GetType().FullName ?? effective.GetType().Name,
                    preferencesId = effective.ID,
                    properties = effectiveProps,
                    sources = sourceInfo
                }));
        }

        /// <summary>
        /// Modifies an editor preference on the <see cref="Engine.GlobalEditorPreferences"/> object.
        /// </summary>
        [XRMcp(Name = "set_editor_preference", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies editor preferences.")]
        [Description("Set an editor preference by property name (writes to the global default).")]
        public static Task<McpToolResponse> SetEditorPreferenceAsync(
            McpToolContext context,
            [McpName("property_name"), Description("Property name (case-insensitive).")]
            string propertyName,
            [McpName("value"), Description("JSON value to assign.")]
            object value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
                return Task.FromResult(new McpToolResponse("property_name is required.", isError: true));

            return SetSettingsProperty(Engine.GlobalEditorPreferences, propertyName, value);
        }

        // ───────────────────────────────────────────────────────────────────
        // P5.3 — Engine Settings (read-only overview)
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a combined overview of engine-level configuration:
        /// <see cref="UserSettings"/>, timing, project info, and runtime state.
        /// </summary>
        [XRMcp(Name = "get_engine_settings", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read engine configuration overview (user settings, timing, project info, runtime metrics).")]
        public static Task<McpToolResponse> GetEngineSettingsAsync(
            McpToolContext context,
            [McpName("section"), Description("Optional section: 'user', 'timing', 'project', 'runtime'. Omit for all.")]
            string? section = null)
        {
            var result = new Dictionary<string, object?>();

            bool all = string.IsNullOrWhiteSpace(section);

            // User settings
            if (all || string.Equals(section, "user", StringComparison.OrdinalIgnoreCase))
            {
                var userSettings = Engine.UserSettings;
                result["userSettings"] = new
                {
                    settingsType = userSettings.GetType().FullName ?? userSettings.GetType().Name,
                    settingsId = userSettings.ID,
                    properties = ReadSettingsProperties(userSettings, category: null)
                };
            }

            // Timing
            if (all || string.Equals(section, "timing", StringComparison.OrdinalIgnoreCase))
            {
                result["timing"] = new
                {
                    targetUpdatesPerSecond = Engine.GameSettings.TargetUpdatesPerSecond,
                    fixedFramesPerSecond = Engine.GameSettings.FixedFramesPerSecond,
                    targetFramesPerSecond = Engine.GameSettings.TargetFramesPerSecond,
                    unfocusedTargetFPS = Engine.GameSettings.UnfocusedTargetFramesPerSecond,
                };
            }

            // Project info
            if (all || string.Equals(section, "project", StringComparison.OrdinalIgnoreCase))
            {
                var project = Engine.CurrentProject;
                result["project"] = project is null
                    ? (object)new { loaded = false }
                    : new
                    {
                        loaded = true,
                        name = project.ProjectName,
                        version = project.ProjectVersion,
                        engineVersion = project.EngineVersion,
                        author = project.Author,
                        description = project.Description,
                        startupScenePath = project.StartupScenePath,
                        projectDirectory = project.ProjectDirectory,
                        assetsDirectory = project.AssetsDirectory,
                        configDirectory = project.ConfigDirectory,
                    };
            }

            // Runtime info
            if (all || string.Equals(section, "runtime", StringComparison.OrdinalIgnoreCase))
            {
                result["runtime"] = new
                {
                    networkingType = Engine.GameSettings.NetworkingType.ToString(),
                    gpuRenderDispatch = Engine.GameSettings.GPURenderDispatch,
                    logOutputToFile = Engine.GameSettings.LogOutputToFile,
                };
            }

            int sectionCount = result.Count;
            return Task.FromResult(new McpToolResponse(
                $"Engine settings: {sectionCount} section(s) returned.",
                result));
        }

        // ───────────────────────────────────────────────────────────────────
        // P5.4 — Game Config Files
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lists config files in the active project's Config/ directory.
        /// </summary>
        [XRMcp(Name = "list_game_configs", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List config files in the current project's Config/ directory.")]
        public static Task<McpToolResponse> ListGameConfigsAsync(
            McpToolContext context,
            [McpName("pattern"), Description("Optional file glob filter (e.g. '*.asset', '*.json'). Default: all files.")]
            string? pattern = null)
        {
            if (!TryGetConfigDirectory(out string configDir, out var error))
                return Task.FromResult(error!);

            var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;

            string[] files;
            try
            {
                files = Directory.GetFiles(configDir, searchPattern, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Failed to list config files: {ex.Message}", isError: true));
            }

            var entries = files.Select(f =>
            {
                var fi = new FileInfo(f);
                return new
                {
                    path = Path.GetRelativePath(configDir, f).Replace('\\', '/'),
                    name = fi.Name,
                    extension = fi.Extension.TrimStart('.'),
                    size = fi.Length,
                    lastModified = fi.LastWriteTimeUtc.ToString("o")
                };
            }).ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Config directory: {entries.Length} file(s).",
                new
                {
                    configDirectory = configDir,
                    files = entries
                }));
        }

        /// <summary>
        /// Reads a config file from the project's Config/ directory.
        /// </summary>
        [XRMcp(Name = "read_game_config", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read the contents of a config file from the project's Config/ directory.")]
        public static Task<McpToolResponse> ReadGameConfigAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path within the Config/ directory (e.g., 'engine_settings.asset').")]
            string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(new McpToolResponse("path is required.", isError: true));

            if (!TryGetConfigDirectory(out string configDir, out var error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateConfigPath(configDir, path, out var pathError, mustExist: true);
            if (pathError is not null)
                return Task.FromResult(pathError);

            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Failed to read config file: {ex.Message}", isError: true));
            }

            var fi = new FileInfo(fullPath);
            return Task.FromResult(new McpToolResponse(
                $"Read config file: {path}",
                new
                {
                    path = Path.GetRelativePath(configDir, fullPath).Replace('\\', '/'),
                    size = fi.Length,
                    lastModified = fi.LastWriteTimeUtc.ToString("o"),
                    content
                }));
        }

        /// <summary>
        /// Writes a config file to the project's Config/ directory.
        /// Creates the file if it does not exist.
        /// </summary>
        [XRMcp(Name = "write_game_config", Permission = McpPermissionLevel.Destructive, PermissionReason = "Creates or overwrites config files on disk.")]
        [Description("Write (create or overwrite) a config file in the project's Config/ directory.")]
        public static Task<McpToolResponse> WriteGameConfigAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path within the Config/ directory (e.g., 'custom_settings.json').")]
            string path,
            [McpName("content"), Description("The full text content to write.")]
            string content)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(new McpToolResponse("path is required.", isError: true));
            if (content is null)
                return Task.FromResult(new McpToolResponse("content is required.", isError: true));

            if (!TryGetConfigDirectory(out string configDir, out var error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateConfigPath(configDir, path, out var pathError, mustExist: false);
            if (pathError is not null)
                return Task.FromResult(pathError);

            bool exists = File.Exists(fullPath);

            try
            {
                // Ensure parent directory exists
                string? parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.WriteAllText(fullPath, content);
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Failed to write config file: {ex.Message}", isError: true));
            }

            var fi = new FileInfo(fullPath);
            return Task.FromResult(new McpToolResponse(
                exists ? $"Overwrote config file: {path}" : $"Created config file: {path}",
                new
                {
                    path = Path.GetRelativePath(configDir, fullPath).Replace('\\', '/'),
                    size = fi.Length,
                    created = !exists,
                    overwritten = exists
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // Settings Helpers
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads all public instance properties from a settings object, optionally filtered by <see cref="CategoryAttribute"/>.
        /// </summary>
        private static List<object> ReadSettingsProperties(object settingsObject, string? category)
        {
            var type = settingsObject.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public;

            return type
                .GetProperties(flags)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Where(p =>
                {
                    if (string.IsNullOrWhiteSpace(category))
                        return true;
                    var catAttr = p.GetCustomAttribute<CategoryAttribute>();
                    return catAttr is not null &&
                           catAttr.Category.Contains(category, StringComparison.OrdinalIgnoreCase);
                })
                .Select(p =>
                {
                    object? rawValue = null;
                    string? readError = null;
                    try { rawValue = p.GetValue(settingsObject); }
                    catch (Exception ex) { readError = ex.InnerException?.Message ?? ex.Message; }

                    var catAttr = p.GetCustomAttribute<CategoryAttribute>();
                    var descAttr = p.GetCustomAttribute<DescriptionAttribute>();

                    return (object)new
                    {
                        name = p.Name,
                        type = FormatTypeName(p.PropertyType),
                        category = catAttr?.Category,
                        description = descAttr?.Description,
                        canWrite = p.CanWrite,
                        value = readError is null ? ToMcpValue(rawValue) : null,
                        valuePreview = readError is null ? rawValue?.ToString() : null,
                        readError
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Sets a property on a settings object by name, using <see cref="McpToolRegistry.TryConvertValue"/>
        /// and <see cref="Undo.TrackChange"/> for undo support.
        /// </summary>
        private static Task<McpToolResponse> SetSettingsProperty(XRBase target, string propertyName, object value)
        {
            var targetType = target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;

            var property = targetType.GetProperty(propertyName, flags);
            if (property is null || !property.CanWrite)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Writable property '{propertyName}' not found on '{targetType.Name}'.", isError: true));
            }

            if (!McpToolRegistry.TryConvertValue(value, property.PropertyType, out var converted, out var convError))
            {
                return Task.FromResult(new McpToolResponse(
                    convError ?? $"Unable to convert value for '{property.Name}'.", isError: true));
            }

            using var _ = Undo.TrackChange($"MCP Set {property.Name}", target);
            property.SetValue(target, converted);

            return Task.FromResult(new McpToolResponse(
                $"Set '{property.Name}' on '{targetType.Name}'.",
                new
                {
                    settingsType = targetType.FullName ?? targetType.Name,
                    property = property.Name,
                    propertyType = FormatTypeName(property.PropertyType)
                }));
        }

        /// <summary>
        /// Resolves the Config directory from the current project. Falls back to the sandbox config directory.
        /// </summary>
        private static bool TryGetConfigDirectory(out string configDir, out McpToolResponse? error)
        {
            error = null;
            configDir = string.Empty;

            // Prefer the loaded project's Config/ directory
            var project = Engine.CurrentProject;
            if (project?.ConfigDirectory is not null && Directory.Exists(project.ConfigDirectory))
            {
                configDir = project.ConfigDirectory;
                return true;
            }

            // If no project is loaded, there's no config directory to expose
            error = new McpToolResponse(
                "No project is loaded, or the project's Config/ directory does not exist. Load a project first.",
                isError: true);
            return false;
        }

        /// <summary>
        /// Resolves a relative path against a config root and validates it stays within bounds.
        /// Mirrors the sandboxing logic used for game assets (path traversal protection).
        /// </summary>
        private static string ResolveAndValidateConfigPath(
            string configRoot,
            string relativePath,
            out McpToolResponse? error,
            bool mustExist)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = new McpToolResponse("Path cannot be empty.", isError: true);
                return string.Empty;
            }

            // Reject absolute paths
            if (Path.IsPathRooted(relativePath))
            {
                error = new McpToolResponse("Absolute paths are not allowed. Use paths relative to the Config/ directory.", isError: true);
                return string.Empty;
            }

            // Reject traversal
            if (relativePath.Contains(".."))
            {
                error = new McpToolResponse("Path traversal ('..') is not allowed.", isError: true);
                return string.Empty;
            }

            // Normalize and combine
            string combined = Path.GetFullPath(Path.Combine(configRoot, relativePath));

            // Verify the resolved path is still under configRoot
            string normalizedRoot = Path.GetFullPath(configRoot);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedRoot += Path.DirectorySeparatorChar;

            if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !combined.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                error = new McpToolResponse("Resolved path escapes the Config directory.", isError: true);
                return string.Empty;
            }

            // Existence check
            if (mustExist && !File.Exists(combined))
            {
                error = new McpToolResponse($"Config file not found: '{relativePath}'.", isError: true);
                return string.Empty;
            }

            return combined;
        }
    }
}
