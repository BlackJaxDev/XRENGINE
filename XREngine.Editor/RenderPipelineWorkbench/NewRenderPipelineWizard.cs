using System.Text;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// The state and the build logic of the new pipeline wizard. The wizard makes the text of a new <c>.xrs</c> file from
/// a template, a code base, a parent script, or a fork, then loads that text again to report its problems. The ImGui
/// pages only edit the properties and call <see cref="Build"/> and <see cref="Create"/>.
/// </summary>
public sealed class NewRenderPipelineWizard
{
    private const int MaxPruneRounds = 4;

    private readonly List<string> _availableStages = [];
    private readonly List<string> _unconverted = [];
    private readonly List<RenderPipelineProblem> _validationProblems = [];
    private IReadOnlyList<RenderPipelineScriptProblem> _problems = [];

    /// <summary>Gets or sets the page that the wizard shows.</summary>
    public ENewRenderPipelineWizardStep Step { get; set; }

    /// <summary>Gets or sets where the new pipeline comes from.</summary>
    public ENewRenderPipelineSource Source { get; set; }

    /// <summary>Gets or sets the template that <see cref="ENewRenderPipelineSource.Template"/> copies.</summary>
    public RenderPipelineTemplate Template { get; set; } = RenderPipelineTemplates.MinimalForward;

    /// <summary>Gets or sets the folder that holds the template files.</summary>
    public string TemplateDirectory { get; set; } = string.Empty;

    /// <summary>Gets the code base of <see cref="ENewRenderPipelineSource.DeriveFromBase"/> and <see cref="ENewRenderPipelineSource.Fork"/>.</summary>
    public RenderPipelineScriptBase? Base { get; private set; }

    /// <summary>Gets the arguments for <see cref="Base"/>, one for each base parameter.</summary>
    public object?[] BaseArguments { get; private set; } = [];

    /// <summary>Gets or sets the parent script of <see cref="ENewRenderPipelineSource.DeriveFromScript"/>.</summary>
    public string ParentScriptPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the path of the new <c>.xrs</c> file.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether <see cref="Create"/> can replace an existing file.</summary>
    public bool ReplaceExistingFile { get; set; }

    /// <summary>Gets or sets the resource profile that a fork and the validator use.</summary>
    public RenderPipelineResourceProfile Profile { get; set; } = RenderPipelineValidationContext.DefaultProfile;

    /// <summary>Gets the stage keys that the template declares. The Features page lists them.</summary>
    public IReadOnlyList<string> AvailableStages => _availableStages;

    /// <summary>Gets the stage keys to remove from the template.</summary>
    public HashSet<string> RemovedStages { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the script text that <see cref="Build"/> made, or an empty string.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets the problems of the last <see cref="Build"/>: load problems of the template and of the new text.</summary>
    public IReadOnlyList<RenderPipelineScriptProblem> Problems => _problems;

    /// <summary>Gets the validator problems of the new pipeline. Only authored pipelines have them.</summary>
    public IReadOnlyList<RenderPipelineProblem> ValidationProblems => _validationProblems;

    /// <summary>Gets one line for each code item that a fork could not convert.</summary>
    public IReadOnlyList<string> Unconverted => _unconverted;

    /// <summary>
    /// Gets whether the last build has load errors. Validator problems do not block <see cref="Create"/>, because the
    /// user fixes them in the Workbench, and Apply checks them again.
    /// </summary>
    public bool HasErrors
    {
        get
        {
            for (int i = 0; i < _problems.Count; i++)
            {
                if (_problems[i].IsError)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Gets whether <see cref="Create"/> can write the file.</summary>
    public bool CanCreate
        => Text.Length > 0 &&
           !HasErrors &&
           IsScriptPath(OutputPath) &&
           (ReplaceExistingFile || !File.Exists(OutputPath));

    /// <summary>Selects <paramref name="entry"/> as the code base and resets its arguments to the defaults.</summary>
    public void SelectBase(RenderPipelineScriptBase? entry)
    {
        Base = entry;
        BaseArguments = entry?.GetDefaultArguments() ?? [];
    }

    /// <summary>Sets the argument at <paramref name="index"/> for <see cref="Base"/>.</summary>
    public void SetBaseArgument(int index, object? value)
    {
        if ((uint)index < (uint)BaseArguments.Length)
            BaseArguments[index] = value;
    }

    /// <summary>
    /// Makes <see cref="Text"/> from the current choices, then loads it again and validates it. The method collects
    /// each failure as a problem and does not throw for bad input.
    /// </summary>
    /// <returns>True when the build made text without errors.</returns>
    public bool Build()
    {
        Text = string.Empty;
        _problems = [];
        _unconverted.Clear();
        _validationProblems.Clear();

        try
        {
            Text = Source switch
            {
                ENewRenderPipelineSource.Template => BuildFromTemplate(),
                ENewRenderPipelineSource.DeriveFromBase => BuildDerivedFromBase(),
                ENewRenderPipelineSource.DeriveFromScript => BuildDerivedFromScript(),
                ENewRenderPipelineSource.Fork => BuildFork(),
                _ => string.Empty,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Text = string.Empty;
            AddProblem(ex.Message);
        }

        if (Text.Length > 0)
            CheckText();

        return Text.Length > 0 && !HasErrors;
    }

    /// <summary>Writes <see cref="Text"/> to <see cref="OutputPath"/>.</summary>
    /// <returns>The full path of the new file.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="CanCreate"/> is false.</exception>
    public string Create()
    {
        if (!CanCreate)
            throw new InvalidOperationException("The wizard has no valid script to write, or the output file exists.");

        string fullPath = Path.GetFullPath(OutputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    /// <summary>Returns true when <paramref name="path"/> is a non-empty path that ends with <c>.xrs</c>.</summary>
    public static bool IsScriptPath(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.EndsWith(RenderPipelineScriptLoader.FileExtension, StringComparison.OrdinalIgnoreCase);

    private string BuildFromTemplate()
    {
        RenderPipelineScriptLoadResult result = RenderPipelineTemplates.Load(Template, TemplateDirectory);
        if (result.Problems.Count > 0)
            AddProblems(result.Problems, string.Concat("Template ", Template.FileName, ": "));
        if (result.Pipeline is not CustomRenderPipeline template)
            return string.Empty;

        RenderPipelineDraftState state = RenderPipelineDraftState.From(template);
        _availableStages.Clear();
        for (int i = 0; i < state.Stages.Count; i++)
            _availableStages.Add(state.Stages[i].Key);

        RemovedStages.IntersectWith(_availableStages);
        if (RemovedStages.Count > 0)
            RemoveStages(state);

        CustomRenderPipeline pipeline = new();
        state.WriteTo(pipeline);
        return RenderPipelineScriptWriter.Write(pipeline);
    }

    private string BuildDerivedFromBase()
    {
        if (Base is null)
            throw new InvalidOperationException("Select a code base.");

        return RenderPipelineScriptWriter.Write(Base.Create(BaseArguments));
    }

    private string BuildDerivedFromScript()
    {
        if (!IsScriptPath(ParentScriptPath))
            throw new InvalidOperationException("Select a parent .xrs file.");

        string parent = Path.GetFullPath(ParentScriptPath);
        if (!File.Exists(parent))
            throw new IOException(string.Concat("The parent script '", parent, "' does not exist."));

        // A relative parent path keeps the pair of files portable when the user moves the folder.
        string? outputDirectory = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetDirectoryName(Path.GetFullPath(OutputPath));
        string reference = outputDirectory is null ? parent : Path.GetRelativePath(outputDirectory, parent);
        return RenderPipelineScriptWriter.WriteDerivedFromScript(reference);
    }

    private string BuildFork()
    {
        if (Base is null)
            throw new InvalidOperationException("Select a code base to fork.");

        RenderPipeline source = Base.Create(BaseArguments);
        return RenderPipelineScriptWriter.WriteExpanded(source, Profile, _unconverted);
    }

    /// <summary>Loads <see cref="Text"/> as the new file would load, and validates an authored result on each backend.</summary>
    private void CheckText()
    {
        string? sourcePath = string.IsNullOrWhiteSpace(OutputPath) ? null : Path.GetFullPath(OutputPath);
        RenderPipelineScriptLoadResult result = RenderPipelineScriptLoader.Load(Text, sourcePath);
        if (result.Problems.Count > 0)
            AddProblems(result.Problems, null, result.Document.UnconvertedItems);

        if (result.Pipeline is not CustomRenderPipeline custom || custom.Commands is null)
            return;

        RenderPipelineDraftState state = RenderPipelineDraftState.From(custom);
        RenderPipelineDraftPreview preview = RenderPipelineDraftPreview.Build(state, 0, Profile);
        AddValidation(preview.Validate(custom, RendererBackendId.OpenGL, Profile), "OpenGL");
        AddValidation(preview.Validate(custom, RendererBackendId.Vulkan, Profile), "Vulkan");
    }

    private void AddValidation(RenderPipelineValidationReport report, string backend)
    {
        for (int i = 0; i < report.Problems.Count; i++)
        {
            RenderPipelineProblem problem = report.Problems[i];
            if (problem.Severity == ERenderPipelineProblemSeverity.Info)
                continue;

            _validationProblems.Add(problem with { Message = string.Concat(backend, ": ", problem.Message) });
        }
    }

    /// <summary>
    /// Removes <see cref="RemovedStages"/> from <paramref name="state"/>, then removes the resources that only the
    /// removed commands used. A resource that lost all of its producers loses its optional reads too, such as the
    /// bloom input of the tonemap pass. A resource that no pass used before the removal stays, because the template
    /// author declared it on purpose.
    /// </summary>
    private void RemoveStages(RenderPipelineDraftState state)
    {
        RenderPipelineResourceSnapshot before = BuildResourceSnapshot(state);
        foreach (string key in RemovedStages)
            RenderPipelineDraftStageEdits.RemoveStage(state, key);

        RenderPipelineResourceSnapshot after = BuildResourceSnapshot(state);
        for (int i = 0; i < state.Resources.Count; i++)
        {
            string name = state.Resources[i].Name;
            if (before.TryGet(name, out RenderPipelineResourceEntry? old) && old!.Producers.Count > 0 &&
                after.TryGet(name, out RenderPipelineResourceEntry? current) && current!.Producers.Count == 0)
            {
                RenderPipelineDraftResourceEdits.ClearOptionalReferences(state, name);
            }
        }

        for (int round = 0; round < MaxPruneRounds; round++)
        {
            after = BuildResourceSnapshot(state);
            int removed = state.Resources.RemoveAll(r => IsUnused(after, r.Name) && !IsUnused(before, r.Name));
            if (removed == 0)
                break;
        }
    }

    private RenderPipelineResourceSnapshot BuildResourceSnapshot(RenderPipelineDraftState state)
        => RenderPipelineDraftPreview.Build(state, 0, Profile).Resources;

    private static bool IsUnused(RenderPipelineResourceSnapshot snapshot, string name)
        => snapshot.TryGet(name, out RenderPipelineResourceEntry? entry) && entry!.IsDeclaredButUnused;

    private void AddProblem(string message)
        => _problems = [.. _problems, new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, 0, 0, message)];

    /// <summary>Adds <paramref name="problems"/>, except the items in <paramref name="skip"/>. <see cref="Unconverted"/> lists those.</summary>
    private void AddProblems(IReadOnlyList<RenderPipelineScriptProblem> problems, string? prefix, IReadOnlyList<RenderPipelineScriptProblem>? skip = null)
    {
        List<RenderPipelineScriptProblem> merged = [.. _problems];
        for (int i = 0; i < problems.Count; i++)
        {
            RenderPipelineScriptProblem problem = problems[i];
            if (skip is not null && Contains(skip, problem))
                continue;

            merged.Add(prefix is null ? problem : problem with { Message = string.Concat(prefix, problem.Message) });
        }

        _problems = merged;
    }

    private static bool Contains(IReadOnlyList<RenderPipelineScriptProblem> problems, RenderPipelineScriptProblem problem)
    {
        for (int i = 0; i < problems.Count; i++)
        {
            if (problems[i] == problem)
                return true;
        }

        return false;
    }
}
