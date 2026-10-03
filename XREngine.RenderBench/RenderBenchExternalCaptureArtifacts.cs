using System.Security.Cryptography;
using XREngine.Rendering.Profiling;

namespace XREngine.RenderBench;

/// <summary>Bounded external GPU capture evidence attached from the current task run.</summary>
public sealed record RenderBenchExternalCaptureArtifacts(
    string ToolIdentity,
    bool Required,
    IReadOnlyList<RenderBenchExternalCaptureArtifact> Artifacts)
{
    public bool HasMissingArtifacts => Artifacts.Any(static artifact => artifact.Status == "missing");

    public static RenderBenchExternalCaptureArtifacts Attach(
        RenderProfileExternalCaptureConfiguration configuration,
        string profileRunDirectory)
    {
        configuration.Validate();
        if (!configuration.IsRequested)
            throw new InvalidOperationException("No external capture tool was selected.");

        string taskRoot = ResolveTaskRunRoot(profileRunDirectory);
        string destinationDirectory = Path.Combine(profileRunDirectory, "external-captures");
        Directory.CreateDirectory(destinationDirectory);
        List<RenderBenchExternalCaptureArtifact> artifacts = new(configuration.ArtifactPaths.Length);
        HashSet<string> selectedPaths = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < configuration.ArtifactPaths.Length; index++)
        {
            string requested = configuration.ArtifactPaths[index];
            string source = Path.GetFullPath(Path.IsPathRooted(requested)
                ? requested
                : Path.Combine(taskRoot, requested));
            if (!IsWithin(taskRoot, source))
                throw new ArgumentException("External capture source must stay within the current task run root.");
            if (!selectedPaths.Add(source))
                throw new ArgumentException("External capture artifact paths must be unique.");
            RejectReparsePoints(taskRoot, source);
            string relative = Path.GetRelativePath(taskRoot, source);
            if (!File.Exists(source))
            {
                artifacts.Add(new(relative, null, "missing", null, null));
                continue;
            }

            string destination = Path.Combine(destinationDirectory, $"{index:D2}-{Path.GetFileName(source)}");
            File.Copy(source, destination, overwrite: false);
            using FileStream copied = File.OpenRead(destination);
            string sha256 = Convert.ToHexString(SHA256.HashData(copied));
            artifacts.Add(new(relative, destination, "attached", copied.Length, sha256));
        }
        return new(configuration.ToolIdentity!, configuration.RequireArtifacts, artifacts);
    }

    private static string ResolveTaskRunRoot(string profileRunDirectory)
    {
        string validationRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Build", "_AgentValidation"));
        if ((File.GetAttributes(validationRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Path.GetDirectoryName(validationRoot)!) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("External capture validation root cannot be a reparse point.");
        string relative = Path.GetRelativePath(validationRoot, Path.GetFullPath(profileRunDirectory));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("External captures require output under Build/_AgentValidation/<task-run>.");
        string taskName = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.IsNullOrWhiteSpace(taskName) || taskName == "." ||
            taskName.Equals("00000000-000000-shared", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External captures require a dedicated task run directory.");
        return Path.Combine(validationRoot, taskName);
    }

    private static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            relative != ".";
    }

    private static void RejectReparsePoints(string taskRoot, string source)
    {
        string current = taskRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("External capture task root cannot be a reparse point.");
        string relative = Path.GetRelativePath(taskRoot, source);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("External capture source cannot traverse a reparse point.");
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
    }
}
