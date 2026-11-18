using XREngine.ShaderTranslation.Translation;

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Compiles one assembled HLSL stage to SPIR-V with the Slang command-line compiler.
/// </summary>
/// <remarks>
/// Matrices use the row-major layout option. With it, an HLSL <c>mul(M, v)</c> becomes <c>v * M</c> in SPIR-V,
/// and the compatibility includes transpose engine matrices to keep column-vector math correct.
/// </remarks>
public sealed class SlangSpirvCompiler(ExternalTool slangc)
{
    /// <summary>The environment variable that can name the <c>slangc</c> executable.</summary>
    public const string OverrideVariable = "XRE_SLANGC";

    /// <summary>Gets the compiler tool.</summary>
    public ExternalTool Tool { get; } = slangc ?? throw new ArgumentNullException(nameof(slangc));

    /// <summary>Finds <c>slangc</c>, or returns null when it is not installed.</summary>
    public static SlangSpirvCompiler? FindDefault()
        => ExternalTool.Find("slangc", OverrideVariable) is ExternalTool tool ? new SlangSpirvCompiler(tool) : null;

    /// <summary>Compiles one stage.</summary>
    /// <param name="source">The assembled translation unit text.</param>
    /// <param name="fileName">The translation unit file name. It must end in <c>.hlsl</c>.</param>
    /// <param name="stage">The stage to compile.</param>
    /// <param name="entryPoint">The entry point function name.</param>
    /// <param name="includeDirectories">The include search directories, in priority order.</param>
    /// <param name="workDirectory">An empty directory that this call can write to.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <exception cref="ShaderCompileException">The compiler rejected the source.</exception>
    public async Task<SlangSpirvOutput> CompileAsync(
        string source,
        string fileName,
        ShaderProgramStage stage,
        string entryPoint,
        IReadOnlyList<string> includeDirectories,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.Combine(workDirectory, fileName);
        string spirvPath = Path.ChangeExtension(sourcePath, ".spv");
        string reflectionPath = Path.ChangeExtension(sourcePath, ".reflection.json");
        await File.WriteAllTextAsync(sourcePath, source, cancellationToken).ConfigureAwait(false);

        List<string> arguments =
        [
            sourcePath,
            "-target", "spirv",
            "-profile", "spirv_1_5",
            "-entry", entryPoint,
            "-stage", ToSlangStage(stage),
            "-matrix-layout-row-major",
            // Unity programs declare material properties as plain globals.
            "-warnings-disable", "39019",
            "-o", spirvPath,
            "-reflection-json", reflectionPath,
        ];
        foreach (string directory in includeDirectories)
        {
            arguments.Add("-I");
            arguments.Add(directory);
        }

        ExternalToolResult result = await Tool.RunAsync(arguments, workDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(spirvPath))
            throw new ShaderCompileException("slangc", result.CombinedOutput);

        byte[] spirv = await File.ReadAllBytesAsync(spirvPath, cancellationToken).ConfigureAwait(false);
        string reflectionJson = await File.ReadAllTextAsync(reflectionPath, cancellationToken).ConfigureAwait(false);
        return new SlangSpirvOutput(spirv, SlangStageReflection.Parse(reflectionJson), result.CombinedOutput);
    }

    private static string ToSlangStage(ShaderProgramStage stage)
        => stage switch
        {
            ShaderProgramStage.Vertex => "vertex",
            ShaderProgramStage.Hull => "hull",
            ShaderProgramStage.Domain => "domain",
            ShaderProgramStage.Geometry => "geometry",
            ShaderProgramStage.Fragment => "fragment",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
