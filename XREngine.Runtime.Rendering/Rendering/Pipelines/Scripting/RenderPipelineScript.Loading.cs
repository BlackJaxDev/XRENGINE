using System.Reflection;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Pipelines.Overlays;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering;

// Loads parsed documents into pipelines.
public sealed partial class RenderPipelineScript
{
    internal static RenderPipelineScriptLoadResult LoadDocument(
        RenderPipelineScriptDocument document,
        string? sourcePath,
        Func<string, string?> readFile,
        HashSet<string> visited)
    {
        List<RenderPipelineScriptProblem> problems = [.. document.Problems];
        if (document.HasBlockingErrors)
            return new RenderPipelineScriptLoadResult(null, document, problems);

        RenderPipeline? pipeline = document switch
        {
            { ParentPath: { } parentPath } => LoadParent(document, parentPath, sourcePath, readFile, visited, problems),
            { BaseName: { } baseName } => CreateBase(document, baseName, problems),
            _ => CreateStandalone(document, problems),
        };

        if (pipeline is null)
            return new RenderPipelineScriptLoadResult(null, document, problems);

        if (document.IsDerived && !CheckDerivedContent(document, problems))
            return new RenderPipelineScriptLoadResult(null, document, problems);

        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        if (!ApplySettings(document, pipeline, problems, settings))
            return new RenderPipelineScriptLoadResult(null, document, problems);

        if (document.IsDerived && !AttachOverlay(document, pipeline, sourcePath, settings, problems))
            return new RenderPipelineScriptLoadResult(null, document, problems);

        return new RenderPipelineScriptLoadResult(pipeline, document, problems);
    }

    private static RenderPipeline? LoadParent(
        RenderPipelineScriptDocument document,
        string parentPath,
        string? sourcePath,
        Func<string, string?> readFile,
        HashSet<string> visited,
        List<RenderPipelineScriptProblem> problems)
    {
        string? directory = sourcePath is null ? null : Path.GetDirectoryName(sourcePath);
        string resolved = directory is null ? parentPath : Path.GetFullPath(Path.Combine(directory, parentPath));
        if (sourcePath is not null)
            visited.Add(Path.GetFullPath(sourcePath));

        if (!visited.Add(resolved))
        {
            AddError(problems, document.BaseLine, document.BaseColumn, $"The parent chain returns to '{parentPath}'.");
            return null;
        }

        string? text = readFile(resolved);
        if (text is null)
        {
            AddError(problems, document.BaseLine, document.BaseColumn, $"Cannot read the parent script '{parentPath}'.");
            return null;
        }

        RenderPipelineScriptLoadResult parent = LoadDocument(RenderPipelineScriptDocument.Parse(text), resolved, readFile, visited);
        string parentName = Path.GetFileName(resolved);
        for (int i = 0; i < parent.Problems.Count; i++)
        {
            RenderPipelineScriptProblem problem = parent.Problems[i];
            problems.Add(problem with { Message = string.Concat(parentName, ": ", problem.ToString()), Line = document.BaseLine, Column = document.BaseColumn });
        }

        return parent.Pipeline;
    }

    private static RenderPipeline? CreateBase(RenderPipelineScriptDocument document, string baseName, List<RenderPipelineScriptProblem> problems)
    {
        if (!RenderPipelineScriptBases.TryGet(baseName, out RenderPipelineScriptBase? entry) || entry is null)
        {
            AddError(problems, document.BaseLine, document.BaseColumn, $"Unknown base pipeline '{baseName}'. Known bases: {GetBaseNames()}.");
            return null;
        }

        object?[] arguments = entry.GetDefaultArguments();
        bool valid = true;
        bool namedSeen = false;
        for (int i = 0; i < document.BaseArguments.Count; i++)
        {
            ScriptArgument argument = document.BaseArguments[i];
            int index;
            if (argument.Name is null)
            {
                if (namedSeen)
                {
                    AddError(problems, argument.Line, argument.Column, "A positional argument cannot follow a named argument.");
                    valid = false;
                    continue;
                }

                index = i;
            }
            else
            {
                namedSeen = true;
                index = FindParameter(entry, argument.Name);
            }

            if (index < 0 || index >= entry.Parameters.Count)
            {
                string message = argument.Name is null
                    ? $"Base '{entry.Name}' takes {entry.Parameters.Count} arguments."
                    : $"Base '{entry.Name}' has no parameter '{argument.Name}'.";
                AddError(problems, argument.Line, argument.Column, message);
                valid = false;
                continue;
            }

            RenderPipelineScriptBaseParameter parameter = entry.Parameters[index];
            try
            {
                arguments[index] = ConvertAssignedValue(argument.Value, parameter.Type, parameter.Name, argument.Line, argument.Column);
            }
            catch (ScriptSyntaxException ex)
            {
                AddError(problems, ex.Line, ex.Column, ex.Reason);
                valid = false;
            }
        }

        return valid ? entry.Create(arguments) : null;
    }

    private static int FindParameter(RenderPipelineScriptBase entry, string name)
    {
        for (int i = 0; i < entry.Parameters.Count; i++)
        {
            if (string.Equals(entry.Parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string GetBaseNames()
    {
        IReadOnlyList<RenderPipelineScriptBase> bases = RenderPipelineScriptBases.All;
        string[] names = new string[bases.Count];
        for (int i = 0; i < names.Length; i++)
            names[i] = bases[i].Name;
        return string.Join(", ", names);
    }

    private static CustomRenderPipeline? CreateStandalone(RenderPipelineScriptDocument document, List<RenderPipelineScriptProblem> problems)
    {
        CustomRenderPipeline pipeline = new()
        {
            AuthoredResources = [.. document.Resources],
            AuthoredStages = [.. document.Stages],
            RenderPasses = new(document.RenderPasses),
            PassDependencies = [.. document.PassDependencies],
        };

        try
        {
            ViewportRenderCommandContainer commands = CompileContainer(pipeline, document.Steps, ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources);
            if (!document.HasPassesBlock)
                pipeline.RenderPasses = CollectScenePasses(commands);
            pipeline.Commands = commands;
        }
        catch (ScriptSyntaxException ex)
        {
            AddError(problems, ex.Line, ex.Column, ex.Reason);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AddError(problems, 0, 0, ex.Message);
            return null;
        }

        return pipeline;
    }

    /// <summary>
    /// Returns the scene passes that the commands render, each with no sorter. A script with no <c>passes</c> block
    /// uses them, so a command-only script renders the same passes as before the block existed.
    /// </summary>
    private static Dictionary<int, IComparer<RenderCommand>?> CollectScenePasses(ViewportRenderCommandContainer commands)
    {
        RenderPassMetadataCollection metadata = new();
        commands.BuildRenderPassMetadata(metadata);
        Dictionary<int, IComparer<RenderCommand>?> passes = [];
        foreach (RenderPassMetadata pass in metadata.Build())
            passes.TryAdd(pass.PassIndex, null);
        return passes;
    }

    /// <summary>
    /// Checks that a derived script holds settings and patches only. Plain commands and a passes block belong to a
    /// standalone script; a derived script changes the base commands with patch statements.
    /// </summary>
    private static bool CheckDerivedContent(RenderPipelineScriptDocument document, List<RenderPipelineScriptProblem> problems)
    {
        if (!document.HasCommands && !document.HasPassesBlock)
            return true;

        AddError(problems, document.BaseLine, document.BaseColumn,
            "A derived script holds settings, patch statements, and resource and schema patches only. Use after(...), replace(...), and the other patch statements to change the base commands.");
        return false;
    }

    /// <summary>
    /// Makes the overlay of a derived script and adds it to <paramref name="pipeline"/>. Each command body compiles
    /// here, so a body error reports its own line. A patch that does not apply is an error, and the base renders
    /// without the overlays.
    /// </summary>
    /// <returns>False when a command body does not compile.</returns>
    private static bool AttachOverlay(
        RenderPipelineScriptDocument document,
        RenderPipeline pipeline,
        string? sourcePath,
        Dictionary<string, string> settings,
        List<RenderPipelineScriptProblem> problems)
    {
        // A parent script always gets an overlay, so the writer keeps the parent path.
        if (!document.HasPatches && settings.Count == 0 && document.ParentPath is null)
            return true;

        RenderPipelineOverlay overlay = new()
        {
            Name = sourcePath is null ? "script" : Path.GetFileName(sourcePath),
            BaseTypeName = pipeline.GetType().FullName ?? pipeline.GetType().Name,
            ParentPath = document.ParentPath,
            ParentFullPath = document.ParentPath is null || sourcePath is null
                ? null
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, document.ParentPath)),
            BaseStructureHash = RenderPipelineStructureHash.TryParse(document.BaseHash, out ulong savedHash) ? savedHash : null,
            SettingOverrides = settings,
            ResourcePatches = [.. document.ResourcePatches],
            SchemaPatches = [.. document.SchemaPatches],
        };

        bool valid = true;
        for (int i = 0; i < document.CommandPatches.Count; i++)
        {
            RenderCommandPatch patch = document.CommandPatches[i];
            ScriptPatchLocation location = document.CommandPatchLocations[i];
            valid &= NormalizeBody(pipeline, patch.CommandText, location, problems, out string commandText);
            valid &= NormalizeBody(pipeline, patch.WrapScopeText, location, problems, out string scopeText);
            patch.CommandText = commandText;
            patch.WrapScopeText = scopeText.Length == 0 ? null : scopeText;
            overlay.CommandPatches.Add(patch);
        }

        if (!valid)
            return false;

        int layersBelow = pipeline.Overlays.Count;
        pipeline.Overlays = [.. pipeline.Overlays, overlay];
        try
        {
            pipeline.DescribeResourceLayout(Pipelines.Validation.RenderPipelineValidationContext.DefaultProfile);
        }
        catch (InvalidOperationException ex)
        {
            AddError(problems, document.BaseLine, document.BaseColumn, string.Concat("The resource layout with the overlay is invalid: ", ex.Message));
        }

        ReportPatchResults(document, pipeline.OverlayReport.CommandResults, layersBelow, problems);
        ReportPatchResults(document, pipeline.OverlayReport.ResourceResults, layersBelow, problems);
        ReportPatchResults(document, pipeline.OverlayReport.SchemaResults, layersBelow, problems);

        if (overlay.BaseStructureHash is { } expected && document.HasPatches)
        {
            ulong current = pipeline.ComputeBaseStructureHash(layersBelow);
            if (current != expected)
            {
                problems.Add(new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Warning, document.BaseLine, document.BaseColumn,
                    "The base changed after this script was saved. Review the rebase report in the Patches tab, then accept the rebase."));
            }
        }

        return true;
    }

    /// <summary>Compiles a patch body and returns its canonical text. A compile error reports the line in the script.</summary>
    private static bool NormalizeBody(RenderPipeline pipeline, string? body, ScriptPatchLocation location, List<RenderPipelineScriptProblem> problems, out string text)
    {
        text = body ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return true;

        try
        {
            text = Export(Compile(pipeline, body));
            return true;
        }
        catch (ScriptSyntaxException ex)
        {
            // Body lines count from the line of the opening brace.
            int line = ex.Line > 0 ? location.BodyLine + ex.Line - 1 : location.Line;
            AddError(problems, line, ex.Line > 0 ? ex.Column : location.Column, ex.Reason);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            AddError(problems, location.Line, location.Column, ex.Message);
            return false;
        }
    }

    private static void ReportPatchResults(
        RenderPipelineScriptDocument document,
        IReadOnlyList<RenderPipelinePatchResult> results,
        int overlayIndex,
        List<RenderPipelineScriptProblem> problems)
    {
        for (int i = 0; i < results.Count; i++)
        {
            RenderPipelinePatchResult result = results[i];
            if (result.OverlayIndex != overlayIndex)
                continue;

            ERenderPipelineScriptProblemSeverity? severity = result.Status switch
            {
                ERenderPipelinePatchStatus.AnchorMissing or ERenderPipelinePatchStatus.Failed => ERenderPipelineScriptProblemSeverity.Error,
                ERenderPipelinePatchStatus.AnchorMoved => ERenderPipelineScriptProblemSeverity.Warning,
                ERenderPipelinePatchStatus.Applied when result.Message.Length > 0 => ERenderPipelineScriptProblemSeverity.Warning,
                _ => null,
            };

            if (severity is null)
                continue;

            (int line, int column) = FindPatchLocation(document, result.Patch);
            problems.Add(new RenderPipelineScriptProblem(severity.Value, line, column, string.Concat(DescribePatch(result.Patch), ": ", result.Message)));
        }
    }

    private static (int Line, int Column) FindPatchLocation(RenderPipelineScriptDocument document, object patch)
    {
        int index = document.CommandPatches.FindIndex(p => ReferenceEquals(p, patch));
        if (index >= 0)
            return (document.CommandPatchLocations[index].Line, document.CommandPatchLocations[index].Column);
        index = document.ResourcePatches.FindIndex(p => ReferenceEquals(p, patch));
        if (index >= 0)
            return (document.ResourcePatchLocations[index].Line, document.ResourcePatchLocations[index].Column);
        index = document.SchemaPatches.FindIndex(p => ReferenceEquals(p, patch));
        if (index >= 0)
            return (document.SchemaPatchLocations[index].Line, document.SchemaPatchLocations[index].Column);
        return (document.BaseLine, document.BaseColumn);
    }

    private static string DescribePatch(object patch)
        => patch switch
        {
            RenderCommandPatch command => command.Describe(),
            RenderResourcePatch resource => resource.Describe(),
            PostProcessSchemaPatch schema => schema.Describe(),
            _ => patch.ToString() ?? string.Empty,
        };

    private static bool ApplySettings(RenderPipelineScriptDocument document, RenderPipeline pipeline, List<RenderPipelineScriptProblem> problems, Dictionary<string, string> applied)
    {
        bool valid = true;
        Type pipelineType = pipeline.GetType();
        for (int i = 0; i < document.Settings.Count; i++)
        {
            ScriptSetting setting = document.Settings[i];
            PropertyInfo? property = RenderPipelineScriptSettings.FindSetting(pipelineType, setting.Name);
            if (property is null)
            {
                AddError(problems, setting.Line, setting.Column, $"{pipelineType.Name} has no setting '{setting.Name}'.");
                valid = false;
                continue;
            }

            try
            {
                property.SetValue(pipeline, ConvertAssignedValue(setting.Value, property.PropertyType, setting.Name, setting.Line, setting.Column));
                if (FormatValueText(property.GetValue(pipeline)) is { } text)
                    applied[property.Name] = text;
            }
            catch (ScriptSyntaxException ex)
            {
                AddError(problems, ex.Line, ex.Column, ex.Reason);
                valid = false;
            }
            catch (TargetInvocationException ex)
            {
                AddError(problems, setting.Line, setting.Column, $"Setting '{setting.Name}' failed: {ex.InnerException?.Message ?? ex.Message}");
                valid = false;
            }
        }

        return valid;
    }

    private static void AddError(List<RenderPipelineScriptProblem> problems, int line, int column, string message)
        => problems.Add(new RenderPipelineScriptProblem(ERenderPipelineScriptProblemSeverity.Error, line, column, message));
}
