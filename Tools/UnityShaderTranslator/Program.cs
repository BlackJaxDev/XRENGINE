using System.Diagnostics;
using System.Text;
using XREngine.ShaderTranslation.Compilation;
using XREngine.ShaderTranslation.Diagnostics;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.Tools.UnityShaderTranslator;

/// <summary>
/// Translates Unity <c>.shader</c> files to engine GLSL and reports which passes compile.
/// </summary>
/// <remarks>
/// Use it to measure translation coverage on a Unity project. It writes the GLSL, SPIR-V, and
/// assembled HLSL of each pass under the output folder, plus <c>report.md</c>.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options? options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("""
                Usage: UnityShaderTranslator (--shader <file> | --scan <folder>)... --out <folder>
                         [--project <unity-project-root>] [--compat <folder>] [--keywords A,B] [--keep]

                --shader    Translate one .shader file. Repeat for more files.
                --scan      Translate every .shader file under a folder.
                --out       Write outputs and report.md to this folder.
                --project   Add a Unity project root to the include search path.
                --compat    The compatibility include folder. Default: Build/CommonAssets/Shaders/UnityCompat
                            under the repository that contains this tool.
                --keywords  Material keywords to enable, separated by commas.
                --keep      Keep compiler job files under <out>/jobs.

                slangc and spirv-cross come from XRE_SLANGC and XRE_SPIRV_CROSS, the Vulkan SDK, or PATH.
                """);
            return 2;
        }

        SlangSpirvCompiler? compiler = SlangSpirvCompiler.FindDefault();
        SpirvCrossGlslEmitter? crossCompiler = SpirvCrossGlslEmitter.FindDefault();
        if (compiler is null || crossCompiler is null)
        {
            Console.Error.WriteLine($"{ShaderTranslationDiagnosticCodes.ToolUnavailable}: slangc={compiler?.Tool.Path ?? "missing"}, spirv-cross={crossCompiler?.Tool.Path ?? "missing"}.");
            return 3;
        }

        string compat = options.CompatDirectory ?? FindCompatDirectory();
        Directory.CreateDirectory(options.OutputDirectory);
        List<string> includeDirectories = [compat];
        if (options.ProjectRoot is not null)
            includeDirectories.Add(options.ProjectRoot);

        ShaderTranslator translator = new(compiler, crossCompiler);
        ShaderTranslationRequest request = new()
        {
            MaterialKeywords = options.Keywords,
            IncludeDirectories = includeDirectories,
            WorkDirectory = Path.Combine(options.OutputDirectory, "jobs"),
            KeepWorkFiles = options.Keep,
        };

        StringBuilder report = new();
        report.AppendLine("# Shader translation report").AppendLine();
        report.AppendLine($"Compiler: `{compiler.Tool.Path}`  ").AppendLine($"Cross-compiler: `{crossCompiler.Tool.Path}`  ");
        report.AppendLine($"Compatibility includes: `{compat}`").AppendLine();
        report.AppendLine("| Shader | Result | Passes | Errors | Warnings | Seconds |").AppendLine("| --- | --- | --- | --- | --- | --- |");
        StringBuilder details = new();
        int succeeded = 0;
        List<string> shaders = options.CollectShaders();
        for (int index = 0; index < shaders.Count; index++)
        {
            string shader = shaders[index];
            Stopwatch stopwatch = Stopwatch.StartNew();
            ShaderTranslationResult result = await translator.TranslateFileAsync(shader, request).ConfigureAwait(false);
            stopwatch.Stop();
            string name = result.Document?.Name ?? Path.GetFileNameWithoutExtension(shader);
            string folder = Path.Combine(options.OutputDirectory, $"{index:D3}-{Sanitize(Path.GetFileNameWithoutExtension(shader))}");
            WriteOutputs(folder, result);
            int errors = result.Diagnostics.Count(diagnostic => diagnostic.Severity == ShaderTranslationSeverity.Error);
            int warnings = result.Diagnostics.Count(diagnostic => diagnostic.Severity == ShaderTranslationSeverity.Warning);
            if (result.Succeeded)
                succeeded++;
            report.AppendLine($"| {Escape(name)} | {(result.Succeeded ? "ok" : "failed")} | {result.Passes.Count} | {errors} | {warnings} | {stopwatch.Elapsed.TotalSeconds:F1} |");
            Console.WriteLine($"[{index + 1}/{shaders.Count}] {(result.Succeeded ? "ok    " : "FAILED")} {name} ({result.Passes.Count} passes, {errors} errors)");
            if (result.Diagnostics.Count > 0)
            {
                details.AppendLine($"## {Escape(name)}").AppendLine().AppendLine($"`{shader}`").AppendLine().AppendLine("```text");
                foreach (ShaderTranslationDiagnostic diagnostic in result.Diagnostics)
                    details.AppendLine(Truncate(diagnostic.ToString(), 4000));
                details.AppendLine("```").AppendLine();
            }
        }

        report.AppendLine().AppendLine($"Translated {succeeded} of {shaders.Count} shaders without errors.").AppendLine();
        report.Append(details);
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "report.md"), report.ToString()).ConfigureAwait(false);
        Console.WriteLine($"Translated {succeeded} of {shaders.Count} shaders without errors. Report: {Path.Combine(options.OutputDirectory, "report.md")}");
        return succeeded == shaders.Count ? 0 : 1;
    }

    private static void WriteOutputs(string folder, ShaderTranslationResult result)
    {
        Directory.CreateDirectory(folder);
        foreach (TranslatedPass pass in result.Passes)
        {
            foreach (TranslatedStage stage in pass.Stages)
            {
                string stem = Path.Combine(folder, $"pass{pass.Index}-{pass.Kind}-{stage.Stage}".ToLowerInvariant());
                File.WriteAllText(stem + ".glsl", stage.Glsl);
                File.WriteAllText(stem + ".hlsl", stage.Hlsl);
                File.WriteAllBytes(stem + ".spv", stage.Spirv);
            }
        }
    }

    private static string FindCompatDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Build", "CommonAssets", "Shaders", "UnityCompat");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Build/CommonAssets/Shaders/UnityCompat. Pass --compat.");
    }

    private static string Sanitize(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (char c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return builder.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + " ...";
}
