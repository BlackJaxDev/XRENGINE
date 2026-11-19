using System.Text.Json;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Admission.PackageSources;

namespace XREngine.UnityScriptHost;

internal static partial class Program
{
    private static int RunPackageCommand(string command, Dictionary<string, string> options, CancellationToken cancellationToken)
    {
        if (command == "package")
        {
            RejectUnexpectedOptions(options, "--source", "--output");
            string source = Path.GetFullPath(RequiredOption(options, "--source"));
            string output = ValidateOutput(source, RequiredOption(options, "--output"));
            UnityPackageSourceInventory inventory = new UnityPackageSourceInspector().Inspect(source, cancellationToken);
            WriteReport(output, inventory, cancellationToken);
            Console.WriteLine($"Inspected package '{inventory.Name}' version '{inventory.Version}': {inventory.ContentSha256}.");
            return 0;
        }

        RejectUnexpectedOptions(options, "--project", "--lock", "--output");
        string project = Path.GetFullPath(RequiredOption(options, "--project"));
        string lockPath = Path.GetFullPath(RequiredOption(options, "--lock"));
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(lockPath);
        using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxProfileBytes)
            throw new InvalidDataException("The package source lock exceeds its byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The package source lock changed while it was read.");
        UnitySourceCompositionRequest request = JsonSerializer.Deserialize<UnitySourceCompositionRequest>(bytes, ProfileJsonOptions)
            ?? throw new InvalidDataException("The package source lock is empty.");
        string destination = Path.GetFullPath(RequiredOption(options, "--output"));
        UnitySourceInventory composed = new UnitySourcePackageResolver().Compose(project, request,
            Path.GetDirectoryName(lockPath)!, destination, cancellationToken);
        WriteReport(Path.Combine(destination, "source-inventory.json"), composed, cancellationToken);
        WriteReport(Path.Combine(destination, "composition.json"), request, cancellationToken);
        Console.WriteLine($"Composed {request.Packages.Length} pinned packages. Source digest: {composed.SourceContentSha256}.");
        Console.WriteLine("Scripts did not run. The composed source is not an execution grant.");
        return 0;
    }
}
