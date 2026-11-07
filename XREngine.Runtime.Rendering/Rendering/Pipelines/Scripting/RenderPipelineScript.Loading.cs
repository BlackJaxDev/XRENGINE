using System.Reflection;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
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

        if (!ApplySettings(document, pipeline, problems))
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
    /// Checks that a derived script holds only settings. Commands, passes, resources, and schema changes of a derived
    /// pipeline are patch statements, which go in an overlay.
    /// </summary>
    private static bool CheckDerivedContent(RenderPipelineScriptDocument document, List<RenderPipelineScriptProblem> problems)
    {
        if (!document.HasCommands && !document.HasPassesBlock && !document.HasResourcesBlock && !document.HasSchemaBlock)
            return true;

        AddError(problems, document.BaseLine, document.BaseColumn,
            "A derived script holds only a settings block. Use patch statements to change the commands, passes, resources, or schema of the base.");
        return false;
    }

    private static bool ApplySettings(RenderPipelineScriptDocument document, RenderPipeline pipeline, List<RenderPipelineScriptProblem> problems)
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
