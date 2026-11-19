using System.Security.Cryptography;
using System.Text.Json;
using XREngine.UnityScriptHost.Admission;
using XREngine.UnityScriptHost.Analysis;
using XREngine.UnityScriptHost.Execution;

namespace XREngine.UnityScriptHost;

internal static partial class Program
{
    private static int RunApiUsageCommand(Dictionary<string, string> options)
    {
        RejectUnexpectedOptions(options, "--assembly", "--sha256", "--output", "--catalog");
        string assemblyPath = Path.GetFullPath(RequiredOption(options, "--assembly"));
        string expectedHash = RequiredOption(options, "--sha256");
        string output = ValidateOutput(Path.GetDirectoryName(assemblyPath)!, RequiredOption(options, "--output"));
        byte[] bytes = ReadAnalysisInput(assemblyPath, 64 * 1024 * 1024);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The inspected assembly does not match the requested SHA-256 digest.");

        var members = new Dictionary<string, UnityCapabilityStatus>(StringComparer.Ordinal);
        string catalogHash = string.Empty;
        if (options.TryGetValue("--catalog", out string? catalogPath))
        {
            string path = Path.GetFullPath(catalogPath);
            if (path.Equals(output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The catalog cannot be the report destination.");
            byte[] catalogBytes = ReadAnalysisInput(path, (int)MaxProfileBytes);
            catalogHash = Convert.ToHexStringLower(SHA256.HashData(catalogBytes));
            using JsonDocument document = JsonDocument.Parse(catalogBytes, new() { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The API catalog must map exact member keys to status names.");
            foreach (JsonProperty member in document.RootElement.EnumerateObject())
            {
                if (members.Count >= 4096 || string.IsNullOrWhiteSpace(member.Name) || member.Name.Length > 4096 ||
                    member.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("The API catalog has invalid or unbounded members.");
                UnityCapabilityStatus status = member.Value.GetString() switch
                {
                    "Implemented" => UnityCapabilityStatus.Implemented,
                    "CompileOnly" => UnityCapabilityStatus.CompileOnly,
                    "Unsupported" => UnityCapabilityStatus.Unsupported,
                    _ => throw new InvalidDataException("The API catalog has an unknown status name.")
                };
                if (!members.TryAdd(member.Name, status))
                    throw new InvalidDataException("The API catalog has duplicate member keys.");
            }
        }
        UnityApiUsageReport report = new UnityApiUsageAnalyzer().Analyze(bytes, new UnityCompatibilityCatalog(members))
            with { CatalogSha256 = catalogHash };
        WriteReport(output, report, CancellationToken.None);
        Console.WriteLine($"Inspected {report.Members.Length} external method and field references. API report: {output}");
        Console.WriteLine("Metadata only. Scripts did not run. Unlisted members have no declared authoring support; native conversion was not assessed.");
        return 0;
    }

    private static byte[] ReadAnalysisInput(string path, int maxBytes)
    {
        UnitySourceInventoryBuilder.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > maxBytes)
            throw new InvalidDataException("The analysis input is empty or exceeds its byte limit.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The analysis input changed during capture.");
        return bytes;
    }
}
