using System.Globalization;
using System.Text.RegularExpressions;
using XREngine.Animation;

namespace XREngine.Animation.Importers;

public static partial class AnimYamlImporter
{
    private static readonly Regex UnityObjectHeader = new(
        @"^---\s*!u!(?<class>\d+)\s*&(?<id>-?\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static void ResolveAdditiveReferencePose(
        string sourcePath,
        AnimationClip clip,
        ImportedAnimationImportManifestBuilder manifest,
        HashSet<string> activePaths)
    {
        ImportedHumanoidClipRootMotionSettings? settings = clip.ImportedHumanoidRootMotionSettings;
        if (settings?.HasAdditiveReferencePose != true)
            return;

        SourceAssetReference reference = settings.AdditiveReferencePoseClip;
        const string field = "m_AnimationClipSettings.m_AdditiveReferencePoseClip";
        if (reference.IsNull)
        {
            RecordFailure(manifest, field, "Additive reference pose is enabled but the Unity reference is null.");
            return;
        }
        string resolvedPath;
        string relativePath;
        if (string.IsNullOrWhiteSpace(reference.Guid))
        {
            resolvedPath = Path.GetFullPath(sourcePath);
            relativePath = Path.GetFileName(resolvedPath);
        }
        else
        {
            Dictionary<string, string> paths = ResolveSourceGuidPaths(
                sourcePath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { reference.Guid });
            if (!paths.TryGetValue(reference.Guid, out relativePath!))
            {
                RecordFailure(manifest, field, $"Additive reference pose GUID '{reference.Guid}' could not be resolved through a project .meta file.");
                return;
            }

            string? projectRoot = FindProjectRoot(sourcePath);
            if (projectRoot is null)
            {
                RecordFailure(manifest, field, "Additive reference pose project root could not be resolved from the source Assets directory.");
                return;
            }
            resolvedPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
        }

        settings.AdditiveReferencePoseClip = reference with { ResolvedAssetPath = relativePath };
        if (!resolvedPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
        {
            RecordFailure(manifest, field, $"Additive reference pose asset '{relativePath}' is not a supported .anim file.");
            return;
        }
        if (!TryGetStandaloneAnimationClipFileId(
                resolvedPath,
                out long standaloneFileId,
                out string headerDiagnostic)
            || standaloneFileId != reference.FileId)
        {
            RecordFailure(
                manifest,
                field,
                string.IsNullOrEmpty(headerDiagnostic)
                    ? $"Additive reference pose file '{relativePath}' contains AnimationClip fileID {standaloneFileId}, not requested fileID {reference.FileId}."
                    : headerDiagnostic);
            return;
        }

        if (Path.GetFullPath(sourcePath).Equals(resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryValidateReferenceTime(settings, clip, out string timeDiagnostic))
            {
                RecordFailure(manifest, field, timeDiagnostic);
                return;
            }
            clip.ImportedAdditiveReferencePoseClip = clip;
            return;
        }

        string identity = $"{resolvedPath}|{reference.FileId}";
        if (!activePaths.Add(identity))
        {
            RecordFailure(manifest, field, "Additive reference pose contains a cyclic clip reference.");
            return;
        }
        try
        {
            AnimationClip imported = ImportCore(resolvedPath, activePaths);
            if (imported.SourceImportManifest is { } childManifest
                && childManifest.TryGetBlockingDiagnostic(
                    allowRuntimeAdapters: true,
                    out string diagnostic))
            {
                RecordFailure(manifest, field, $"Referenced additive pose clip '{relativePath}' is not executable: {diagnostic}");
                return;
            }
            if (!TryValidateReferenceTime(settings, imported, out string timeDiagnostic))
            {
                RecordFailure(manifest, field, timeDiagnostic);
                return;
            }
            clip.ImportedAdditiveReferencePoseClip = imported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecordFailure(manifest, field, $"Referenced additive pose clip '{relativePath}' could not be imported: {ex.Message}");
        }
        finally { activePaths.Remove(identity); }
    }

    private static string? FindProjectRoot(string path)
    {
        DirectoryInfo? directory = new FileInfo(Path.GetFullPath(path)).Directory;
        while (directory is not null && !directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        return directory?.Parent?.FullName;
    }

    private static bool TryGetStandaloneAnimationClipFileId(
        string path,
        out long fileId,
        out string diagnostic)
    {
        fileId = 0L;
        diagnostic = string.Empty;
        Match[] animationClips;
        try
        {
            animationClips = UnityObjectHeader.Matches(File.ReadAllText(path))
                .Cast<Match>()
                .Where(static match => match.Groups["class"].Value == "74")
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Additive reference pose asset '{path}' could not be read: {ex.Message}";
            return false;
        }
        if (animationClips.Length != 1)
        {
            diagnostic =
                $"Additive reference pose asset '{path}' contains {animationClips.Length} AnimationClip documents; " +
                "only one standalone .anim AnimationClip is supported.";
            return false;
        }

        return long.TryParse(
            animationClips[0].Groups["id"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out fileId);
    }

    private static bool TryValidateReferenceTime(
        ImportedHumanoidClipRootMotionSettings settings,
        AnimationClip referenceClip,
        out string diagnostic)
    {
        if (float.IsFinite(settings.AdditiveReferencePoseTime)
            && settings.AdditiveReferencePoseTime >= 0.0f
            && settings.AdditiveReferencePoseTime <= referenceClip.LengthInSeconds)
        {
            diagnostic = string.Empty;
            return true;
        }

        diagnostic =
            $"Additive reference pose time {settings.AdditiveReferencePoseTime.ToString(CultureInfo.InvariantCulture)} seconds " +
            $"is outside referenced clip length {referenceClip.LengthInSeconds.ToString(CultureInfo.InvariantCulture)} seconds.";
        return false;
    }

    private static void RecordFailure(ImportedAnimationImportManifestBuilder manifest, string field, string diagnostic)
        => manifest.RecordSection(EImportedAnimationDataDomain.RootMotionSettings, EImportedAnimationCapabilityState.PreservedNotExecutable, field, diagnostic, diagnostic);
}
