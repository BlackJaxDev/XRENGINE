using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ImGuiNET;
using XREngine.Editor.Tools;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private static string? _dependenciesRepoRoot;
    private static List<DependencyTaskDescriptor>? _dependencyTasks;
    private static string _selectedDependencyTaskId = string.Empty;
    private static string _dependencySearchFilter = string.Empty;
    private static int _dependencyCategoryFilter;
    private static int _dependencyBuildConfigurationIndex;
    private static string _dependencyExtraArgs = string.Empty;
    private static bool _dependencyAutoScroll = true;
    private static string? _dependencyValidationError;
    private static readonly Dictionary<string, Dictionary<string, string>> _dependencyParamValues = new(StringComparer.Ordinal);
    private static readonly DependencyTaskRunner _dependencyTaskRunner = new();
    private static readonly List<DependencyTaskLogLine> _dependencyLogSnapshot = new(256);

    public static void OpenDependenciesAndBuildsPanel()
    {
        _showDependenciesAndBuildsPanel = true;
        RefreshDependencyTasksCatalog();
    }

    private static void DrawDependenciesAndBuildsPanel()
    {
        if (!_showDependenciesAndBuildsPanel)
            return;

        ImGui.SetNextWindowSize(new Vector2(1140.0f, 700.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Dependencies & Builds", ref _showDependenciesAndBuildsPanel))
        {
            ImGui.End();
            return;
        }

        if (_dependencyTasks is null)
            RefreshDependencyTasksCatalog();

        if (string.IsNullOrWhiteSpace(_dependenciesRepoRoot) || _dependencyTasks is null)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.40f, 0.30f, 1.0f), "Could not locate repository root containing Tools/Dependencies.");
            if (ImGui.Button("Retry Discovery"))
                RefreshDependencyTasksCatalog();
            ImGui.End();
            return;
        }

        DrawDependenciesHeaderBar();
        ImGui.Separator();

        Vector2 avail = ImGui.GetContentRegionAvail();
        float leftWidth = Math.Clamp(avail.X * 0.34f, 280.0f, 420.0f);

        if (ImGui.BeginChild("DependenciesTaskList", new Vector2(leftWidth, 0.0f), ImGuiChildFlags.Borders))
            DrawDependenciesTaskList();
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("DependenciesTaskWorkspace", Vector2.Zero, ImGuiChildFlags.None))
            DrawDependenciesTaskWorkspace();
        ImGui.EndChild();

        ImGui.End();
    }

    private static void DrawDependenciesHeaderBar()
    {
        ImGui.SetNextItemWidth(220.0f);
        ImGui.InputTextWithHint("##DepsSearch", "Filter scripts or builds...", ref _dependencySearchFilter, 128u);
        ImGui.SameLine();

        string[] filters = ["All", "Dependencies", "Native Builds", "Managed Builds"];
        ImGui.SetNextItemWidth(140.0f);
        ImGui.Combo("##DepsCategory", ref _dependencyCategoryFilter, filters, filters.Length);
        ImGui.SameLine();

        if (ImGui.SmallButton("Refresh"))
            RefreshDependencyTasksCatalog();

        ImGui.SameLine();
        ImGui.TextDisabled(_dependenciesRepoRoot ?? string.Empty);
    }

    private static void DrawDependenciesTaskList()
    {
        if (_dependencyTasks is null || _dependencyTasks.Count == 0)
        {
            ImGui.TextDisabled("No scripts or build targets found.");
            return;
        }

        string search = _dependencySearchFilter.Trim();
        for (int i = 0; i < _dependencyTasks.Count; i++)
        {
            DependencyTaskDescriptor task = _dependencyTasks[i];
            if (!MatchesDependencyFilter(task, search))
                continue;

            bool selected = string.Equals(_selectedDependencyTaskId, task.Id, StringComparison.Ordinal);
            ImGui.PushID(i);

            if (ImGui.Selectable($"{task.DisplayName}##DepTaskItem", selected))
            {
                _selectedDependencyTaskId = task.Id;
                _dependencyValidationError = null;
            }

            if (!string.IsNullOrEmpty(task.StatusSummary))
            {
                ImGui.SameLine();
                Vector4 badgeColor = task.IsReady == true
                    ? new Vector4(0.32f, 0.88f, 0.44f, 1.0f)
                    : task.IsReady == false
                        ? new Vector4(1.0f, 0.65f, 0.25f, 1.0f)
                        : new Vector4(0.62f, 0.66f, 0.72f, 1.0f);
                ImGui.TextColored(badgeColor, $"[{task.StatusSummary}]");
            }

            ImGui.PopID();
        }
    }

    private static void DrawDependenciesTaskWorkspace()
    {
        DependencyTaskDescriptor? selected = FindSelectedDependencyTask();
        Vector2 avail = ImGui.GetContentRegionAvail();
        float detailsHeight = Math.Clamp(avail.Y * 0.48f, 220.0f, 380.0f);

        if (ImGui.BeginChild("DependencyTaskDetails", new Vector2(0.0f, detailsHeight), ImGuiChildFlags.Borders))
        {
            if (selected is null)
                ImGui.TextDisabled("Select a dependency script or build target on the left.");
            else
                DrawSelectedDependencyTaskDetails(selected);
        }
        ImGui.EndChild();

        ImGui.Spacing();

        if (ImGui.BeginChild("DependencyTaskConsole", Vector2.Zero, ImGuiChildFlags.Borders))
            DrawDependencyTaskConsole();
        ImGui.EndChild();
    }

    private static void DrawSelectedDependencyTaskDetails(DependencyTaskDescriptor task)
    {
        ImGui.TextUnformatted(task.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled($"({task.Category})");
        ImGui.TextDisabled(task.RelativePath);

        if (!string.IsNullOrWhiteSpace(task.Synopsis))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(task.Synopsis);
        }

        ImGui.Separator();

        Dictionary<string, string> paramValues = GetOrCreateDependencyParameterValues(task);
        string configuration = _dependencyBuildConfigurationIndex == 1 ? "Debug" : "Release";

        if (task.Kind is DependencyTaskKind.NativeBridgeBuild or DependencyTaskKind.ManagedBuild)
        {
            string[] configs = ["Release", "Debug"];
            ImGui.SetNextItemWidth(140.0f);
            ImGui.Combo("Configuration##DepBuildConfig", ref _dependencyBuildConfigurationIndex, configs, configs.Length);
        }

        for (int i = 0; i < task.Parameters.Length; i++)
        {
            DependencyScriptParameterDescriptor param = task.Parameters[i];
            paramValues.TryGetValue(param.Name, out string? currentValue);
            currentValue ??= param.DefaultValue;

            ImGui.PushID(i);
            if (param.Kind is DependencyScriptParameterKind.Switch or DependencyScriptParameterKind.Bool)
            {
                bool checkedVal = string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase) || currentValue == "1";
                if (ImGui.Checkbox(param.Name, ref checkedVal))
                    paramValues[param.Name] = checkedVal ? "true" : "false";
            }
            else if (param.AllowedValues.Length > 0)
            {
                int currentIdx = Array.FindIndex(param.AllowedValues, v => string.Equals(v, currentValue, StringComparison.OrdinalIgnoreCase));
                if (currentIdx < 0)
                    currentIdx = 0;
                ImGui.SetNextItemWidth(220.0f);
                if (ImGui.Combo(param.Name, ref currentIdx, param.AllowedValues, param.AllowedValues.Length))
                    paramValues[param.Name] = param.AllowedValues[currentIdx];
            }
            else if (param.Kind == DependencyScriptParameterKind.Int)
            {
                _ = int.TryParse(currentValue, out int intVal);
                ImGui.SetNextItemWidth(140.0f);
                if (ImGui.InputInt(param.Name, ref intVal))
                    paramValues[param.Name] = intVal.ToString();
            }
            else
            {
                string textVal = currentValue;
                ImGui.SetNextItemWidth(320.0f);
                if (ImGui.InputText(param.Name, ref textVal, 512u))
                    paramValues[param.Name] = textVal;
            }

            if (!string.IsNullOrWhiteSpace(param.Description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(param.Description);

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(320.0f);
        ImGui.InputTextWithHint("Extra Arguments##DepExtraArgs", "Optional extra CLI arguments", ref _dependencyExtraArgs, 512u);

        bool canBuildCommand = DependencyScriptCatalog.TryBuildCommandLine(
            _dependenciesRepoRoot!,
            task,
            paramValues,
            configuration,
            _dependencyExtraArgs,
            out string fileName,
            out string arguments,
            out string displayCommand,
            out string? commandError);

        ImGui.Spacing();
        if (canBuildCommand)
            ImGui.TextDisabled(displayCommand);
        else if (!string.IsNullOrWhiteSpace(commandError))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.30f, 1.0f), commandError);

        if (!string.IsNullOrWhiteSpace(_dependencyValidationError))
            ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.30f, 1.0f), _dependencyValidationError);

        ImGui.Spacing();
        bool running = _dependencyTaskRunner.IsRunning;
        using (new ImGuiDisabledScope(running || !canBuildCommand))
        {
            if (ImGui.Button("Run"))
            {
                _dependencyValidationError = null;
                string rootCapture = _dependenciesRepoRoot!;
                List<DependencyTaskDescriptor>? tasksCapture = _dependencyTasks;
                bool started = _dependencyTaskRunner.TryStart(
                    task.DisplayName,
                    rootCapture,
                    fileName,
                    arguments,
                    displayCommand,
                    onCompleted: () =>
                    {
                        if (tasksCapture is not null)
                            DependencyScriptCatalog.RefreshStatuses(rootCapture, tasksCapture);
                    });
                if (!started)
                    _dependencyValidationError = "Another dependency or build task is currently running.";
            }
        }

        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _dependencyTaskRunner.Cancel();
        }
    }

    private static void DrawDependencyTaskConsole()
    {
        bool running = _dependencyTaskRunner.IsRunning;
        int? exitCode = _dependencyTaskRunner.LastExitCode;

        ImGui.TextUnformatted("Output");
        ImGui.SameLine();
        if (running)
        {
            ImGui.TextColored(
                new Vector4(0.42f, 0.82f, 1.0f, 1.0f),
                $"Running '{_dependencyTaskRunner.ActiveTaskName}' ({_dependencyTaskRunner.ElapsedSeconds:F1} s)...");
        }
        else if (exitCode.HasValue)
        {
            Vector4 statusColor = exitCode.Value == 0
                ? new Vector4(0.32f, 0.88f, 0.44f, 1.0f)
                : new Vector4(1.0f, 0.38f, 0.30f, 1.0f);
            ImGui.TextColored(statusColor, $"Last exit code: {exitCode.Value} ({_dependencyTaskRunner.ElapsedSeconds:F1} s)");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##DepsLogClear"))
            _dependencyTaskRunner.ClearLog();

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##DepsLogCopy"))
        {
            _dependencyTaskRunner.CopyLogSnapshot(_dependencyLogSnapshot);
            var sb = new StringBuilder(_dependencyLogSnapshot.Count * 64);
            for (int i = 0; i < _dependencyLogSnapshot.Count; i++)
                sb.AppendLine(_dependencyLogSnapshot[i].Text);
            ImGui.SetClipboardText(sb.ToString());
        }

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _dependencyAutoScroll);
        ImGui.Separator();

        _dependencyTaskRunner.CopyLogSnapshot(_dependencyLogSnapshot);
        ImGui.BeginChild("DependencyLogScroll", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        for (int i = 0; i < _dependencyLogSnapshot.Count; i++)
        {
            DependencyTaskLogLine line = _dependencyLogSnapshot[i];
            if (line.IsCommand)
                ImGui.TextColored(new Vector4(0.45f, 0.85f, 1.0f, 1.0f), line.Text);
            else if (line.IsError)
                ImGui.TextColored(new Vector4(1.0f, 0.42f, 0.35f, 1.0f), line.Text);
            else
                ImGui.TextUnformatted(line.Text);
        }

        if (_dependencyAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 24.0f)
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();
    }

    private static void RefreshDependencyTasksCatalog()
    {
        _dependenciesRepoRoot ??= DependencyScriptCatalog.FindRepositoryRoot();
        if (string.IsNullOrWhiteSpace(_dependenciesRepoRoot))
        {
            _dependencyTasks = null;
            return;
        }

        _dependencyTasks = DependencyScriptCatalog.DiscoverTasks(_dependenciesRepoRoot);
        if (string.IsNullOrEmpty(_selectedDependencyTaskId) && _dependencyTasks.Count > 0)
            _selectedDependencyTaskId = _dependencyTasks[0].Id;
    }

    private static bool MatchesDependencyFilter(DependencyTaskDescriptor task, string search)
    {
        if (_dependencyCategoryFilter == 1 && task.Kind != DependencyTaskKind.DependencyScript)
            return false;
        if (_dependencyCategoryFilter == 2 && task.Kind != DependencyTaskKind.NativeBridgeBuild)
            return false;
        if (_dependencyCategoryFilter == 3 && task.Kind != DependencyTaskKind.ManagedBuild)
            return false;

        if (string.IsNullOrEmpty(search))
            return true;

        return task.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || task.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)
            || task.Synopsis.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static DependencyTaskDescriptor? FindSelectedDependencyTask()
    {
        if (_dependencyTasks is null)
            return null;

        for (int i = 0; i < _dependencyTasks.Count; i++)
        {
            if (string.Equals(_dependencyTasks[i].Id, _selectedDependencyTaskId, StringComparison.Ordinal))
                return _dependencyTasks[i];
        }

        return _dependencyTasks.Count > 0 ? _dependencyTasks[0] : null;
    }

    private static Dictionary<string, string> GetOrCreateDependencyParameterValues(DependencyTaskDescriptor task)
    {
        if (_dependencyParamValues.TryGetValue(task.Id, out Dictionary<string, string>? values))
            return values;

        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < task.Parameters.Length; i++)
            values[task.Parameters[i].Name] = task.Parameters[i].DefaultValue;
        _dependencyParamValues[task.Id] = values;
        return values;
    }
}
