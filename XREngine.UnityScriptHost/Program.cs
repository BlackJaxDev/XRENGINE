using System.Text.Json;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Compilation;

namespace XREngine.UnityScriptHost;

/// <summary>Inspects Unity source and builds selected source assemblies without running scripts.</summary>
internal static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("Unity script host source inspection and compilation (no script execution).");
            Console.WriteLine("inspect --project <directory> --output <new-json-file> [--entry <Assets/path.unity>] [--root <name>]");
            Console.WriteLine("verify --project <directory> --inventory <json-file>");
            Console.WriteLine("graph --project <directory> --profile <json-file> --output <new-json-file> [--entry <Assets/path.unity>] [--root <name>]");
            Console.WriteLine("compile --project <directory> --profile <json-file> --output <new-directory> [--entry <Assets/path.unity>] [--root <name>]");
            Console.WriteLine("diagnose --project <directory> --profile <json-file> --output <new-directory> [--entry <Assets/path.unity>] [--root <name>]");
            Console.WriteLine("package --source <package-directory> --output <new-json-file>");
            Console.WriteLine("compose --project <directory> --lock <json-file> --output <new-directory>");
            Console.WriteLine("api-usage --assembly <dll> --sha256 <digest> --output <new-json-file> [--catalog <json-file>]");
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            Dictionary<string, string> options = ParseOptions(args);
            if (args[0] == "api-usage")
                return RunApiUsageCommand(options);
            if (args[0] is "package" or "compose")
                return RunPackageCommand(args[0], options, cancellation.Token);
            string project = Path.GetFullPath(RequiredOption(options, "--project"));
            if (args[0] == "inspect")
            {
                RejectUnexpectedOptions(options, "--project", "--output", "--entry", "--root");
                string output = ValidateOutput(project, RequiredOption(options, "--output"));
                UnitySourceInventory inventory = new UnitySourceInventoryBuilder().Inspect(project,
                    options.GetValueOrDefault("--entry", string.Empty), options.GetValueOrDefault("--root", string.Empty), cancellation.Token);
                WriteInventory(output, inventory, cancellation.Token);
                Console.WriteLine($"Inspected {inventory.Entries.Length} source entries, {inventory.Packages.Length} package manifests, and {inventory.AssemblyDefinitions.Length} assembly definitions.");
                Console.WriteLine($"Source content SHA-256: {inventory.SourceContentSha256}");
                Console.WriteLine($"Recorded {inventory.Diagnostics.Length} execution requirements ({inventory.SuppressedDiagnosticCount} additional diagnostics omitted). Scripts did not run; this report is not execution approval.");
                return 0;
            }

            if (args[0] == "verify")
            {
                RejectUnexpectedOptions(options, "--project", "--inventory");
                string inventoryPath = Path.GetFullPath(RequiredOption(options, "--inventory"));
                UnitySourceInventoryBuilder.EnsureNoReparsePoints(inventoryPath);
                using var stream = new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > UnityInventoryOutputStream.MaxBytes)
                    throw new InvalidDataException("Inventory exceeds the supported byte limit.");
                UnitySourceInventory expected = JsonSerializer.Deserialize<UnitySourceInventory>(stream)
                    ?? throw new InvalidDataException("Inventory is empty.");
                if (expected.SchemaVersion != UnitySourceInventory.CurrentSchemaVersion ||
                    expected.EntryPath is null || expected.RootName is null || expected.SourceContentSha256 is null ||
                    expected.SourceContentSha256.Length != 64)
                    throw new InvalidDataException("Inventory schema or source selection is invalid.");
                UnitySourceInventory actual = new UnitySourceInventoryBuilder().Inspect(project, expected.EntryPath, expected.RootName, cancellation.Token);
                if (!string.Equals(expected.SourceContentSha256, actual.SourceContentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Source content changed. The inventory must be regenerated; no scripts ran.");
                    return 2;
                }
                Console.WriteLine("Source content matches the inventory. This is not an execution grant or an immutable snapshot.");
                return 0;
            }

            if (args[0] is "graph" or "compile" or "diagnose")
                return await RunCompilationCommandAsync(args[0], options, project, cancellation.Token);

            throw new ArgumentException("Unknown command. Use --help for supported commands.");
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled. No scripts ran.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            JsonException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            Console.Error.WriteLine($"Unity script host failed: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Options must be unique name/value pairs.");
        }
        return options;
    }

    private static string RequiredOption(Dictionary<string, string> options, string name)
        => options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required option is missing: {name}.");

    private static void RejectUnexpectedOptions(Dictionary<string, string> options, params string[] allowed)
    {
        foreach (string name in options.Keys)
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown option: {name}.");
    }

    private static string ValidateOutput(string project, string value)
    {
        string output = Path.GetFullPath(value);
        string parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output directory is missing.");
        string sourceIdentity = UnitySourcePath.GetDirectoryIdentity(project);
        string outputIdentity = UnitySourcePath.GetDirectoryIdentity(parent);
        string projectPrefix = sourceIdentity + Path.DirectorySeparatorChar;
        if (string.Equals(outputIdentity, sourceIdentity, StringComparison.OrdinalIgnoreCase) || outputIdentity.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Write the inventory outside the source project.");
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException("The output already exists. Choose a new report filename.");
        return output;
    }

    private static void WriteInventory(string output, UnitySourceInventory inventory, CancellationToken cancellationToken)
        => WriteReport(output, inventory, cancellationToken);

    private static void WriteReport<T>(string output, T report, CancellationToken cancellationToken)
    {
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            UnitySourceInventoryBuilder.EnsureNoReparsePoints(Path.GetDirectoryName(output)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bounded = new UnityInventoryOutputStream(stream))
            {
                JsonSerializer.Serialize(bounded, report, JsonOptions);
                bounded.Flush();
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
