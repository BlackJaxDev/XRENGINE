using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace XREngine
{

    public static class ArchiveExtractor
    {
        private const float ArchivePhaseStart = 0.05f;
        private const float ArchivePhaseEnd = 0.5f;
        private const float CopyPhaseStart = 0.5f;
        private const float CopyPhaseEnd = 0.98f;

        public static void Extract(
            string packagePath,
            string destinationFolderPath,
            bool overwrite,
            IProgress<ArchiveExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            foreach (var update in ExtractWithProgress(packagePath, destinationFolderPath, overwrite, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(update);
            }
        }

        public static IEnumerable<ArchiveExtractionProgress> ExtractWithProgress(
            string packagePath,
            string destinationFolderPath,
            bool overwrite,
            CancellationToken cancellationToken = default)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(packagePath)}.archive.extract");
            yield return new ArchiveExtractionProgress(0f, EArchiveExtractionPhase.Preparing, "Preparing extraction...");
            try
            {
                foreach (var update in ExtractArchiveWithProgress(packagePath, tempFolder, cancellationToken))
                    yield return update;

                foreach (var update in CopyExtractedAssetsWithProgress(tempFolder, destinationFolderPath, overwrite, cancellationToken))
                    yield return update;

                yield return new ArchiveExtractionProgress(1f, EArchiveExtractionPhase.Completed, "Import complete.");
            }
            finally
            {
                TryDeleteDirectory(tempFolder);
            }
        }

        public static Task ExtractAsync(
            string packagePath,
            string destinationFolderPath,
            bool overwrite,
            IProgress<ArchiveExtractionProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.Run(() => Extract(packagePath, destinationFolderPath, overwrite, progress, cancellationToken), cancellationToken);

        private static IEnumerable<ArchiveExtractionProgress> ExtractArchiveWithProgress(
            string packagePath,
            string tempFolder,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(tempFolder);
            using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzipStream = new GZipInputStream(fileStream);
            using var tarStream = new TarInputStream(gzipStream, Encoding.Default);

            TarEntry? entry;
            while ((entry = tarStream.GetNextEntry()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string entryPath = Path.Combine(tempFolder, entry.Name);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(entryPath);
                }
                else
                {
                    var folder = Path.GetDirectoryName(entryPath);
                    if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                        Directory.CreateDirectory(folder);

                    using var output = new FileStream(entryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    tarStream.CopyEntryContents(output);
                }

                float archiveProgress = fileStream.Length > 0
                    ? (float)fileStream.Position / fileStream.Length
                    : 0f;
                float scaled = ArchivePhaseStart + (ArchivePhaseEnd - ArchivePhaseStart) * archiveProgress;
                yield return new ArchiveExtractionProgress(
                    scaled,
                    EArchiveExtractionPhase.ExtractingArchive,
                    $"Extracting {entry.Name}",
                    entry.Name);
            }
        }

        private static IEnumerable<ArchiveExtractionProgress> CopyExtractedAssetsWithProgress(
            string tempFolder,
            string destinationFolderPath,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(tempFolder))
                yield break;

            var directories = Directory.GetDirectories(tempFolder);
            int total = directories.Length;
            if (total == 0)
                yield break;

            for (int index = 0; index < directories.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = directories[index];
                var pathnameFile = Path.Combine(item, "pathname");
                var pathname = File.Exists(pathnameFile) ? File.ReadAllText(pathnameFile) : Path.GetFileName(item);
                var path = Path.Combine(destinationFolderPath, pathname);
                var assetPath = Path.Combine(item, "asset");

                if (File.Exists(assetPath))
                {
                    var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (folder is not null)
                    {
                        if (!Directory.Exists(folder))
                            Directory.CreateDirectory(folder);

                        if (overwrite || !File.Exists(path))
                            File.Copy(assetPath, path, true);
                    }
                }
                else if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                var assetMetaPath = Path.Combine(item, "asset.meta");
                var metaPath = $"{path}.meta";

                if (File.Exists(assetMetaPath) && (overwrite || !File.Exists(metaPath)))
                    File.Copy(assetMetaPath, metaPath, true);

                float copyProgress = (float)(index + 1) / total;
                float scaled = CopyPhaseStart + (CopyPhaseEnd - CopyPhaseStart) * copyProgress;
                yield return new ArchiveExtractionProgress(
                    scaled,
                    EArchiveExtractionPhase.CopyingAssets,
                    $"Copying {pathname}",
                    pathname);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
