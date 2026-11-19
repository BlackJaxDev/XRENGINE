using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Compilation;

namespace XREngine.UnityScriptHost;

internal static partial class Program
{
    private const long MaxProfileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private static async Task<int> RunCompilationCommandAsync(string command, Dictionary<string, string> options,
        string project, CancellationToken cancellationToken)
    {
        RejectUnexpectedOptions(options, "--project", "--profile", "--output", "--entry", "--root");
        string profilePath = Path.GetFullPath(RequiredOption(options, "--profile"));
        UnityCompilationProfile profile = ReadProfile(profilePath);
        string requestedOutput = RequiredOption(options, "--output");
        if (command == "graph")
            ValidateOutput(project, requestedOutput);

        UnitySourceInventory inventory = new UnitySourceInventoryBuilder().Inspect(project,
            options.GetValueOrDefault("--entry", string.Empty), options.GetValueOrDefault("--root", string.Empty), cancellationToken);
        UnityAssemblyGraph graph = new UnityAssemblyGraphBuilder().Build(project, inventory, profile);

        if (command == "graph")
        {
            string output = ValidateOutput(project, requestedOutput);
            WriteReport(output, graph, cancellationToken);
            Console.WriteLine($"Selected {graph.Assemblies.Length} assemblies. Graph report: {output}");
            Console.WriteLine("Scripts did not run.");
            return graph.Succeeded ? 0 : 3;
        }

        string directory = Path.GetFullPath(requestedOutput);
        var compiler = new UnityScriptCompiler();
        UnityCompilationResult result = command == "diagnose"
            ? await compiler.DiagnoseAsync(project, inventory, graph, profile, Path.GetDirectoryName(profilePath)!, directory, cancellationToken)
            : await compiler.CompileAsync(project, inventory, graph, profile, Path.GetDirectoryName(profilePath)!, directory, cancellationToken);
        WriteReport(Path.Combine(directory, "graph.json"), graph, cancellationToken);
        WriteReport(Path.Combine(directory, "result.json"), result, cancellationToken);
        WriteReport(Path.Combine(directory, "conversion-coverage.json"), result.ConversionCoverage, cancellationToken);
        Console.WriteLine(command == "diagnose"
            ? $"Collected source diagnostics without accepting artifacts. Report: {Path.Combine(directory, "result.json")}"
            : $"Compiled {result.Artifacts.Length} assemblies. Compilation report: {Path.Combine(directory, "result.json")}");
        Console.WriteLine("Scripts did not run.");
        Console.WriteLine("Conversion coverage records compilation blockers only. Native output has not been assessed.");
        return result.Succeeded ? 0 : 3;
    }

    private static UnityCompilationProfile ReadProfile(string profilePath)
    {
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(profilePath);
        using var stream = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxProfileBytes)
            throw new InvalidDataException("Compilation profile exceeds the byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Compilation profile changed during capture.");
        UnityCompilationProfile profile = JsonSerializer.Deserialize<UnityCompilationProfile>(bytes, ProfileJsonOptions)
            ?? throw new InvalidDataException("Compilation profile is empty.");
        if (profile.SchemaVersion != UnityCompilationProfile.CurrentSchemaVersion ||
            !IsProfileValue(profile.Name, 2048, required: true) ||
            !IsProfileValue(profile.UnityVersion, 128, required: true) ||
            !IsProfileValue(profile.TargetPlatform, 128, required: true) ||
            !IsProfileValue(profile.SdkVersion, 128, required: true) ||
            !IsProfileValue(profile.ReferencePackVersion, 128, required: true) ||
            !IsProfileValue(profile.LanguageVersion, 128, required: true) ||
            !IsProfileArray(profile.Defines, 4096, 2048) ||
            !IsProfileArray(profile.RootAssemblies, 4096, 2048) ||
            !IsProfileArray(profile.SourceReferenceAssemblies, 1024, 200) ||
            profile.References is null || profile.References.Length > 1024)
            throw new InvalidDataException("Compilation profile has invalid or unbounded values.");
        foreach (UnityCompilationReference reference in profile.References)
            if (reference is null || !IsProfileValue(reference.Path, 2048, required: true) ||
                !IsProfileValue(reference.Sha256, 64, required: true) ||
                !IsProfileValue(reference.AssemblyName, 2048, required: true) ||
                !IsProfileValue(reference.Role, 128, required: true))
                throw new InvalidDataException("Compilation profile has an invalid reference.");
        return profile;
    }

    private static bool IsProfileArray(string[]? values, int countLimit, int valueLimit)
        => values is not null && values.Length <= countLimit &&
            values.All(value => IsProfileValue(value, valueLimit, required: true));

    private static bool IsProfileValue(string? value, int limit, bool required)
        => value is not null && value.Length <= limit && (!required || !string.IsNullOrWhiteSpace(value));
}
