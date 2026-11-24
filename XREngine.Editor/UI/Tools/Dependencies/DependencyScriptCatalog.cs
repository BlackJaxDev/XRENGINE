using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Tools;

/// <summary>
/// Discovers runnable PowerShell dependency scripts, native C++ bridge projects, and managed
/// build targets from the workspace, parses PowerShell parameter metadata, and builds safe
/// process command lines.
/// </summary>
public static partial class DependencyScriptCatalog
{
    [GeneratedRegex(@"\[ValidateSet\((?<values>[^\)]+)\)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ValidateSetRegex();

    [GeneratedRegex(@"\[(?<type>switch|string|int|int32|bool|boolean)\]\s*\$(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*=\s*(?<default>[^,\r\n\)]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterDeclarationRegex();

    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> (or the current/base directories) to
    /// locate the workspace root containing <c>Tools/Dependencies</c> or <c>XRENGINE.slnx</c>.
    /// </summary>
    public static string? FindRepositoryRoot(string? startDirectory = null)
    {
        string[] roots = !string.IsNullOrWhiteSpace(startDirectory)
            ? [startDirectory]
            : [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];

        for (int r = 0; r < roots.Length; r++)
        {
            if (string.IsNullOrWhiteSpace(roots[r]) || !Directory.Exists(roots[r]))
                continue;

            DirectoryInfo? current = new DirectoryInfo(roots[r]);
            for (int depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
            {
                string depsDir = Path.Combine(current.FullName, "Tools", "Dependencies");
                string slnx = Path.Combine(current.FullName, "XRENGINE.slnx");
                if (Directory.Exists(depsDir) || File.Exists(slnx))
                    return current.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Discovers all dependency scripts under <c>Tools/Dependencies/</c>, native C++ bridge
    /// projects under <c>Build/Native/</c>, and primary managed build targets under
    /// <paramref name="repoRoot"/>.
    /// </summary>
    public static List<DependencyTaskDescriptor> DiscoverTasks(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var tasks = new List<DependencyTaskDescriptor>();

        DiscoverDependencyScripts(repoRoot, tasks);
        DiscoverNativeBridgeProjects(repoRoot, tasks);
        DiscoverManagedBuildTargets(repoRoot, tasks);

        tasks.Sort(static (left, right) =>
        {
            int kindCompare = left.Kind.CompareTo(right.Kind);
            return kindCompare != 0
                ? kindCompare
                : StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        });

        return tasks;
    }

    /// <summary>
    /// Parses a PowerShell script's comment help (<c>.SYNOPSIS</c>, <c>.DESCRIPTION</c>,
    /// <c>.PARAMETER</c>) and top-level <c>param(...)</c> block into UI parameter descriptors.
    /// </summary>
    public static DependencyTaskDescriptor ParsePowerShellScript(string repoRoot, string scriptFullPath)
    {
        string relativePath = Path.GetRelativePath(repoRoot, scriptFullPath).Replace('\\', '/');
        string fileNameNoExt = Path.GetFileNameWithoutExtension(scriptFullPath);
        string content = File.ReadAllText(scriptFullPath);

        ParseCommentHelp(
            content,
            out string synopsis,
            out string description,
            out Dictionary<string, string> parameterDescriptions);

        DependencyScriptParameterDescriptor[] parameters = ParseParamBlock(content, parameterDescriptions);

        string category = fileNameNoExt.StartsWith("Build-", StringComparison.OrdinalIgnoreCase)
            ? "Dependencies (Build)"
            : fileNameNoExt.StartsWith("Install-", StringComparison.OrdinalIgnoreCase)
                ? "Dependencies (Install)"
                : "Dependencies (Stage)";

        var descriptor = new DependencyTaskDescriptor
        {
            Id = $"script:{relativePath}",
            DisplayName = fileNameNoExt,
            Category = category,
            Kind = DependencyTaskKind.DependencyScript,
            RelativePath = relativePath,
            FullPath = scriptFullPath,
            Synopsis = string.IsNullOrWhiteSpace(synopsis) ? $"Runs {relativePath}." : synopsis,
            Description = description,
            Parameters = parameters,
        };

        EvaluateReadiness(repoRoot, descriptor);
        return descriptor;
    }

    /// <summary>
    /// Refreshes staged or built status badges for all discovered tasks.
    /// </summary>
    public static void RefreshStatuses(string repoRoot, IReadOnlyList<DependencyTaskDescriptor> tasks)
    {
        for (int i = 0; i < tasks.Count; i++)
            EvaluateReadiness(repoRoot, tasks[i]);
    }

    /// <summary>
    /// Builds the process executable, argument string, and readable preview command for a task.
    /// </summary>
    public static bool TryBuildCommandLine(
        string repoRoot,
        DependencyTaskDescriptor task,
        IReadOnlyDictionary<string, string> parameterValues,
        string configuration,
        string extraArguments,
        out string fileName,
        out string arguments,
        out string displayCommand,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(task);
        string config = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "Release";
        string trimmedExtra = extraArguments?.Trim() ?? string.Empty;

        if (task.Kind == DependencyTaskKind.DependencyScript)
        {
            fileName = ResolvePowerShellExecutable();
            var args = new StringBuilder();
            args.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            args.Append(QuoteArgument(task.FullPath));

            for (int i = 0; i < task.Parameters.Length; i++)
            {
                DependencyScriptParameterDescriptor param = task.Parameters[i];
                parameterValues.TryGetValue(param.Name, out string? rawValue);
                string value = rawValue ?? param.DefaultValue;

                if (param.Kind == DependencyScriptParameterKind.Switch)
                {
                    if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1")
                        args.Append(" -").Append(param.Name);
                }
                else if (param.Kind == DependencyScriptParameterKind.Bool)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        bool b = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
                        args.Append(" -").Append(param.Name).Append(b ? ":$true" : ":$false");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    args.Append(" -").Append(param.Name).Append(' ').Append(QuoteArgument(value.Trim()));
                }
            }

            if (!string.IsNullOrEmpty(trimmedExtra))
                args.Append(' ').Append(trimmedExtra);

            arguments = args.ToString();
            displayCommand = $"{Path.GetFileName(fileName)} {arguments}";
            error = null;
            return true;
        }

        if (task.Kind == DependencyTaskKind.NativeBridgeBuild)
        {
            string? msbuildPath = ResolveMsBuildPath();
            if (string.IsNullOrWhiteSpace(msbuildPath))
            {
                fileName = string.Empty;
                arguments = string.Empty;
                displayCommand = string.Empty;
                error = "MSBuild.exe was not found. Install Visual Studio Build Tools with the Desktop development with C++ workload.";
                return false;
            }

            fileName = msbuildPath;
            var args = new StringBuilder();
            args.Append(QuoteArgument(task.FullPath));
            args.Append($" /p:Configuration={config} /p:Platform=x64 /m:2 /nr:false");
            if (!string.IsNullOrEmpty(trimmedExtra))
                args.Append(' ').Append(trimmedExtra);

            arguments = args.ToString();
            displayCommand = $"msbuild {arguments}";
            error = null;
            return true;
        }

        // Managed builds run through Tools/Invoke-AgentBuild.ps1 when available so MSBuild stays
        // serialized and bounded on Windows.
        string wrapperPath = Path.Combine(repoRoot, "Tools", "Invoke-AgentBuild.ps1");
        if (File.Exists(wrapperPath))
        {
            fileName = ResolvePowerShellExecutable();
            var args = new StringBuilder();
            args.Append("-NoProfile -ExecutionPolicy Bypass -Command ");
            var psCommand = new StringBuilder();
            psCommand.Append("& '").Append(wrapperPath.Replace("'", "''")).Append("' -DotnetArguments @('build', '")
                .Append(task.RelativePath.Replace("'", "''")).Append("', '-c', '").Append(config).Append("'");
            if (!string.IsNullOrEmpty(trimmedExtra))
            {
                foreach (string token in SplitCommandLineTokens(trimmedExtra))
                    psCommand.Append(", '").Append(token.Replace("'", "''")).Append('\'');
            }
            psCommand.Append(')');
            args.Append(QuoteArgument(psCommand.ToString()));

            arguments = args.ToString();
            displayCommand = $"{Path.GetFileName(fileName)} {arguments}";
            error = null;
            return true;
        }

        fileName = "dotnet";
        arguments = $"build {QuoteArgument(task.FullPath)} -c {config}" + (string.IsNullOrEmpty(trimmedExtra) ? string.Empty : $" {trimmedExtra}");
        displayCommand = $"dotnet {arguments}";
        error = null;
        return true;
    }

    public static string ResolvePowerShellExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            string? pwsh = FindOnPath("pwsh.exe");
            if (!string.IsNullOrEmpty(pwsh))
                return pwsh;

            string programFilesPwsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell",
                "7",
                "pwsh.exe");
            if (File.Exists(programFilesPwsh))
                return programFilesPwsh;

            return "powershell.exe";
        }

        return "pwsh";
    }

    public static string? ResolveMsBuildPath()
    {
        if (!OperatingSystem.IsWindows())
            return FindOnPath("msbuild");

        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio",
            "Installer",
            "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process? process = Process.Start(psi);
                if (process is not null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                    foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string candidate = line.Trim();
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch
            {
                // Fall through to PATH lookup.
            }
        }

        return FindOnPath("MSBuild.exe");
    }

    private static void DiscoverDependencyScripts(string repoRoot, List<DependencyTaskDescriptor> tasks)
    {
        string depsDir = Path.Combine(repoRoot, "Tools", "Dependencies");
        if (!Directory.Exists(depsDir))
            return;

        foreach (string file in Directory.EnumerateFiles(depsDir, "*.ps1", SearchOption.TopDirectoryOnly))
        {
            try
            {
                tasks.Add(ParsePowerShellScript(repoRoot, file));
            }
            catch
            {
                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                tasks.Add(new DependencyTaskDescriptor
                {
                    Id = $"script:{relative}",
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    Category = "Dependencies",
                    Kind = DependencyTaskKind.DependencyScript,
                    RelativePath = relative,
                    FullPath = file,
                    Synopsis = $"Runs {relative}.",
                    Description = string.Empty,
                    Parameters = [],
                });
            }
        }
    }

    private static void DiscoverNativeBridgeProjects(string repoRoot, List<DependencyTaskDescriptor> tasks)
    {
        string nativeDir = Path.Combine(repoRoot, "Build", "Native");
        if (!Directory.Exists(nativeDir))
            return;

        foreach (string vcxproj in Directory.EnumerateFiles(nativeDir, "*.vcxproj", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(repoRoot, vcxproj).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(vcxproj);
            var descriptor = new DependencyTaskDescriptor
            {
                Id = $"native:{relative}",
                DisplayName = $"{name} (C++ Native Bridge)",
                Category = "Native Builds",
                Kind = DependencyTaskKind.NativeBridgeBuild,
                RelativePath = relative,
                FullPath = vcxproj,
                Synopsis = $"Builds native C++ bridge project {relative} (x64).",
                Description = "Uses Visual Studio MSBuild to compile the native C++ interop DLL.",
                Parameters = [],
            };
            EvaluateReadiness(repoRoot, descriptor);
            tasks.Add(descriptor);
        }
    }

    private static void DiscoverManagedBuildTargets(string repoRoot, List<DependencyTaskDescriptor> tasks)
    {
        (string RelativePath, string DisplayName, string Synopsis)[] candidates =
        [
            ("XREngine.Editor/XREngine.Editor.csproj", "Build Editor (XREngine.Editor)", "Builds the desktop ImGui editor project."),
            ("XREngine.Server/XREngine.Server.csproj", "Build Server (XREngine.Server)", "Builds the dedicated server executable."),
            ("XREngine.VRClient/XREngine.VRClient.csproj", "Build VR Client (XREngine.VRClient)", "Builds the standalone VR client executable."),
            ("XREngine.Browser/XREngine.Browser.csproj", "Build Browser (XREngine.Browser)", "Builds the WebAssembly browser host project."),
            ("XRENGINE.slnx", "Build Full Solution (XRENGINE.slnx)", "Builds all projects in the repository solution."),
        ];

        for (int i = 0; i < candidates.Length; i++)
        {
            string fullPath = Path.Combine(repoRoot, candidates[i].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            tasks.Add(new DependencyTaskDescriptor
            {
                Id = $"managed:{candidates[i].RelativePath}",
                DisplayName = candidates[i].DisplayName,
                Category = "Managed Builds",
                Kind = DependencyTaskKind.ManagedBuild,
                RelativePath = candidates[i].RelativePath,
                FullPath = fullPath,
                Synopsis = candidates[i].Synopsis,
                Description = "Runs through Tools/Invoke-AgentBuild.ps1 to keep MSBuild serialized and responsive.",
                Parameters = [],
                IsReady = null,
                StatusSummary = "Build target",
            });
        }
    }

    private static void EvaluateReadiness(string repoRoot, DependencyTaskDescriptor task)
    {
        string name = Path.GetFileNameWithoutExtension(task.FullPath);

        if (task.Kind == DependencyTaskKind.NativeBridgeBuild)
        {
            bool dllFound = CheckNativeBridgeDll(repoRoot, name);
            task.IsReady = dllFound;
            task.StatusSummary = dllFound ? "Built" : "Not built";
            return;
        }

        if (task.Kind != DependencyTaskKind.DependencyScript)
            return;

        if (name.Equals("Get-MotionBricks", StringComparison.OrdinalIgnoreCase))
        {
            string onnx = Path.Combine(repoRoot, "Build", "Dependencies", "MotionBricks", "soma", "mmm_v6_soma_fp16.onnx");
            string presets = Path.Combine(repoRoot, "Build", "Dependencies", "MotionBricks", "smart_primitives", "soma");
            bool hasOnnx = File.Exists(onnx);
            bool hasPresets = Directory.Exists(presets);
            task.IsReady = hasOnnx && hasPresets;
            task.StatusSummary = hasOnnx && hasPresets
                ? "Staged (FP16 + presets)"
                : hasOnnx ? "ONNX staged, presets missing" : "Not staged";
        }
        else if (name.Equals("Get-TensorRT", StringComparison.OrdinalIgnoreCase))
        {
            string? envTrt = Environment.GetEnvironmentVariable("TENSORRT_ROOT_DIR");
            bool envOk = !string.IsNullOrWhiteSpace(envTrt) && File.Exists(Path.Combine(envTrt, "include", "NvInfer.h"));
            bool stagedOk = HasFileUnder(Path.Combine(repoRoot, "Build", "Dependencies", "TensorRT"), "NvInfer.h");
            task.IsReady = envOk || stagedOk;
            task.StatusSummary = envOk || stagedOk ? "Staged" : "Not staged";
        }
        else if (name.Equals("Get-CudaToolkit", StringComparison.OrdinalIgnoreCase))
        {
            string? cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            bool cudaOk = !string.IsNullOrWhiteSpace(cudaPath) && Directory.Exists(cudaPath);
            task.IsReady = cudaOk;
            task.StatusSummary = cudaOk ? "Installed (CUDA_PATH)" : "CUDA_PATH not set";
        }
        else if (name.Equals("Build-Meshoptimizer", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "Meshoptimizer", "win-x64"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Built (win-x64)" : "Not built";
        }
        else if (name.Equals("Build-Bc7Enc", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "Bc7Enc"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not built";
        }
        else if (name.Equals("Build-CoACD", StringComparison.OrdinalIgnoreCase) || name.Equals("Get-CoACD", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "CoACD"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not staged";
        }
        else if (name.Equals("Get-Phonon", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "SteamAudio"))
                || HasFileUnder(Path.Combine(repoRoot, "XREngine.Audio.SteamAudio"), "phonon.dll");
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not staged";
        }
        else if (name.Equals("Get-Audio2Face3DSdk", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "Audio2Face-3D-SDK"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not staged";
        }
        else if (name.Equals("Get-StreamlineSdk", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "ThirdParty", "NVIDIA", "SDK", "win-x64"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not staged";
        }
        else if (name.Equals("Get-UltralightResources", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = Directory.Exists(Path.Combine(repoRoot, "Build", "Dependencies", "Ultralight"));
            task.IsReady = ok;
            task.StatusSummary = ok ? "Staged" : "Not staged";
        }
        else
        {
            task.IsReady = null;
            task.StatusSummary = "Script";
        }
    }

    private static bool CheckNativeBridgeDll(string repoRoot, string bridgeName)
    {
        string expectedDll = $"{bridgeName}.Native.dll";
        string[] probeDirs =
        [
            Path.Combine(repoRoot, "XREngine.Animation.MotionBricks", "runtimes", "win-x64", "native"),
            Path.Combine(repoRoot, "XREngine.Gltf", "runtimes", "win-x64", "native"),
            Path.Combine(repoRoot, "XREngine.Audio.Audio2Face", "runtimes", "win-x64", "native"),
            Path.Combine(repoRoot, "Build", "Native", bridgeName),
            Path.Combine(repoRoot, "Build", "Editor"),
        ];

        for (int i = 0; i < probeDirs.Length; i++)
        {
            if (HasFileUnder(probeDirs[i], expectedDll))
                return true;
        }

        return false;
    }

    private static bool HasFileUnder(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return false;

        try
        {
            foreach (string _ in Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories))
                return true;
        }
        catch
        {
            // Ignore transient access errors during scan.
        }

        return false;
    }

    private static void ParseCommentHelp(
        string scriptText,
        out string synopsis,
        out string description,
        out Dictionary<string, string> paramDescriptions)
    {
        string parsedSynopsis = string.Empty;
        string parsedDescription = string.Empty;
        var parsedParamDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int commentStart = scriptText.IndexOf("<#", StringComparison.Ordinal);
        if (commentStart < 0)
        {
            synopsis = parsedSynopsis;
            description = parsedDescription;
            paramDescriptions = parsedParamDescriptions;
            return;
        }

        int commentEnd = scriptText.IndexOf("#>", commentStart + 2, StringComparison.Ordinal);
        if (commentEnd < 0)
        {
            synopsis = parsedSynopsis;
            description = parsedDescription;
            paramDescriptions = parsedParamDescriptions;
            return;
        }

        string block = scriptText.Substring(commentStart + 2, commentEnd - (commentStart + 2));
        string[] lines = block.Split(['\r', '\n']);

        string currentSection = string.Empty;
        string currentParamName = string.Empty;
        var currentBuilder = new StringBuilder();

        void FlushSection()
        {
            string text = currentBuilder.ToString().Trim();
            currentBuilder.Clear();
            if (string.IsNullOrEmpty(text))
                return;

            if (currentSection.Equals("SYNOPSIS", StringComparison.OrdinalIgnoreCase))
                parsedSynopsis = text;
            else if (currentSection.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase))
                parsedDescription = text;
            else if (currentSection.Equals("PARAMETER", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(currentParamName))
                parsedParamDescriptions[currentParamName] = text;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('.'))
            {
                FlushSection();
                string[] headerParts = trimmed[1..].Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                currentSection = headerParts.Length > 0 ? headerParts[0] : string.Empty;
                currentParamName = headerParts.Length > 1 ? headerParts[1].Trim() : string.Empty;
                continue;
            }

            if (!string.IsNullOrEmpty(currentSection))
            {
                if (currentBuilder.Length > 0 && !string.IsNullOrEmpty(trimmed))
                    currentBuilder.Append(' ');
                currentBuilder.Append(trimmed);
            }
        }

        FlushSection();
        synopsis = parsedSynopsis;
        description = parsedDescription;
        paramDescriptions = parsedParamDescriptions;
    }

    private static DependencyScriptParameterDescriptor[] ParseParamBlock(
        string scriptText,
        IReadOnlyDictionary<string, string> parameterDescriptions)
    {
        int paramIdx = FindTopLevelParamBlockStart(scriptText);
        if (paramIdx < 0)
            return [];

        int openParen = scriptText.IndexOf('(', paramIdx);
        if (openParen < 0)
            return [];

        int closeParen = FindMatchingCloseParen(scriptText, openParen);
        if (closeParen <= openParen)
            return [];

        string paramBody = scriptText.Substring(openParen + 1, closeParen - openParen - 1);
        List<string> chunks = SplitTopLevelCommas(paramBody);
        var results = new List<DependencyScriptParameterDescriptor>();

        for (int i = 0; i < chunks.Count; i++)
        {
            string chunk = chunks[i];
            Match declMatch = ParameterDeclarationRegex().Match(chunk);
            if (!declMatch.Success)
                continue;

            string rawType = declMatch.Groups["type"].Value;
            string name = declMatch.Groups["name"].Value;
            string rawDefault = declMatch.Groups["default"].Success ? declMatch.Groups["default"].Value.Trim() : string.Empty;

            DependencyScriptParameterKind kind = rawType.ToLowerInvariant() switch
            {
                "switch" => DependencyScriptParameterKind.Switch,
                "int" or "int32" => DependencyScriptParameterKind.Int,
                "bool" or "boolean" => DependencyScriptParameterKind.Bool,
                _ => DependencyScriptParameterKind.String,
            };

            string defaultVal = CleanDefaultLiteral(rawDefault, kind);
            string[] allowedValues = [];
            Match validateMatch = ValidateSetRegex().Match(chunk);
            if (validateMatch.Success)
                allowedValues = ParseAllowedValues(validateMatch.Groups["values"].Value);

            parameterDescriptions.TryGetValue(name, out string? desc);
            results.Add(new DependencyScriptParameterDescriptor(
                name,
                kind,
                defaultVal,
                allowedValues,
                desc ?? string.Empty));
        }

        return [.. results];
    }

    private static int FindTopLevelParamBlockStart(string scriptText)
    {
        int searchStart = 0;
        int commentStart = scriptText.IndexOf("<#", StringComparison.Ordinal);
        if (commentStart >= 0)
        {
            int commentEnd = scriptText.IndexOf("#>", commentStart + 2, StringComparison.Ordinal);
            if (commentEnd > commentStart)
                searchStart = commentEnd + 2;
        }

        int idx = scriptText.IndexOf("param(", searchStart, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return idx;

        return scriptText.IndexOf("param (", searchStart, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindMatchingCloseParen(string text, int openParenIndex)
    {
        int depth = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = openParenIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (inSingleQuote || inDoubleQuote)
                continue;

            if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevelCommas(string text)
    {
        var parts = new List<string>();
        int parenDepth = 0;
        int bracketDepth = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (inSingleQuote || inDoubleQuote)
                continue;

            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (c == ',' && parenDepth == 0 && bracketDepth == 0)
            {
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (start < text.Length)
            parts.Add(text.Substring(start));

        return parts;
    }

    private static string CleanDefaultLiteral(string rawDefault, DependencyScriptParameterKind kind)
    {
        if (string.IsNullOrWhiteSpace(rawDefault))
            return kind == DependencyScriptParameterKind.Switch ? "false" : string.Empty;

        string trimmed = rawDefault.Trim();
        if (trimmed.StartsWith('$'))
        {
            if (trimmed.Equals("$true", StringComparison.OrdinalIgnoreCase))
                return "true";
            if (trimmed.Equals("$false", StringComparison.OrdinalIgnoreCase))
                return "false";
            return string.Empty;
        }

        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            return trimmed.Length >= 2 ? trimmed[1..^1] : string.Empty;

        return trimmed;
    }

    private static string[] ParseAllowedValues(string rawValues)
    {
        var values = new List<string>();
        foreach (string part in rawValues.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string cleaned = part.Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(cleaned))
                values.Add(cleaned);
        }

        return [.. values];
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (!value.Contains(' ') && !value.Contains('"') && !value.Contains('\t'))
            return value;
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static List<string> SplitCommandLineTokens(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string? FindOnPath(string executableName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
