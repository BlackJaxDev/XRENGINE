using Assimp;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XREngine.Fbx;

public static class FbxPhase0BaselineHarness
{
    public static int Run(string[] args)
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        string manifestPath = Path.Combine(workspaceRoot, FbxPhase0Decisions.CorpusManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string manifestDirectory = Path.GetDirectoryName(manifestPath) ?? throw new InvalidOperationException("Could not resolve the FBX manifest directory.");
        FbxCorpusManifest manifest = FbxCorpusManifest.Load(manifestPath);

        Console.WriteLine($"FBX phase 0 baseline report for {manifest.Entries.Count} manifest entries");
        foreach (FbxCorpusEntry entry in manifest.Entries)
        {
            if (entry.Availability != FbxCorpusAvailability.CheckedIn || !entry.ExpectedImportSuccess || string.IsNullOrWhiteSpace(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.ExpectedSummaryPath))
                continue;

            string assetPath = Path.Combine(workspaceRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string summaryPath = Path.Combine(manifestDirectory, entry.ExpectedSummaryPath.Replace('/', Path.DirectorySeparatorChar));

            Stopwatch stopwatch = Stopwatch.StartNew();
            FbxGoldenSummary summary = CreateSummary(entry, assetPath);
            stopwatch.Stop();

            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, FbxCorpusJson.SerializerOptions));

            Console.WriteLine($"- {entry.Id}: {summary.DetectedEncoding} {summary.DetectedVersionText ?? "unknown"}, nodes={summary.NodeCount}, meshes={summary.MeshCount}, materials={summary.MaterialCount}, animations={summary.AnimationCount}, bones={summary.BoneCount}, vertices={summary.TotalVertices}, faces={summary.TotalFaces}, depth={summary.MaxHierarchyDepth}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }

        Console.WriteLine($"Recommended baseline command: {FbxPhase0Decisions.BaselineHarnessCommand}");
        return 0;
    }

    private static FbxGoldenSummary CreateSummary(FbxCorpusEntry entry, string assetPath)
    {
        FileInfo fileInfo = new(assetPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"FBX corpus file '{assetPath}' does not exist.", assetPath);

        (FbxTransportEncoding encoding, string? versionText) = ReadHeader(assetPath);

        using AssimpContext context = new();
        Scene? scene = context.ImportFile(assetPath, PostProcessSteps.None);
        if (scene is null || scene.RootNode is null)
            throw new InvalidDataException($"Assimp did not return a valid scene for '{assetPath}'.");

        HashSet<string> uniqueBones = [];
        long totalVertices = 0;
        long totalFaces = 0;
        foreach (Mesh mesh in scene.Meshes)
        {
            totalVertices += mesh.VertexCount;
            totalFaces += mesh.FaceCount;
            foreach (Bone bone in mesh.Bones)
                uniqueBones.Add(bone.Name);
        }

        int maxDepth = 0;
        int nodeCount = CountNodes(scene.RootNode, 0, ref maxDepth);

        return new FbxGoldenSummary(
            entry.Id,
            ImportSucceeded: true,
            DetectedEncoding: encoding,
            DetectedVersionText: versionText,
            FileSizeBytes: fileInfo.Length,
            NodeCount: nodeCount,
            MeshCount: scene.MeshCount,
            MaterialCount: scene.MaterialCount,
            AnimationCount: scene.AnimationCount,
            BoneCount: uniqueBones.Count,
            TotalVertices: totalVertices,
            TotalFaces: totalFaces,
            MaxHierarchyDepth: maxDepth,
            Notes: entry.Notes);
    }

    private static int CountNodes(Node node, int depth, ref int maxDepth)
    {
        maxDepth = Math.Max(maxDepth, depth);
        int count = 1;
        for (int index = 0; index < node.ChildCount; index++)
            count += CountNodes(node.Children[index], depth + 1, ref maxDepth);
        return count;
    }

    private static (FbxTransportEncoding Encoding, string? VersionText) ReadHeader(string assetPath)
    {
        using FileStream stream = File.OpenRead(assetPath);
        byte[] header = GC.AllocateUninitializedArray<byte>(64);
        int bytesRead = stream.Read(header, 0, header.Length);
        ReadOnlySpan<byte> data = header.AsSpan(0, bytesRead);

        string asciiHeader = Encoding.ASCII.GetString(header, 0, bytesRead);
        if (asciiHeader.StartsWith("; FBX ", StringComparison.Ordinal))
        {
            int versionStart = 6;
            int versionEnd = asciiHeader.IndexOf(" project file", versionStart, StringComparison.Ordinal);
            string? versionText = versionEnd > versionStart ? asciiHeader[versionStart..versionEnd].Trim() : null;
            return (FbxTransportEncoding.Ascii, versionText);
        }

        ReadOnlySpan<byte> binaryMagic = "Kaydara FBX Binary  "u8;
        if (data.StartsWith(binaryMagic))
        {
            string? versionText = bytesRead >= 27
                ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(23, 4)).ToString(CultureInfo.InvariantCulture)
                : null;
            return (FbxTransportEncoding.Binary, versionText);
        }

        return (FbxTransportEncoding.Unknown, null);
    }

    private static string ResolveWorkspaceRoot()
        => TryFindWorkspaceRoot(Directory.GetCurrentDirectory())
        ?? TryFindWorkspaceRoot(AppContext.BaseDirectory)
        ?? throw new DirectoryNotFoundException("Could not locate the workspace root from the current directory or benchmark base directory.");

    private static string? TryFindWorkspaceRoot(string startPath)
    {
        DirectoryInfo? directory = new(startPath);
        if (!directory.Exists)
            return null;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}