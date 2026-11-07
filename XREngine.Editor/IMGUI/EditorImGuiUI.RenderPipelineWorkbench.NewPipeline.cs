using System.Numerics;
using ImGuiNET;
using XREngine.Editor.RenderPipelineWorkbench;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const string NewRenderPipelinePopup = "New Render Pipeline##WorkbenchNewPipeline";

    private static readonly string[] NewPipelineStepNames = ["1. Source", "2. Features", "3. Output", "4. Review"];

    private static NewRenderPipelineWizard? _newPipelineWizard;
    private static bool _newPipelineWizardRequested;

    /// <summary>Opens the new pipeline wizard. A code Workbench pipeline becomes the default base for derive and fork.</summary>
    private static void OpenNewRenderPipelineWizard(RenderPipeline? current)
    {
        NewRenderPipelineWizard wizard = new()
        {
            TemplateDirectory = RenderPipelineTemplates.DefaultDirectory,
            Profile = GetWorkbenchProfile(_workbench.Model.Instance),
        };

        RenderPipelineScriptBase? initialBase = null;
        if (current is not null && current is not CustomRenderPipeline)
            RenderPipelineScriptBases.TryGet(current.GetType(), out initialBase);
        if (initialBase is null && RenderPipelineScriptBases.All.Count > 0)
            initialBase = RenderPipelineScriptBases.All[0];
        wizard.SelectBase(initialBase);
        if (initialBase is not null && current is not null && initialBase.PipelineType == current.GetType())
        {
            object?[] arguments = initialBase.GetArguments(current);
            for (int i = 0; i < arguments.Length; i++)
                wizard.SetBaseArgument(i, arguments[i]);
        }

        _newPipelineWizard = wizard;
        _newPipelineWizardRequested = true;
    }

    /// <summary>Draws the new pipeline wizard popup: source, features, output, and review pages.</summary>
    private static void DrawNewRenderPipelineWizard()
    {
        if (_newPipelineWizardRequested)
        {
            _newPipelineWizardRequested = false;
            ImGui.OpenPopup(NewRenderPipelinePopup);
        }

        if (_newPipelineWizard is not { } wizard)
            return;

        ImGui.SetNextWindowSize(new Vector2(640f, 480f), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(NewRenderPipelinePopup, ref open))
        {
            if (!open)
                _newPipelineWizard = null;
            return;
        }

        DrawNewPipelineStepHeader(wizard.Step);
        ImGui.Separator();

        float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.BeginChild("##NewPipelineBody", new Vector2(0f, -footer), ImGuiChildFlags.None);
        switch (wizard.Step)
        {
            case ENewRenderPipelineWizardStep.Source:
                DrawNewPipelineSourcePage(wizard);
                break;
            case ENewRenderPipelineWizardStep.Features:
                DrawNewPipelineFeaturesPage(wizard);
                break;
            case ENewRenderPipelineWizardStep.Output:
                DrawNewPipelineOutputPage(wizard);
                break;
            case ENewRenderPipelineWizardStep.Review:
                DrawNewPipelineReviewPage(wizard);
                break;
        }
        ImGui.EndChild();

        ImGui.Separator();
        DrawNewPipelineFooter(wizard);
        ImGui.EndPopup();
    }

    private static void DrawNewPipelineStepHeader(ENewRenderPipelineWizardStep step)
    {
        string[] names = NewPipelineStepNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            if (i == (int)step)
                ImGui.TextUnformatted(names[i]);
            else
                ImGui.TextDisabled(names[i]);
        }
    }

    private static void DrawNewPipelineSourcePage(NewRenderPipelineWizard wizard)
    {
        DrawNewPipelineSourceOption(wizard, ENewRenderPipelineSource.Template, "Template", "Copy an .xrs template into a new standalone pipeline.");
        DrawNewPipelineSourceOption(wizard, ENewRenderPipelineSource.DeriveFromBase, "Derive from a code pipeline", "Make a derived script on a code pipeline type. The script holds settings only.");
        DrawNewPipelineSourceOption(wizard, ENewRenderPipelineSource.DeriveFromScript, "Derive from a script", "Make a derived script on a parent .xrs file.");
        DrawNewPipelineSourceOption(wizard, ENewRenderPipelineSource.Fork, "Fork of a code pipeline", "Copy the commands and the resource layout of a code pipeline into a standalone script. Code items become '// unconverted:' lines.");
        ImGui.Separator();

        switch (wizard.Source)
        {
            case ENewRenderPipelineSource.Template:
                for (int i = 0; i < RenderPipelineTemplates.All.Count; i++)
                {
                    RenderPipelineTemplate template = RenderPipelineTemplates.All[i];
                    if (ImGui.Selectable(template.DisplayName, ReferenceEquals(wizard.Template, template)))
                    {
                        wizard.Template = template;
                        wizard.RemovedStages.Clear();
                    }

                    ImGui.Indent();
                    ImGui.TextDisabled(template.Description);
                    ImGui.Unindent();
                }
                break;
            case ENewRenderPipelineSource.DeriveFromBase:
            case ENewRenderPipelineSource.Fork:
                DrawNewPipelineBaseCombo(wizard);
                break;
            case ENewRenderPipelineSource.DeriveFromScript:
                string parent = wizard.ParentScriptPath;
                if (ImGui.InputText("Parent script", ref parent, 1024))
                    wizard.ParentScriptPath = parent;
                ImGui.SameLine();
                if (ImGui.Button("Browse...##NewPipelineParent"))
                {
                    ImGuiFileBrowser.OpenFile("WorkbenchNewPipelineParent", "Select Parent Script", result =>
                    {
                        if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedPath))
                            wizard.ParentScriptPath = result.SelectedPath;
                    }, WorkbenchScriptFilter);
                }
                break;
        }
    }

    private static void DrawNewPipelineSourceOption(NewRenderPipelineWizard wizard, ENewRenderPipelineSource source, string label, string tooltip)
    {
        if (ImGui.RadioButton(label, wizard.Source == source))
            wizard.Source = source;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private static void DrawNewPipelineBaseCombo(NewRenderPipelineWizard wizard)
    {
        if (!ImGui.BeginCombo("Code pipeline", wizard.Base?.Name ?? "Select..."))
            return;

        IReadOnlyList<RenderPipelineScriptBase> bases = RenderPipelineScriptBases.All;
        for (int i = 0; i < bases.Count; i++)
        {
            RenderPipelineScriptBase entry = bases[i];
            if (ImGui.Selectable(entry.Name, ReferenceEquals(entry, wizard.Base)))
                wizard.SelectBase(entry);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.PipelineType.Name);
        }

        ImGui.EndCombo();
    }

    private static void DrawNewPipelineFeaturesPage(NewRenderPipelineWizard wizard)
    {
        switch (wizard.Source)
        {
            case ENewRenderPipelineSource.Template:
                if (wizard.AvailableStages.Count == 0)
                {
                    ImGui.TextDisabled("This template declares no post-process stages.");
                    break;
                }

                ImGui.TextUnformatted("Post-process stages");
                ImGui.TextDisabled("Clear a stage to remove it, its branches, and the resources that only it uses.");
                for (int i = 0; i < wizard.AvailableStages.Count; i++)
                {
                    string key = wizard.AvailableStages[i];
                    bool keep = !wizard.RemovedStages.Contains(key);
                    if (ImGui.Checkbox(key, ref keep))
                    {
                        if (keep)
                            wizard.RemovedStages.Remove(key);
                        else
                            wizard.RemovedStages.Add(key);
                    }
                }
                break;
            case ENewRenderPipelineSource.DeriveFromBase:
            case ENewRenderPipelineSource.Fork:
                DrawNewPipelineBaseArguments(wizard);
                break;
            default:
                ImGui.TextDisabled("A script derived from a parent script starts with the parent settings. Change them in the Workbench.");
                break;
        }
    }

    private static void DrawNewPipelineBaseArguments(NewRenderPipelineWizard wizard)
    {
        if (wizard.Base is not { } entry)
        {
            ImGui.TextDisabled("Select a code pipeline on the Source page.");
            return;
        }

        if (entry.Parameters.Count == 0)
        {
            ImGui.TextDisabled(string.Concat(entry.Name, " has no arguments."));
            return;
        }

        ImGui.TextUnformatted(string.Concat(entry.Name, " arguments"));
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            RenderPipelineScriptBaseParameter parameter = entry.Parameters[i];
            object? value = wizard.BaseArguments[i];
            if (parameter.Type == typeof(bool))
            {
                bool flag = value is true;
                if (ImGui.Checkbox(parameter.Name, ref flag))
                    wizard.SetBaseArgument(i, flag);
            }
            else if (parameter.Type.IsEnum)
            {
                string current = value?.ToString() ?? string.Empty;
                if (ImGui.BeginCombo(parameter.Name, current))
                {
                    string[] names = Enum.GetNames(parameter.Type);
                    for (int n = 0; n < names.Length; n++)
                    {
                        if (ImGui.Selectable(names[n], string.Equals(names[n], current, StringComparison.Ordinal)))
                            wizard.SetBaseArgument(i, Enum.Parse(parameter.Type, names[n]));
                    }

                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGui.TextDisabled(string.Concat(parameter.Name, " = ", value?.ToString() ?? "null"));
            }
        }
    }

    private static void DrawNewPipelineOutputPage(NewRenderPipelineWizard wizard)
    {
        string path = wizard.OutputPath;
        if (ImGui.InputText("Output file", ref path, 1024))
            wizard.OutputPath = path;
        ImGui.SameLine();
        if (ImGui.Button("Browse...##NewPipelineOutput"))
        {
            string directory = string.IsNullOrWhiteSpace(wizard.OutputPath)
                ? Engine.Assets.GameAssetsPath
                : Path.GetDirectoryName(Path.GetFullPath(wizard.OutputPath)) ?? Engine.Assets.GameAssetsPath;
            ImGuiFileBrowser.SaveFile("WorkbenchNewPipelineOutput", "New Render Pipeline Script", result =>
            {
                if (!result.Success || string.IsNullOrWhiteSpace(result.SelectedPath))
                    return;

                string selected = result.SelectedPath;
                if (!NewRenderPipelineWizard.IsScriptPath(selected))
                    selected = string.Concat(selected, RenderPipelineScriptLoader.FileExtension);
                wizard.OutputPath = selected;
            }, WorkbenchScriptFilter, directory, "NewRenderPipeline.xrs");
        }

        if (!string.IsNullOrWhiteSpace(wizard.OutputPath) && !NewRenderPipelineWizard.IsScriptPath(wizard.OutputPath))
            ImGui.TextColored(WorkbenchWarningColor, "The file name must end with .xrs.");

        if (NewRenderPipelineWizard.IsScriptPath(wizard.OutputPath) && File.Exists(wizard.OutputPath))
        {
            bool replace = wizard.ReplaceExistingFile;
            ImGui.TextColored(WorkbenchWarningColor, "The file exists.");
            if (ImGui.Checkbox("Replace the existing file", ref replace))
                wizard.ReplaceExistingFile = replace;
        }
    }

    private static void DrawNewPipelineReviewPage(NewRenderPipelineWizard wizard)
    {
        for (int i = 0; i < wizard.Problems.Count; i++)
        {
            RenderPipelineScriptProblem problem = wizard.Problems[i];
            ImGui.TextColored(problem.IsError ? WorkbenchErrorColor : WorkbenchWarningColor, problem.ToString());
        }

        for (int i = 0; i < wizard.ValidationProblems.Count; i++)
        {
            RenderPipelineProblem problem = wizard.ValidationProblems[i];
            ImGui.TextColored(problem.Severity == ERenderPipelineProblemSeverity.Error ? WorkbenchErrorColor : WorkbenchWarningColor, problem.Message);
        }

        if (wizard.Unconverted.Count > 0)
        {
            ImGui.TextColored(WorkbenchWarningColor, string.Concat(wizard.Unconverted.Count.ToString(), " items need work by hand. The file lists each one as an '// unconverted:' line."));
            if (ImGui.TreeNode("Unconverted items##NewPipelineUnconverted"))
            {
                for (int i = 0; i < wizard.Unconverted.Count; i++)
                    ImGui.TextUnformatted(wizard.Unconverted[i]);
                ImGui.TreePop();
            }
        }

        if (wizard.Problems.Count == 0 && wizard.ValidationProblems.Count == 0 && wizard.Text.Length > 0)
            ImGui.TextDisabled("The script loads and validates on OpenGL and Vulkan.");

        string text = wizard.Text;
        ImGui.InputTextMultiline("##NewPipelineText", ref text, (uint)Math.Max(1024, text.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);
    }

    private static void DrawNewPipelineFooter(NewRenderPipelineWizard wizard)
    {
        if (ImGui.Button("Cancel"))
        {
            _newPipelineWizard = null;
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(wizard.Step == ENewRenderPipelineWizardStep.Source);
        if (ImGui.Button("Back"))
            wizard.Step--;
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (wizard.Step != ENewRenderPipelineWizardStep.Review)
        {
            bool canAdvance = wizard.Step switch
            {
                ENewRenderPipelineWizardStep.Source => wizard.Source switch
                {
                    ENewRenderPipelineSource.DeriveFromBase or ENewRenderPipelineSource.Fork => wizard.Base is not null,
                    ENewRenderPipelineSource.DeriveFromScript => NewRenderPipelineWizard.IsScriptPath(wizard.ParentScriptPath),
                    _ => true,
                },
                ENewRenderPipelineWizardStep.Output => NewRenderPipelineWizard.IsScriptPath(wizard.OutputPath),
                _ => true,
            };

            ImGui.BeginDisabled(!canAdvance);
            if (ImGui.Button("Next"))
            {
                wizard.Step++;
                // The Features page lists the template stages, and the Review page shows the final text.
                if (wizard.Step is ENewRenderPipelineWizardStep.Features or ENewRenderPipelineWizardStep.Review)
                    wizard.Build();
            }
            ImGui.EndDisabled();
            return;
        }

        ImGui.BeginDisabled(!wizard.CanCreate);
        if (ImGui.Button("Create and Open"))
        {
            string? created = null;
            try
            {
                created = wizard.Create();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowWorkbenchScriptProblems(wizard.OutputPath, [new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, 0, 0, ex.Message)]);
            }

            if (created is not null)
            {
                _newPipelineWizard = null;
                ImGui.CloseCurrentPopup();
                OpenRenderPipelineScriptFile(created);
            }
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !wizard.CanCreate)
            ImGui.SetTooltip("Fix the load errors, select an .xrs output file, or allow the wizard to replace the existing file.");
    }
}
