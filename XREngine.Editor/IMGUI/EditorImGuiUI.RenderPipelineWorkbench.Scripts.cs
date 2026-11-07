using System.Text;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Editor.UI;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string WorkbenchScriptProblemsPopup = "Script problems##WorkbenchScriptProblems";
    private const string WorkbenchScriptFilter = "Render Pipeline Scripts|*.xrs|All Files|*.*";

    private static IReadOnlyList<RenderPipelineScriptProblem> _workbenchScriptProblems = [];
    private static string _workbenchScriptProblemsTitle = string.Empty;
    private static bool _workbenchScriptProblemsRequested;

    /// <summary>
    /// Loads the <c>.xrs</c> file at <paramref name="path"/> and opens it in the Workbench. An authored pipeline opens
    /// with a draft. A load with problems shows them with their lines and columns.
    /// </summary>
    public static void OpenRenderPipelineScriptFile(string path)
    {
        RenderPipelineScriptLoadResult result;
        try
        {
            result = RenderPipelineScriptLoader.LoadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowWorkbenchScriptProblems(path, [new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, 0, 0, ex.Message)]);
            _showRenderPipelineWorkbench = true;
            return;
        }

        if (result.Problems.Count > 0)
            ShowWorkbenchScriptProblems(path, result.Problems);

        if (result.Pipeline is not { } pipeline)
        {
            _showRenderPipelineWorkbench = true;
            return;
        }

        _workbench.SetScriptSource(pipeline, Path.GetFullPath(path));
        OpenRenderPipelineWorkbench(pipeline);
        if (pipeline is CustomRenderPipeline custom)
            _workbench.Authoring.BeginEdit(custom);
    }

    /// <summary>Draws the File menu: open, save, save as, and expanded export of scripts.</summary>
    private static void DrawWorkbenchFileMenu(RenderPipeline? pipeline)
    {
        if (!ImGui.BeginMenu("File"))
            return;

        if (ImGui.MenuItem("Open Script..."))
        {
            ImGuiFileBrowser.OpenFile("WorkbenchOpenScript", "Open Render Pipeline Script", result =>
            {
                if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
                    OpenRenderPipelineScriptFile(result.SelectedPath);
            }, WorkbenchScriptFilter);
        }

        ImGui.Separator();
        bool canWrite = pipeline is not null && RenderPipelineScriptWriter.CanWrite(pipeline);
        string? scriptPath = pipeline is null ? null : _workbench.GetScriptPath(pipeline);

        if (ImGui.MenuItem("Save", "Ctrl+S", false, canWrite))
        {
            if (scriptPath is null)
                OpenWorkbenchSaveScriptDialog(pipeline!, expanded: false);
            else
                SaveWorkbenchScript(pipeline!, scriptPath, expanded: false);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canWrite
                ? "Write the pipeline as an .xrs script. A code pipeline saves as a derived script with the settings that differ from its type defaults. An open draft saves with its edits."
                : "This pipeline type has no script base. Use Export Expanded or Fork.");
        }

        if (ImGui.MenuItem("Save As...", null, false, canWrite))
            OpenWorkbenchSaveScriptDialog(pipeline!, expanded: false);

        if (ImGui.MenuItem("Export Expanded...", null, false, pipeline is not null))
            OpenWorkbenchSaveScriptDialog(pipeline!, expanded: true);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Write a standalone script with the resource layout of the selected instance profile. Code items become '// unconverted:' lines.");

        ImGui.EndMenu();
    }

    /// <summary>Handles Ctrl+S in the Workbench window.</summary>
    private static void HandleWorkbenchScriptShortcuts(RenderPipeline pipeline)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput)
            return;
        if (!ImGui.GetIO().KeyCtrl || !ImGui.IsKeyPressed(ImGuiKey.S, false) || !RenderPipelineScriptWriter.CanWrite(pipeline))
            return;

        if (_workbench.GetScriptPath(pipeline) is { } path)
            SaveWorkbenchScript(pipeline, path, expanded: false);
        else
            OpenWorkbenchSaveScriptDialog(pipeline, expanded: false);
    }

    private static void OpenWorkbenchSaveScriptDialog(RenderPipeline pipeline, bool expanded)
    {
        string? current = _workbench.GetScriptPath(pipeline);
        string directory = current is null ? Environment.CurrentDirectory : Path.GetDirectoryName(current) ?? Environment.CurrentDirectory;
        string fileName = current is null || expanded
            ? string.Concat(pipeline.GetType().Name, expanded ? ".expanded" : string.Empty, RenderPipelineScriptLoader.FileExtension)
            : Path.GetFileName(current);

        ImGuiFileBrowser.SaveFile(expanded ? "WorkbenchExportScript" : "WorkbenchSaveScript", expanded ? "Export Expanded Script" : "Save Render Pipeline Script", result =>
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                return;

            string path = result.SelectedPath;
            if (!path.EndsWith(RenderPipelineScriptLoader.FileExtension, StringComparison.OrdinalIgnoreCase))
                path = string.Concat(path, RenderPipelineScriptLoader.FileExtension);
            SaveWorkbenchScript(pipeline, path, expanded);
        }, WorkbenchScriptFilter, directory, fileName);
    }

    /// <summary>
    /// Writes the script of <paramref name="pipeline"/> to <paramref name="path"/>. An open draft of the pipeline
    /// saves with its edits, applied or not.
    /// </summary>
    private static void SaveWorkbenchScript(RenderPipeline pipeline, string path, bool expanded)
    {
        string text;
        try
        {
            text = expanded
                ? RenderPipelineScriptWriter.WriteExpanded(pipeline, GetWorkbenchProfile(_workbench.Model.Instance))
                : RenderPipelineScriptWriter.Write(GetWorkbenchSavePipeline(pipeline));
        }
        catch (InvalidOperationException ex)
        {
            ShowWorkbenchScriptProblems(path, [new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, 0, 0, ex.Message)]);
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(fullPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (!expanded)
                _workbench.SetScriptSource(pipeline, fullPath);
            Debug.Out($"Saved render pipeline script to {fullPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowWorkbenchScriptProblems(path, [new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, 0, 0, ex.Message)]);
        }
    }

    /// <summary>Returns the pipeline to write: a copy of the open draft state, or the pipeline itself.</summary>
    private static RenderPipeline GetWorkbenchSavePipeline(RenderPipeline pipeline)
    {
        if (pipeline is not CustomRenderPipeline || !_workbench.Authoring.IsEditing(pipeline) || _workbench.Authoring.Draft is not { } draft)
            return pipeline;

        CustomRenderPipeline snapshot = new();
        draft.State.Clone().WriteTo(snapshot);
        return snapshot;
    }

    private static void ShowWorkbenchScriptProblems(string path, IReadOnlyList<RenderPipelineScriptProblem> problems)
    {
        _workbenchScriptProblems = problems;
        _workbenchScriptProblemsTitle = Path.GetFileName(path);
        _workbenchScriptProblemsRequested = true;
    }

    private static void DrawWorkbenchScriptProblemsPopup()
    {
        if (_workbenchScriptProblemsRequested)
        {
            _workbenchScriptProblemsRequested = false;
            ImGui.OpenPopup(WorkbenchScriptProblemsPopup);
        }

        if (!ImGui.BeginPopup(WorkbenchScriptProblemsPopup))
            return;

        ImGui.TextUnformatted(_workbenchScriptProblemsTitle);
        ImGui.Separator();
        for (int i = 0; i < _workbenchScriptProblems.Count; i++)
        {
            RenderPipelineScriptProblem problem = _workbenchScriptProblems[i];
            ImGui.TextColored(problem.IsError ? WorkbenchErrorColor : WorkbenchWarningColor, problem.ToString());
        }

        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
