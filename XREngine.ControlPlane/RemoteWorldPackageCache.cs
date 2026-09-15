using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>Authenticated, resumable immutable package downloads with bounded storage and active-use leases.</summary>
public sealed class RemoteWorldPackageCache
{
    private readonly string _root;
    private readonly long _maximumBytes;

    /// <summary>Absolute cache root selected by the native client application.</summary>
    public string RootPath => _root;

    public RemoteWorldPackageCache(string root, long maximumBytes = 8L * 1024 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root);
        if (maximumBytes is < 1024 * 1024 or > 1024L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_root);
        RejectReparsePath(_root);
    }

    public async Task<RemoteWorldPackageLease> AcquireAsync(HttpClient authenticatedClient, Uri serviceUrl,
        WorldPackageManifest manifest, CancellationToken cancellationToken = default, IProgress<WorldPackageStagingProgress>? progress = null)
    {
        if (!serviceUrl.IsAbsoluteUri || !string.IsNullOrEmpty(serviceUrl.UserInfo) || !string.IsNullOrEmpty(serviceUrl.Query)
            || !string.IsNullOrEmpty(serviceUrl.Fragment) || serviceUrl.AbsolutePath != "/"
            || serviceUrl.Scheme != "https" && !(serviceUrl.Scheme == "http" && IPAddress.TryParse(serviceUrl.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("Remote content requires HTTPS, or an explicit literal loopback development origin.");
        if (!WorldPackageManifestBuilder.VerifyManifest(manifest).Success || manifest.Files.Count > 8192
            || manifest.TotalBytes < 0 || manifest.TotalBytes > _maximumBytes)
            throw new InvalidDataException("The requested immutable package is invalid or exceeds the cache budget.");
        string hash = WorldAssetIdentity.NormalizeHash(manifest.ManifestHash);
        string target = ContainedPath("objects/" + hash);
        string incoming = ContainedPath("incoming/" + hash);
        string leasePath = ContainedPath("leases/" + hash + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        FileStream? lease = null;
        // A cross-process cache mutex serializes writers and eviction. Active package leases are separate.
        await using FileStream cacheLock = await AcquireLockAsync(ContainedPath("cache.lock"), cancellationToken).ConfigureAwait(false);
        try
        {
            RejectReparsePath(leasePath);
            if (Directory.Exists(target))
            {
                lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                if (!WorldPackageManifestBuilder.Verify(manifest, target, cancellationToken).Success)
                    throw new InvalidDataException("Cached immutable package failed verification.");
                Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                return new RemoteWorldPackageLease(target, lease);
            }
            lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ReserveSpace(manifest.TotalBytes, incoming);
            Directory.CreateDirectory(incoming);
            long completed = 0;
            for (int index = 0; index < manifest.Files.Count; index++)
            {
                WorldPackageFile file = manifest.Files[index];
                string path = ContainedPath("incoming/" + hash + "/" + file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RejectReparsePath(path);
                Uri uri = new(serviceUrl, $"v1/packages/{Uri.EscapeDataString(manifest.PackageId)}/{hash}/files/{index}");
                await DownloadFileAsync(authenticatedClient, uri, file, path, cancellationToken).ConfigureAwait(false);
                completed += file.Length;
                progress?.Report(new WorldPackageStagingProgress { RelativePath = file.RelativePath, BytesTransferred = completed, TotalBytes = manifest.TotalBytes });
            }
            if (!WorldPackageManifestBuilder.Verify(manifest, incoming, cancellationToken).Success)
                throw new InvalidDataException("Downloaded immutable package failed verification.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(incoming, target);
            lease.Dispose();
            lease = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new RemoteWorldPackageLease(target, lease);
        }
        catch { lease?.Dispose(); throw; }
    }

    private static async Task DownloadFileAsync(HttpClient client, Uri uri, WorldPackageFile file, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            if (await MatchesAsync(path, file, cancellationToken).ConfigureAwait(false))
                return;
            File.Delete(path);
        }
        string partial = path + ".download";
        RejectReparsePath(partial);
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > file.Length)
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset != file.Length || !File.Exists(partial))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0)
                request.Headers.Range = new RangeHeaderValue(offset, null);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } finalUri || finalUri != uri)
                throw new InvalidDataException("Content endpoint redirects are not accepted.");
            bool resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (resumed && (response.Content.Headers.ContentRange is not { } range || range.From != offset || range.Length != file.Length
                || range.To != file.Length - 1))
                throw new InvalidDataException("Content range does not match the immutable file.");
            if (!resumed && response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("Unexpected immutable content response.");
            if (!resumed)
                offset = 0;
            if (response.Content.Headers.ContentLength is { } length && length != file.Length - offset)
                throw new InvalidDataException("Remote content length does not match the manifest.");
            await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];
            long total = offset;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > file.Length)
                    throw new InvalidDataException("Remote content exceeded its declared length.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total != file.Length)
                throw new EndOfStreamException("Immutable content transfer was interrupted; it can be resumed.");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!await MatchesAsync(partial, file, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new InvalidDataException("Remote content hash does not match the manifest.");
        }
        File.Move(partial, path);
    }

    private static async Task<bool> MatchesAsync(string path, WorldPackageFile file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return stream.Length == file.Length && Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .Equals(WorldAssetIdentity.NormalizeHash(file.Sha256), StringComparison.OrdinalIgnoreCase);
    }

    private void ReserveSpace(long requested, string incoming)
    {
        long used = Measure(_root);
        long remaining = Math.Max(0, requested - Measure(incoming));
        string objects = ContainedPath("objects");
        if (!Directory.Exists(objects))
            Directory.CreateDirectory(objects);
        foreach (DirectoryInfo candidate in new DirectoryInfo(objects).EnumerateDirectories().OrderBy(directory => directory.LastWriteTimeUtc))
        {
            if (used + remaining <= _maximumBytes)
                break;
            string path = ContainedPath("objects/" + candidate.Name);
            try
            {
                using var unused = new FileStream(ContainedPath("leases/" + candidate.Name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                long bytes = Measure(path);
                Directory.Delete(path, recursive: true);
                used -= bytes;
            }
            catch (IOException) { /* An active process owns the package; eviction must not invalidate it. */ }
        }
        if (used + remaining > _maximumBytes)
            throw new IOException("The immutable cache budget is exhausted by active packages or resumable downloads.");
    }

    private string ContainedPath(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(_root, relative));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Content path escapes the cache root.");
        RejectReparsePath(path);
        return path;
    }

    private static void RejectReparsePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Content cache paths cannot traverse reparse points.");
    }

    private static long Measure(string path)
    {
        if (!Directory.Exists(path))
            return 0;
        long bytes = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            RejectReparsePath(entry);
            bytes = checked(bytes + (Directory.Exists(entry) ? Measure(entry) : new FileInfo(entry).Length));
        }
        return bytes;
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePath(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
    }
}
