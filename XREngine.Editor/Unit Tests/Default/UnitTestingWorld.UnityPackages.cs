using System.Collections;
using System.Diagnostics;

namespace XREngine.Editor;

public static partial class EditorUnitTests
{
    public static partial class Models
    {
        private static bool IsUnityPackagePath(string path)
            => string.Equals(Path.GetExtension(path), ".unitypackage", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Extracts a Unity package on a worker before any source hierarchy is converted.
        /// Each scene and prefab uses the complete extracted project for GUID resolution.
        /// </summary>
        private static void ScheduleUnityPackageExtraction(
            string packagePath,
            Settings.ModelImportSettings settings,
            string desktopDir,
            Action<string, List<string>> onFinished,
            Action<Exception> onError)
        {
            string? projectRoot = null;
            List<string> entries = [];
            long lastProgressTimestamp = 0;

            void ReportProgress(float progress, string message)
            {
                long now = Stopwatch.GetTimestamp();
                if (progress < 1.0f && now - lastProgressTimestamp < Stopwatch.Frequency)
                    return;

                lastProgressTimestamp = now;
                Debug.Meshes($"[UnityPackage] {Path.GetFileName(packagePath)}: {progress:P0} {message}");
            }

            IEnumerable ExtractRoutine()
            {
                string cacheRoot = Engine.Assets.GameCachePath
                    ?? throw new InvalidOperationException("Unity package import requires an asset cache directory.");
                List<string> dependencies = UnityPackageDependencyDownloader.Resolve(
                    settings.Dependencies ?? [],
                    cacheRoot,
                    path => ResolveUnityDependencyPath(desktopDir, path),
                    ReportProgress);
                UnityPackageImportProject project = UnityPackageImportSource.PrepareProject(
                    packagePath, cacheRoot, dependencies, ReportProgress);
                projectRoot = project.ProjectRoot;
                foreach (string path in project.HierarchyPaths)
                    entries.Add(Path.Combine(projectRoot, path));
                entries.Sort(StringComparer.Ordinal);
                if (entries.Count == 0)
                    throw new InvalidDataException($"Unity package '{packagePath}' contains no .unity or .prefab files.");

                Debug.Meshes($"[UnityPackage] Prepared {entries.Count} scene/prefab root(s) from '{packagePath}'. Source project: '{projectRoot}'.");
                yield return new JobProgress(1.0f, projectRoot);
            }

            try
            {
                _ = Engine.Jobs.Schedule(
                    ExtractRoutine(),
                    progress: null,
                    completed: () =>
                    {
                        if (projectRoot is null)
                        {
                            onError(new InvalidDataException($"Unity package extraction returned no source project for '{packagePath}'."));
                            return;
                        }
                        onFinished(projectRoot, entries);
                    },
                    error: onError);
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }

        /// <summary>
        /// Resolves local dependency files and folders with the startup model search order.
        /// </summary>
        private static string ResolveUnityDependencyPath(string desktopDir, string rawPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
            if (Path.IsPathRooted(rawPath))
                return Path.GetFullPath(rawPath);

            foreach (string root in new[] { Environment.CurrentDirectory, desktopDir, Engine.Assets.EngineAssetsPath })
            {
                string candidate = Path.GetFullPath(Path.Combine(root, rawPath));
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }
            throw new FileNotFoundException($"Unity import dependency was not found: '{rawPath}'.");
        }
    }
}
