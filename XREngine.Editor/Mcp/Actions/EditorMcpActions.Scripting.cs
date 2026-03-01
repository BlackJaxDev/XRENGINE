using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components.Scripting;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // P2.1 — Script CRUD
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists all .cs files in the game scripts directory.
        /// </summary>
        [XRMcp]
        [McpName("list_game_scripts")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("List all .cs files in the game project's assets directory.")]
        public static Task<McpToolResponse> ListGameScriptsAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative subfolder within the game assets directory. Empty string or omit for root.")]
            string path = "",
            [McpName("recursive"), Description("Search subdirectories recursively.")]
            bool recursive = true)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string targetDir = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: true, expectDirectory: true);
            if (error is not null)
                return Task.FromResult(error);

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(targetDir, "*.cs", searchOption)
                .Select(f => new
                {
                    path = Path.GetRelativePath(assetsPath, f).Replace('\\', '/'),
                    name = Path.GetFileName(f),
                    size = new FileInfo(f).Length,
                    lastModified = File.GetLastWriteTimeUtc(f).ToString("o")
                })
                .OrderBy(f => f.path)
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Found {files.Length} .cs file(s).",
                new { scriptCount = files.Length, scripts = files }));
        }

        /// <summary>
        /// Reads the contents of a .cs file from game assets.
        /// </summary>
        [XRMcp]
        [McpName("read_game_script")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("Read the contents of a .cs script file from the game project's assets directory.")]
        public static Task<McpToolResponse> ReadGameScriptAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path to the .cs file within the game assets directory.")]
            string path)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new McpToolResponse("Path must point to a .cs file.", isError: true));

            string content = File.ReadAllText(fullPath);
            int lineCount = content.Split('\n').Length;

            return Task.FromResult(new McpToolResponse(
                $"Read '{Path.GetFileName(fullPath)}' ({lineCount} lines, {content.Length} chars).",
                new
                {
                    path = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/'),
                    content,
                    lineCount,
                    charCount = content.Length
                }));
        }

        /// <summary>
        /// Writes or creates a .cs script file in the game assets directory.
        /// Triggers CodeManager invalidation so the file watcher picks up the change.
        /// </summary>
        [XRMcp]
        [McpName("write_game_script")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Creates or overwrites a .cs file on disk.")]
        [Description("Write or create a .cs script file in the game project's assets directory. Optionally triggers immediate compilation.")]
        public static Task<McpToolResponse> WriteGameScriptAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path for the .cs file within the game assets directory.")]
            string path,
            [McpName("content"), Description("The full C# source code to write to the file.")]
            string content,
            [McpName("compile_now"), Description("If true, triggers an immediate compilation after writing.")]
            bool compileNow = false)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new McpToolResponse("Path must end with .cs extension.", isError: true));

            // Ensure parent directory exists
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool isNew = !File.Exists(fullPath);
            File.WriteAllText(fullPath, content);

            string verb = isNew ? "Created" : "Updated";
            string message = $"{verb} script '{Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/')}'.";

            if (compileNow)
            {
                var cm = CodeManager.Instance;
                cm.RemakeSolutionAsDLL(compileNow: true);
                message += " Compilation triggered.";
            }

            return Task.FromResult(new McpToolResponse(message, new
            {
                path = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/'),
                isNew,
                compilationTriggered = compileNow
            }));
        }

        /// <summary>
        /// Deletes a .cs script file from the game assets directory.
        /// </summary>
        [XRMcp]
        [McpName("delete_game_script")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Permanently deletes a .cs file from disk.")]
        [Description("Delete a .cs script file from the game project's assets directory.")]
        public static Task<McpToolResponse> DeleteGameScriptAsync(
            McpToolContext context,
            [McpName("path"), Description("Relative path to the .cs file within the game assets directory.")]
            string path)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullPath = ResolveAndValidateGamePath(assetsPath, path, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new McpToolResponse("Path must point to a .cs file.", isError: true));

            string relativePath = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/');
            File.Delete(fullPath);

            return Task.FromResult(new McpToolResponse(
                $"Deleted script '{relativePath}'.",
                new { path = relativePath, deleted = true }));
        }

        /// <summary>
        /// Renames or moves a .cs script file within the game assets directory.
        /// </summary>
        [XRMcp]
        [McpName("rename_game_script")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Moves/renames a .cs file on disk.")]
        [Description("Rename or move a .cs script file within the game project's assets directory.")]
        public static Task<McpToolResponse> RenameGameScriptAsync(
            McpToolContext context,
            [McpName("old_path"), Description("Current relative path to the .cs file within game assets.")]
            string oldPath,
            [McpName("new_path"), Description("New relative path for the .cs file within game assets.")]
            string newPath)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            string fullOldPath = ResolveAndValidateGamePath(assetsPath, oldPath, out error, mustExist: true, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!fullOldPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new McpToolResponse("Old path must point to a .cs file.", isError: true));

            string fullNewPath = ResolveAndValidateGamePath(assetsPath, newPath, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (!fullNewPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new McpToolResponse("New path must end with .cs extension.", isError: true));

            if (File.Exists(fullNewPath))
                return Task.FromResult(new McpToolResponse($"Destination already exists: '{newPath}'.", isError: true));

            // Ensure destination directory exists
            string? destDir = Path.GetDirectoryName(fullNewPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Move(fullOldPath, fullNewPath);

            return Task.FromResult(new McpToolResponse(
                $"Renamed script from '{oldPath}' to '{newPath}'.",
                new
                {
                    oldPath = Path.GetRelativePath(assetsPath, fullOldPath).Replace('\\', '/'),
                    newPath = Path.GetRelativePath(assetsPath, fullNewPath).Replace('\\', '/'),
                    renamed = true
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P2.2 — Compilation & Hot-Reload
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Triggers CodeManager.RemakeSolutionAsDLL to regenerate project files, compile, and hot-reload.
        /// </summary>
        [XRMcp]
        [McpName("compile_game_scripts")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Compiles game scripts and hot-reloads the DLL into the running editor.")]
        [Description("Regenerate game project files, compile, and hot-reload the game DLL. Returns compilation result.")]
        public static Task<McpToolResponse> CompileGameScriptsAsync(
            McpToolContext context,
            [McpName("config"), Description("Build configuration: 'Debug' or 'Release'.")]
            string config = "Debug",
            [McpName("platform"), Description("Target platform: 'Any CPU' or 'x64'.")]
            string platform = "Any CPU")
        {
            // Validate config/platform
            if (config != CodeManager.Config_Debug && config != CodeManager.Config_Release)
                return Task.FromResult(new McpToolResponse($"Invalid config '{config}'. Use 'Debug' or 'Release'.", isError: true));

            if (platform != CodeManager.Platform_AnyCPU && platform != CodeManager.Platform_x64 && platform != CodeManager.Platform_x86)
                return Task.FromResult(new McpToolResponse($"Invalid platform '{platform}'. Use 'Any CPU', 'x64', or 'x86'.", isError: true));

            var cm = CodeManager.Instance;
            cm.RemakeSolutionAsDLL(compileNow: false);
            bool success = cm.CompileSolution(config, platform);

            if (success)
            {
                string binaryPath = cm.GetBinaryPath(config, platform);
                return Task.FromResult(new McpToolResponse(
                    $"Compilation succeeded. DLL hot-reloaded from '{Path.GetFileName(binaryPath)}'.",
                    new
                    {
                        success = true,
                        config,
                        platform,
                        binaryPath,
                        projectName = cm.GetProjectName()
                    }));
            }

            return Task.FromResult(new McpToolResponse(
                "Compilation failed. Check the editor console for detailed error output.",
                new { success = false, config, platform },
                isError: true));
        }

        /// <summary>
        /// Gets the current compilation state of the game scripts.
        /// </summary>
        [XRMcp]
        [McpName("get_compile_status")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("Get the current compilation state of the game scripts: whether scripts are dirty, last binary path, compile-on-change status.")]
        public static Task<McpToolResponse> GetCompileStatusAsync(McpToolContext context)
        {
            var cm = CodeManager.Instance;
            string binaryPath = cm.GetBinaryPath();
            bool binaryExists = File.Exists(binaryPath);
            bool gameLoaded = GameCSProjLoader.IsLoaded("GAME");

            return Task.FromResult(new McpToolResponse(
                "Compile status retrieved.",
                new
                {
                    compileOnChange = cm.CompileOnChange,
                    binaryPath,
                    binaryExists,
                    gameAssemblyLoaded = gameLoaded,
                    projectName = cm.GetProjectName(),
                    solutionPath = cm.GetSolutionPath()
                }));
        }

        /// <summary>
        /// Returns structured compile errors and warnings from the last build.
        /// </summary>
        /// <remarks>
        /// This performs a fresh compilation to capture diagnostics in real time.
        /// The CodeManager uses MSBuild in-process (or CLI fallback), which writes
        /// output to the Debug console. We capture errors via the StringLogger by
        /// triggering a build and returning the log output.
        /// </remarks>
        [XRMcp]
        [McpName("get_compile_errors")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("Compile the game scripts and return any errors and warnings as structured data.")]
        public static Task<McpToolResponse> GetCompileErrorsAsync(
            McpToolContext context,
            [McpName("config"), Description("Build configuration: 'Debug' or 'Release'.")]
            string config = "Debug",
            [McpName("platform"), Description("Target platform: 'Any CPU' or 'x64'.")]
            string platform = "Any CPU")
        {
            var cm = CodeManager.Instance;

            // Ensure project files are up to date
            cm.RemakeSolutionAsDLL(compileNow: false);

            string solutionPath = cm.GetSolutionPath();
            if (!File.Exists(solutionPath))
            {
                return Task.FromResult(new McpToolResponse(
                    "No solution file found. Ensure game scripts exist in the assets directory.",
                    isError: true));
            }

            // Run compilation via CLI to capture full output
            bool success = BuildProjectFileForDiagnostics(solutionPath, config, platform, out var diagnostics);

            return Task.FromResult(new McpToolResponse(
                success
                    ? $"Build succeeded with {diagnostics.Count(d => d.severity == "warning")} warning(s)."
                    : $"Build failed with {diagnostics.Count(d => d.severity == "error")} error(s) and {diagnostics.Count(d => d.severity == "warning")} warning(s).",
                new
                {
                    success,
                    errorCount = diagnostics.Count(d => d.severity == "error"),
                    warningCount = diagnostics.Count(d => d.severity == "warning"),
                    diagnostics
                }));
        }

        /// <summary>
        /// Toggles CodeManager.CompileOnChange which auto-compiles when .cs files change and editor regains focus.
        /// </summary>
        [XRMcp]
        [McpName("set_compile_on_change")]
        [McpPermission(McpPermissionLevel.Mutate)]
        [Description("Toggle CodeManager.CompileOnChange: when enabled, game scripts auto-compile when .cs files change and editor regains focus.")]
        public static Task<McpToolResponse> SetCompileOnChangeAsync(
            McpToolContext context,
            [McpName("enabled"), Description("True to enable auto-compilation on file change, false to disable.")]
            bool enabled)
        {
            CodeManager.Instance.CompileOnChange = enabled;

            return Task.FromResult(new McpToolResponse(
                $"CompileOnChange set to {enabled}.",
                new { compileOnChange = enabled }));
        }

        /// <summary>
        /// Gets game project metadata: project name, solution path, binary path, target framework, and loaded state.
        /// </summary>
        [XRMcp]
        [McpName("get_game_project_info")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("Get game project metadata: project name, solution/binary paths, target framework, and loaded assembly state.")]
        public static Task<McpToolResponse> GetGameProjectInfoAsync(McpToolContext context)
        {
            var cm = CodeManager.Instance;
            string projectName = cm.GetProjectName();
            string solutionPath = cm.GetSolutionPath();
            string binaryPath = cm.GetBinaryPath();
            bool binaryExists = File.Exists(binaryPath);
            bool gameLoaded = GameCSProjLoader.IsLoaded("GAME");
            string? gameAssetsPath = Engine.Assets?.GameAssetsPath;

            // Count .cs files if assets path is available
            int scriptCount = 0;
            if (!string.IsNullOrEmpty(gameAssetsPath) && Directory.Exists(gameAssetsPath))
                scriptCount = Directory.GetFiles(gameAssetsPath, "*.cs", SearchOption.AllDirectories).Length;

            // Project info from XRProject if loaded
            var project = Engine.CurrentProject;

            return Task.FromResult(new McpToolResponse(
                $"Game project info for '{projectName}'.",
                new
                {
                    projectName,
                    targetFramework = CodeManager.TargetFramework,
                    solutionPath,
                    solutionExists = File.Exists(solutionPath),
                    binaryPath,
                    binaryExists,
                    binaryLastModified = binaryExists ? File.GetLastWriteTimeUtc(binaryPath).ToString("o") : null as string,
                    gameAssemblyLoaded = gameLoaded,
                    compileOnChange = cm.CompileOnChange,
                    gameAssetsPath,
                    scriptCount,
                    xrProject = project is not null
                        ? new
                        {
                            name = project.ProjectName,
                            version = project.ProjectVersion,
                            engineVersion = project.EngineVersion,
                            description = project.Description,
                            author = project.Author,
                            projectDirectory = project.ProjectDirectory
                        }
                        : null as object
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P2.3 — Loaded Plugin Inspection
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists all types currently loaded from the game DLL plugin assemblies.
        /// </summary>
        [XRMcp]
        [McpName("get_loaded_game_types")]
        [McpPermission(McpPermissionLevel.ReadOnly)]
        [Description("List all types loaded from the game DLL plugin: components, menu items, and all exported types grouped by assembly.")]
        public static Task<McpToolResponse> GetLoadedGameTypesAsync(McpToolContext context)
        {
            var loaded = GameCSProjLoader.LoadedAssemblies;
            if (loaded.Count == 0)
            {
                return Task.FromResult(new McpToolResponse(
                    "No game assemblies are currently loaded.",
                    new { assemblyCount = 0, assemblies = Array.Empty<object>() }));
            }

            var assemblies = loaded.Select(kvp => new
            {
                id = kvp.Key,
                components = kvp.Value.Components
                    .Select(t => new
                    {
                        name = t.Name,
                        fullName = t.FullName,
                        @namespace = t.Namespace,
                        isAbstract = t.IsAbstract
                    })
                    .OrderBy(t => t.fullName)
                    .ToArray(),
                menuItems = kvp.Value.MenuItems
                    .Select(t => new
                    {
                        name = t.Name,
                        fullName = t.FullName,
                        @namespace = t.Namespace
                    })
                    .OrderBy(t => t.fullName)
                    .ToArray()
            }).ToArray();

            int totalComponents = assemblies.Sum(a => a.components.Length);
            int totalMenuItems = assemblies.Sum(a => a.menuItems.Length);

            return Task.FromResult(new McpToolResponse(
                $"Found {loaded.Count} loaded assembly(ies) with {totalComponents} component(s) and {totalMenuItems} menu item(s).",
                new
                {
                    assemblyCount = loaded.Count,
                    totalComponents,
                    totalMenuItems,
                    assemblies
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P2.4 — Code Scaffolding
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generates a new XRComponent subclass from a template with proper backing fields, SetField, lifecycle methods.
        /// </summary>
        [XRMcp]
        [McpName("scaffold_component")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Creates a new .cs file on disk.")]
        [Description("Generate a new XRComponent subclass from a template. Creates a .cs file with backing fields, SetField pattern, lifecycle hooks, and Description attribute.")]
        public static Task<McpToolResponse> ScaffoldComponentAsync(
            McpToolContext context,
            [McpName("class_name"), Description("Name of the component class (e.g., 'HealthComponent').")]
            string className,
            [McpName("namespace"), Description("C# namespace for the component (e.g., 'MyGame.Components').")]
            string @namespace,
            [McpName("properties"), Description("JSON array of property definitions: [{\"name\":\"Health\",\"type\":\"float\",\"default\":\"100.0f\"}, ...]. Types are C# type names.")]
            object[]? properties = null,
            [McpName("dest_path"), Description("Relative path within game assets for the .cs file. Defaults to '<ClassName>.cs' in root.")]
            string? destPath = null)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            // Validate class name
            if (string.IsNullOrWhiteSpace(className) || !IsValidCSharpIdentifier(className))
                return Task.FromResult(new McpToolResponse($"Invalid class name: '{className}'.", isError: true));

            if (string.IsNullOrWhiteSpace(@namespace))
                return Task.FromResult(new McpToolResponse("Namespace cannot be empty.", isError: true));

            string fileName = destPath ?? $"{className}.cs";
            string fullPath = ResolveAndValidateGamePath(assetsPath, fileName, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (File.Exists(fullPath))
                return Task.FromResult(new McpToolResponse($"File already exists: '{fileName}'. Use write_game_script to overwrite.", isError: true));

            // Parse properties
            var props = ParsePropertyDefinitions(properties);

            string source = GenerateComponentSource(className, @namespace, props);

            // Ensure parent directory exists
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, source);

            string relativePath = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/');
            return Task.FromResult(new McpToolResponse(
                $"Scaffolded component '{className}' at '{relativePath}'.",
                new
                {
                    path = relativePath,
                    className,
                    @namespace,
                    propertyCount = props.Count,
                    created = true
                }));
        }

        /// <summary>
        /// Generates a new game mode class from a template.
        /// </summary>
        [XRMcp]
        [McpName("scaffold_game_mode")]
        [McpPermission(McpPermissionLevel.Destructive, Reason = "Creates a new .cs file on disk.")]
        [Description("Generate a new game mode class from template. Creates a .cs file extending GameMode<T> with standard lifecycle methods.")]
        public static Task<McpToolResponse> ScaffoldGameModeAsync(
            McpToolContext context,
            [McpName("class_name"), Description("Name of the game mode class (e.g., 'ArenaGameMode').")]
            string className,
            [McpName("namespace"), Description("C# namespace for the game mode (e.g., 'MyGame.Modes').")]
            string @namespace,
            [McpName("dest_path"), Description("Relative path within game assets for the .cs file. Defaults to '<ClassName>.cs' in root.")]
            string? destPath = null)
        {
            if (!TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error))
                return Task.FromResult(error!);

            if (string.IsNullOrWhiteSpace(className) || !IsValidCSharpIdentifier(className))
                return Task.FromResult(new McpToolResponse($"Invalid class name: '{className}'.", isError: true));

            if (string.IsNullOrWhiteSpace(@namespace))
                return Task.FromResult(new McpToolResponse("Namespace cannot be empty.", isError: true));

            string fileName = destPath ?? $"{className}.cs";
            string fullPath = ResolveAndValidateGamePath(assetsPath, fileName, out error, mustExist: false, expectDirectory: false);
            if (error is not null)
                return Task.FromResult(error);

            if (File.Exists(fullPath))
                return Task.FromResult(new McpToolResponse($"File already exists: '{fileName}'. Use write_game_script to overwrite.", isError: true));

            string source = GenerateGameModeSource(className, @namespace);

            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, source);

            string relativePath = Path.GetRelativePath(assetsPath, fullPath).Replace('\\', '/');
            return Task.FromResult(new McpToolResponse(
                $"Scaffolded game mode '{className}' at '{relativePath}'.",
                new
                {
                    path = relativePath,
                    className,
                    @namespace,
                    created = true
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // Scripting Helpers
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the game assets path, returning false with an error response if unavailable.
        /// </summary>
        private static bool TryGetGameAssetsPath(out string assetsPath, out McpToolResponse? error)
        {
            assetsPath = Engine.Assets?.GameAssetsPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assetsPath) || !Directory.Exists(assetsPath))
            {
                error = new McpToolResponse(
                    "Game assets path is not configured or does not exist. Load a project first.",
                    isError: true);
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// Resolves a relative path against the game assets root and validates it stays within bounds.
        /// Rejects path traversal attempts (.., absolute paths outside root).
        /// </summary>
        private static string ResolveAndValidateGamePath(
            string assetsRoot,
            string relativePath,
            out McpToolResponse? error,
            bool mustExist,
            bool expectDirectory)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                // Empty path = root directory
                if (expectDirectory)
                    return assetsRoot;

                error = new McpToolResponse("Path cannot be empty for a file operation.", isError: true);
                return string.Empty;
            }

            // Reject absolute paths immediately
            if (Path.IsPathRooted(relativePath))
            {
                error = new McpToolResponse("Absolute paths are not allowed. Use paths relative to game assets.", isError: true);
                return string.Empty;
            }

            // Reject obvious traversal
            if (relativePath.Contains(".."))
            {
                error = new McpToolResponse("Path traversal ('..') is not allowed.", isError: true);
                return string.Empty;
            }

            // Normalize and combine
            string combined = Path.GetFullPath(Path.Combine(assetsRoot, relativePath));

            // Verify the resolved path is still under assetsRoot
            string normalizedRoot = Path.GetFullPath(assetsRoot);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedRoot += Path.DirectorySeparatorChar;

            if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !combined.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                error = new McpToolResponse("Resolved path escapes the game assets directory.", isError: true);
                return string.Empty;
            }

            // Existence checks
            if (mustExist)
            {
                if (expectDirectory)
                {
                    if (!Directory.Exists(combined))
                    {
                        error = new McpToolResponse($"Directory not found: '{relativePath}'.", isError: true);
                        return string.Empty;
                    }
                }
                else
                {
                    if (!File.Exists(combined))
                    {
                        error = new McpToolResponse($"File not found: '{relativePath}'.", isError: true);
                        return string.Empty;
                    }
                }
            }

            return combined;
        }

        /// <summary>
        /// Validates a C# identifier name (class name, property name, etc.).
        /// </summary>
        private static bool IsValidCSharpIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                    return false;
            }

            // Reject C# keywords
            return !IsCSharpKeyword(name);
        }

        private static bool IsCSharpKeyword(string name)
        {
            return name switch
            {
                "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or
                "char" or "checked" or "class" or "const" or "continue" or "decimal" or "default" or
                "delegate" or "do" or "double" or "else" or "enum" or "event" or "explicit" or
                "extern" or "false" or "finally" or "fixed" or "float" or "for" or "foreach" or
                "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or "is" or
                "lock" or "long" or "namespace" or "new" or "null" or "object" or "operator" or
                "out" or "override" or "params" or "private" or "protected" or "public" or
                "readonly" or "ref" or "return" or "sbyte" or "sealed" or "short" or "sizeof" or
                "stackalloc" or "static" or "string" or "struct" or "switch" or "this" or "throw" or
                "true" or "try" or "typeof" or "uint" or "ulong" or "unchecked" or "unsafe" or
                "ushort" or "using" or "virtual" or "void" or "volatile" or "while" => true,
                _ => false
            };
        }

        /// <summary>
        /// Parses property definitions from the MCP arguments (JSON array of objects).
        /// </summary>
        private static List<(string name, string type, string? defaultValue)> ParsePropertyDefinitions(object[]? properties)
        {
            var result = new List<(string name, string type, string? defaultValue)>();
            if (properties is null)
                return result;

            foreach (var prop in properties)
            {
                if (prop is not Dictionary<string, object> dict && prop is not System.Text.Json.JsonElement)
                    continue;

                string? name = null;
                string? type = null;
                string? defaultValue = null;

                if (prop is Dictionary<string, object> d)
                {
                    name = d.TryGetValue("name", out var n) ? n?.ToString() : null;
                    type = d.TryGetValue("type", out var t) ? t?.ToString() : null;
                    defaultValue = d.TryGetValue("default", out var dv) ? dv?.ToString() : null;
                }
                else if (prop is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    name = je.TryGetProperty("name", out var n) ? n.GetString() : null;
                    type = je.TryGetProperty("type", out var t) ? t.GetString() : null;
                    defaultValue = je.TryGetProperty("default", out var dv) ? dv.GetString() : null;
                }

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type))
                    result.Add((name!, type!, defaultValue));
            }

            return result;
        }

        /// <summary>
        /// Generates XRComponent subclass source code with engine-idiomatic patterns.
        /// </summary>
        private static string GenerateComponentSource(
            string className,
            string ns,
            List<(string name, string type, string? defaultValue)> properties)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using XREngine.Scene;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [Description(\"TODO: Describe what {className} does.\")]");
            sb.AppendLine($"    public class {className} : XRComponent");
            sb.AppendLine("    {");

            // Backing fields
            foreach (var (name, type, defaultValue) in properties)
            {
                string fieldName = $"_{char.ToLowerInvariant(name[0])}{name[1..]}";
                string init = defaultValue is not null ? $" = {defaultValue}" : GetDefaultForType(type);
                sb.AppendLine($"        private {type} {fieldName}{init};");
            }

            if (properties.Count > 0)
                sb.AppendLine();

            // Properties using SetField
            foreach (var (name, type, _) in properties)
            {
                string fieldName = $"_{char.ToLowerInvariant(name[0])}{name[1..]}";
                sb.AppendLine($"        public {type} {name}");
                sb.AppendLine("        {");
                sb.AppendLine($"            get => {fieldName};");
                sb.AppendLine($"            set => SetField(ref {fieldName}, value);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Lifecycle methods
            sb.AppendLine("        protected override void OnComponentActivated()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.OnComponentActivated();");
            sb.AppendLine("            // TODO: Initialize component state");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        protected override void OnComponentDeactivated()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.OnComponentDeactivated();");
            sb.AppendLine("            // TODO: Clean up component state");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        protected override void RegisterCallbacks(bool register)");
            sb.AppendLine("        {");
            sb.AppendLine("            base.RegisterCallbacks(register);");
            sb.AppendLine("            // TODO: Register/unregister event callbacks");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Gets a default initializer string for common C# types.
        /// </summary>
        private static string GetDefaultForType(string type) => type switch
        {
            "float" => " = 0.0f",
            "double" => " = 0.0",
            "int" => " = 0",
            "uint" => " = 0u",
            "long" => " = 0L",
            "bool" => " = false",
            "string" => " = string.Empty",
            _ => string.Empty
        };

        /// <summary>
        /// Generates a game mode subclass source code.
        /// </summary>
        private static string GenerateGameModeSource(string className, string ns)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using System.ComponentModel;");
            sb.AppendLine("using XREngine;");
            sb.AppendLine("using XREngine.Scene;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [Description(\"TODO: Describe what {className} does.\")]");
            sb.AppendLine($"    public class {className} : GameMode<{className}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        public {className}() {{ }}");
            sb.AppendLine();
            sb.AppendLine("        protected override void StartMatch()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.StartMatch();");
            sb.AppendLine("            // TODO: Initialize match state, spawn players, etc.");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        protected override void EndMatch()");
            sb.AppendLine("        {");
            sb.AppendLine("            base.EndMatch();");
            sb.AppendLine("            // TODO: Clean up match state");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Runs a CLI build and parses the output into structured diagnostics.
        /// </summary>
        private static bool BuildProjectFileForDiagnostics(
            string projectPath,
            string config,
            string platform,
            out List<(string severity, string code, string message, string file, int line, int column)> diagnostics)
        {
            diagnostics = [];

            var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
            };

            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("/restore");
            startInfo.ArgumentList.Add("/nologo");
            startInfo.ArgumentList.Add("/t:Build");
            startInfo.ArgumentList.Add($"/p:Configuration={config}");
            startInfo.ArgumentList.Add($"/p:Platform={platform}");
            // Use a format that's easy to parse
            startInfo.ArgumentList.Add("/clp:NoSummary;ErrorsOnly;WarningsOnly");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return false;

            string output = process.StandardOutput.ReadToEnd();
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Parse MSBuild diagnostic output format: file(line,col): severity code: message
            var diagRegex = new System.Text.RegularExpressions.Regex(
                @"^(.+?)\((\d+),(\d+)\)\s*:\s*(error|warning)\s+(\w+)\s*:\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            string combined = output + "\n" + errors;
            foreach (System.Text.RegularExpressions.Match match in diagRegex.Matches(combined))
            {
                diagnostics.Add((
                    severity: match.Groups[4].Value,
                    code: match.Groups[5].Value,
                    message: match.Groups[6].Value.Trim(),
                    file: match.Groups[1].Value.Trim(),
                    line: int.TryParse(match.Groups[2].Value, out int l) ? l : 0,
                    column: int.TryParse(match.Groups[3].Value, out int c) ? c : 0
                ));
            }

            return process.ExitCode == 0;
        }
    }
}
