using XREngine.ShaderTranslation.Compilation;
using XREngine.ShaderTranslation.Diagnostics;
using XREngine.ShaderTranslation.Preprocessing;
using XREngine.ShaderTranslation.ShaderLab;

namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Translates a ShaderLab vertex/fragment shader into engine GLSL through Slang and SPIRV-Cross.
/// </summary>
/// <remarks>
/// The translator uses the first subshader that has a translatable pass. Unity also uses the first
/// subshader that the hardware supports. Each pass compiles one variant: the keywords that the
/// material enables, plus the keywords that the engine pass implies.
/// </remarks>
public sealed class ShaderTranslator(SlangSpirvCompiler compiler, SpirvCrossGlslEmitter crossCompiler)
{
    private readonly SlangSpirvCompiler _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    private readonly SpirvCrossGlslEmitter _crossCompiler = crossCompiler ?? throw new ArgumentNullException(nameof(crossCompiler));

    /// <summary>Reads, parses, and translates one <c>.shader</c> file.</summary>
    public async Task<ShaderTranslationResult> TranslateFileAsync(
        string shaderPath,
        ShaderTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        string text = await File.ReadAllTextAsync(shaderPath, cancellationToken).ConfigureAwait(false);
        ShaderLabDocument document;
        try
        {
            document = ShaderLabParser.Parse(text, shaderPath);
        }
        catch (ShaderLabParseException ex)
        {
            return new ShaderTranslationResult
            {
                Passes = [],
                Diagnostics =
                [
                    new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.ParseFailed, ShaderTranslationSeverity.Error,
                        ex.Message, ex.SourcePath, ex.Line),
                ],
            };
        }

        return await TranslateAsync(document, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Translates a parsed shader.</summary>
    public async Task<ShaderTranslationResult> TranslateAsync(
        ShaderLabDocument document,
        ShaderTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);

        List<ShaderTranslationDiagnostic> diagnostics = [];
        List<TranslatedPass> passes = [];
        ShaderLabSubShader? subShader = SelectSubShader(document);
        if (subShader is null)
        {
            ShaderLabSubShader? surface = document.SubShaders.FirstOrDefault(candidate => candidate.SurfaceProgram is not null);
            if (surface is not null)
            {
                diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.SurfaceShaderUnsupported,
                    ShaderTranslationSeverity.Error, "The shader is a surface shader (#pragma surface). Surface shader generation is not implemented yet.",
                    document.SourcePath, surface.SurfaceProgram!.FirstLine));
            }
            else
            {
                diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.NoTranslatablePass,
                    ShaderTranslationSeverity.Error, "The shader has no subshader with a vertex/fragment pass.", document.SourcePath));
            }

            return new ShaderTranslationResult { Document = document, Passes = passes, Diagnostics = diagnostics };
        }

        string jobRoot = Path.Combine(request.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobRoot);
        try
        {
            for (int index = 0; index < subShader.Passes.Count; index++)
            {
                ShaderLabPass pass = subShader.Passes[index];
                TranslatedPass? translated = await TranslatePassAsync(
                    document, subShader, pass, index, request, jobRoot, diagnostics, cancellationToken).ConfigureAwait(false);
                if (translated is not null)
                    passes.Add(translated);
            }
        }
        finally
        {
            if (!request.KeepWorkFiles)
                TryDeleteDirectory(jobRoot);
        }

        if (passes.Count == 0 && diagnostics.All(diagnostic => diagnostic.Severity != ShaderTranslationSeverity.Error))
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.NoTranslatablePass,
                ShaderTranslationSeverity.Error, "No pass of the selected subshader is drawn by the engine.", document.SourcePath, subShader.Line));
        }

        return new ShaderTranslationResult { Document = document, Passes = passes, Diagnostics = diagnostics };
    }

    private static ShaderLabSubShader? SelectSubShader(ShaderLabDocument document)
    {
        foreach (ShaderLabSubShader subShader in document.SubShaders)
            foreach (ShaderLabPass pass in subShader.Passes)
                if (pass.Kind == ShaderLabPassKind.Normal && pass.Program is not null)
                    return subShader;
        return null;
    }

    private async Task<TranslatedPass?> TranslatePassAsync(
        ShaderLabDocument document,
        ShaderLabSubShader subShader,
        ShaderLabPass pass,
        int index,
        ShaderTranslationRequest request,
        string jobRoot,
        List<ShaderTranslationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string label = pass.Name is null ? $"#{index}" : $"#{index} {pass.Name}";
        if (pass.Kind != ShaderLabPassKind.Normal)
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.PassReferenceUnsupported,
                ShaderTranslationSeverity.Warning, $"{pass.Kind}Pass '{pass.Name}' is not resolved yet; the pass is skipped.",
                document.SourcePath, pass.Line, label));
            return null;
        }

        string? lightMode = pass.LightMode ?? (subShader.Tags.TryGetValue("LightMode", out string? inherited) ? inherited : null);
        EnginePassKind? kind = ClassifyPass(lightMode);
        if (kind is null)
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.PassSkipped,
                ShaderTranslationSeverity.Info, $"LightMode '{lightMode}' is not drawn by the engine; the pass is skipped.",
                document.SourcePath, pass.Line, label));
            return null;
        }

        if (pass.Program is null)
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.PassSkipped,
                ShaderTranslationSeverity.Warning, "The pass has no program block (fixed-function state only); the pass is skipped.",
                document.SourcePath, pass.Line, label));
            return null;
        }

        if (pass.Program.Language == ShaderLabProgramLanguage.Glsl)
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.GlslProgramUnsupported,
                ShaderTranslationSeverity.Error, "GLSLPROGRAM blocks are not supported.", document.SourcePath, pass.Line, label));
            return null;
        }

        List<ShaderLabProgramBlock> includeBlocks = ShaderPassAssembler.GetIncludeBlocks(document, subShader, pass);
        ShaderProgramPragmas pragmas = ShaderProgramPragmas.Parse(
            includeBlocks.Select(block => (block.Code, block.FirstLine)).Append((pass.Program.Code, pass.Program.FirstLine)));
        string? vertexEntry = pragmas.GetEntryPoint(ShaderProgramStage.Vertex);
        string? fragmentEntry = pragmas.GetEntryPoint(ShaderProgramStage.Fragment);
        if (vertexEntry is null || fragmentEntry is null)
        {
            diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.MissingEntryPoint,
                ShaderTranslationSeverity.Error, "The pass needs both '#pragma vertex' and '#pragma fragment'.",
                document.SourcePath, pass.Program.FirstLine, label));
            return null;
        }

        HashSet<string> enabled = new(request.MaterialKeywords, StringComparer.Ordinal);
        foreach (string keyword in GetEngineKeywords(kind.Value))
            enabled.Add(keyword);

        string shaderDirectory = Path.GetDirectoryName(Path.GetFullPath(document.SourcePath))!;
        List<string> includeDirectories = [shaderDirectory, .. request.IncludeDirectories];
        ShaderIncludeExpander includeExpander = new(includeDirectories);
        List<TranslatedStage> stages = [];
        foreach ((ShaderProgramStage stage, string entryPoint) in OrderedStages(pragmas))
        {
            if (stage is not (ShaderProgramStage.Vertex or ShaderProgramStage.Fragment))
            {
                diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.AdditionalStage,
                    ShaderTranslationSeverity.Info, $"The pass uses a {stage.ToString().ToLowerInvariant()} stage ('{entryPoint}').",
                    document.SourcePath, pass.Program.FirstLine, label));
            }

            IReadOnlyList<string> keywords = ShaderVariantSelector.Select(pragmas, enabled, stage);
            string hlsl;
            GlobalWriteRewriteResult globals;
            try
            {
                string assembled = ShaderPassAssembler.Assemble(document, includeBlocks, pass.Program, stage, entryPoint, kind.Value, pragmas.Target, keywords);
                string expanded = includeExpander.Expand(assembled, shaderDirectory, document.SourcePath.Replace('\\', '/'));
                globals = GlobalWriteRewriter.Rewrite(StaticConstRewriter.Rewrite(SwitchSelectorRewriter.Rewrite(expanded)));
                hlsl = globals.Text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.CompileFailed,
                    ShaderTranslationSeverity.Error, $"{stage} stage '{entryPoint}': {ex.Message}", document.SourcePath, pass.Program.FirstLine, label));
                return null;
            }

            string stageDirectory = Path.Combine(jobRoot, $"pass{index}-{stage.ToString().ToLowerInvariant()}");
            Directory.CreateDirectory(stageDirectory);
            try
            {
                SlangSpirvOutput spirv = await _compiler.CompileAsync(
                    hlsl, $"pass{index}_{stage.ToString().ToLowerInvariant()}.hlsl", stage, entryPoint, includeDirectories,
                    stageDirectory, cancellationToken).ConfigureAwait(false);
                string crossGlsl = await _crossCompiler.EmitAsync(spirv.Spirv, stageDirectory, cancellationToken).ConfigureAwait(false);
                GlslPostProcessResult processed = GlslPostProcessor.Process(crossGlsl, spirv.Reflection, stage == ShaderProgramStage.Vertex, globals.RewrittenGlobals);
                foreach (string semantic in processed.UnsupportedVertexInputs)
                {
                    diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.UnsupportedVertexInput,
                        ShaderTranslationSeverity.Error, $"Vertex input '{semantic}' has no engine vertex attribute.",
                        document.SourcePath, pass.Program.FirstLine, label));
                }

                foreach (string note in processed.Notes)
                {
                    diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.BindingRewrite,
                        ShaderTranslationSeverity.Warning, note, document.SourcePath, pass.Program.FirstLine, label));
                }

                stages.Add(new TranslatedStage(stage, entryPoint, keywords, hlsl, spirv.Spirv, processed.Glsl));
            }
            catch (ShaderCompileException ex)
            {
                diagnostics.Add(new ShaderTranslationDiagnostic(ShaderTranslationDiagnosticCodes.CompileFailed,
                    ShaderTranslationSeverity.Error, $"{stage} stage '{entryPoint}': {ex.Message}", document.SourcePath, pass.Program.FirstLine, label));
                return null;
            }
        }

        Dictionary<string, string> tags = new(subShader.Tags, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> tag in pass.Tags)
            tags[tag.Key] = tag.Value;

        return new TranslatedPass
        {
            Index = index,
            Name = pass.Name,
            Kind = kind.Value,
            Commands = [.. subShader.Commands, .. pass.Commands],
            Tags = tags,
            Stages = stages,
        };
    }

    /// <summary>Returns the engine use of a light mode, or null when the engine does not draw it.</summary>
    public static EnginePassKind? ClassifyPass(string? lightMode)
        => lightMode?.ToUpperInvariant() switch
        {
            null or "" or "ALWAYS" => EnginePassKind.Unlit,
            "FORWARDBASE" => EnginePassKind.ForwardBase,
            "FORWARDADD" => EnginePassKind.ForwardAdd,
            "SHADOWCASTER" => EnginePassKind.ShadowCaster,
            _ => null,
        };

    /// <summary>Returns the keywords that the engine enables for a pass kind.</summary>
    public static IReadOnlyList<string> GetEngineKeywords(EnginePassKind kind)
        => kind switch
        {
            EnginePassKind.ForwardBase => ["DIRECTIONAL", "LIGHTPROBE_SH"],
            EnginePassKind.ForwardAdd => ["POINT"],
            EnginePassKind.ShadowCaster => ["SHADOWS_DEPTH"],
            _ => [],
        };

    private static IEnumerable<(ShaderProgramStage Stage, string EntryPoint)> OrderedStages(ShaderProgramPragmas pragmas)
    {
        foreach (ShaderProgramStage stage in Enum.GetValues<ShaderProgramStage>())
            if (pragmas.GetEntryPoint(stage) is string entryPoint)
                yield return (stage, entryPoint);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner or indexer can hold a job file briefly. A stale job folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
