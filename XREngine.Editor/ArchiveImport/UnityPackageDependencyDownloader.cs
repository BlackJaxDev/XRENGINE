using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Runtime.Bootstrap;

namespace XREngine.Editor;

/// <summary>
/// Resolves companion sources and keeps downloaded files in a checked cache.
/// </summary>
internal static class UnityPackageDependencyDownloader
{
    private const long MaxIndexBytes = 16L * 1024 * 1024;
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly object CacheLock = new();

    /// <summary>
    /// Resolves local sources and downloads selected remote archives.
    /// </summary>
    internal static List<string> Resolve(
        IReadOnlyList<UnityPackageDependencySettings> dependencies,
        string cacheRoot,
        Func<string, string> resolveLocalPath,
        Action<float, string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(resolveLocalPath);

        var paths = new List<string>(dependencies.Count);
        string root = Path.Combine(Path.GetFullPath(cacheRoot), "UnityDownloads");
        lock (CacheLock)
        {
            RejectReparsePoints(root);
            Directory.CreateDirectory(root);
            RejectReparsePoints(root);
            using HttpClient client = CreateClient();
            for (int i = 0; i < dependencies.Count; i++)
            {
                UnityPackageDependencySettings dependency = dependencies[i]
                    ?? throw new InvalidDataException("A Unity companion source is null.");
                string source = dependency.Source?.Trim()
                    ?? throw new InvalidDataException("A Unity companion source is missing.");
                if (source.Length == 0)
                    throw new InvalidDataException("A Unity companion source is missing.");

                float start = (float)i / dependencies.Count;
                progress?.Invoke(start, $"Resolving companion source {i + 1} of {dependencies.Count}.");
                if (source.StartsWith("vcc:", StringComparison.OrdinalIgnoreCase))
                    source = ParseVccUrl(source);

                if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    ValidateUrl(uri);
                    string extension = Path.GetExtension(uri.AbsolutePath);
                    if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                             extension.Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(dependency.PackageId) || !string.IsNullOrWhiteSpace(dependency.Version))
                            throw new InvalidDataException("PackageId and Version require a VPM repository URL.");
                        paths.Add(GetCachedFile(client, uri, root, extension.ToLowerInvariant(), MaxArchiveBytes, null));
                    }
                    else
                        paths.Add(ResolveRepository(client, uri, dependency, root, progress));
                }
                else
                {
                    if (Uri.TryCreate(source, UriKind.Absolute, out Uri? other) && !other.IsFile)
                        throw new InvalidDataException("A remote companion source must use HTTPS.");
                    if (!string.IsNullOrWhiteSpace(dependency.PackageId) || !string.IsNullOrWhiteSpace(dependency.Version))
                        throw new InvalidDataException("PackageId and Version require a VPM repository URL.");
                    string path = resolveLocalPath(source);
                    if (!File.Exists(path) && !Directory.Exists(path))
                        throw new FileNotFoundException("The local companion source was not found.", path);
                    paths.Add(path);
                }

                progress?.Invoke((float)(i + 1) / dependencies.Count, $"Companion source {i + 1} is ready.");
            }
        }
        return paths;
    }

    private static string ResolveRepository(HttpClient client, Uri repository, UnityPackageDependencySettings dependency,
        string root, Action<float, string>? progress)
    {
        string indexPath = GetCachedFile(client, repository, root, ".json", MaxIndexBytes, null);
        using FileStream index = File.OpenRead(indexPath);
        using JsonDocument document = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("packages", out JsonElement packages) ||
            packages.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The VPM repository has no packages object.");

        string packageId = dependency.PackageId?.Trim() ?? string.Empty;
        if (packageId.Length == 0)
        {
            using JsonElement.ObjectEnumerator names = packages.EnumerateObject();
            if (!names.MoveNext())
                throw new InvalidDataException("The VPM repository has no packages.");
            packageId = names.Current.Name;
            if (names.MoveNext())
                throw new InvalidDataException("The VPM repository has multiple packages. Set PackageId.");
        }
        if (!packages.TryGetProperty(packageId, out JsonElement package) || package.ValueKind != JsonValueKind.Object ||
            !package.TryGetProperty("versions", out JsonElement versions) || versions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"VPM package '{packageId}' was not found.");

        string? requested = dependency.Version?.Trim();
        if (requested?.Length == 0)
            requested = null;
        string selectionPath = Path.Combine(root, HashText(repository.AbsoluteUri + "\n" + packageId) + ".version");
        RejectReparsePoints(selectionPath);
        if (string.IsNullOrEmpty(requested) && File.Exists(selectionPath))
            requested = File.ReadAllText(selectionPath).Trim();
        string version = requested ?? FindGreatestStableVersion(versions);
        if (!TryParseVersion(version, allowPrerelease: true, out _, out _, out _))
            throw new InvalidDataException($"VPM version '{version}' is not a supported numeric semantic version.");
        if (!versions.TryGetProperty(version, out JsonElement manifest) || manifest.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"VPM package '{packageId}' does not contain version '{version}'.");
        if (ReadRequiredString(manifest, "name") != packageId || ReadRequiredString(manifest, "version") != version)
            throw new InvalidDataException("The VPM manifest name or version does not match the repository entry.");

        string archiveUrl = ReadRequiredString(manifest, "url");
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out Uri? archive))
            throw new InvalidDataException("The VPM archive URL is invalid.");
        ValidateUrl(archive);
        string? expectedHash = manifest.TryGetProperty("zipSHA256", out JsonElement hashElement) &&
            hashElement.ValueKind == JsonValueKind.String ? hashElement.GetString() : null;
        if (expectedHash is not null && !IsSha256(expectedHash))
            throw new InvalidDataException("The VPM zipSHA256 value is invalid.");
        if (manifest.TryGetProperty("vpmDependencies", out JsonElement declared) &&
            declared.ValueKind == JsonValueKind.Object && declared.EnumerateObject().MoveNext())
        {
            var names = new List<string>();
            foreach (JsonProperty item in declared.EnumerateObject())
                names.Add(item.Name);
            string message = $"VPM package '{packageId}' declares companion packages: {string.Join(", ", names)}. Supply each source separately.";
            System.Diagnostics.Trace.TraceWarning(message);
            progress?.Invoke(0f, message);
        }

        progress?.Invoke(0f, $"Selected VPM package '{packageId}' version {version}.");
        System.Diagnostics.Trace.TraceInformation($"Selected VPM package '{packageId}' version {version}.");
        string path = GetCachedFile(client, archive, root, ".zip", MaxArchiveBytes, expectedHash);
        ValidateVpmArchive(path, packageId, version);
        if (string.IsNullOrWhiteSpace(dependency.Version) && !File.Exists(selectionPath))
            WriteAtomic(selectionPath, version);
        return path;
    }

    private static string FindGreatestStableVersion(JsonElement versions)
    {
        string? best = null;
        int bestMajor = -1, bestMinor = -1, bestPatch = -1;
        foreach (JsonProperty candidate in versions.EnumerateObject())
        {
            if (!TryParseVersion(candidate.Name, allowPrerelease: false, out int major, out int minor, out int patch))
                continue;
            if (major > bestMajor || major == bestMajor && minor > bestMinor ||
                major == bestMajor && minor == bestMinor && patch > bestPatch)
            {
                best = candidate.Name;
                bestMajor = major;
                bestMinor = minor;
                bestPatch = patch;
            }
        }
        return best ?? throw new InvalidDataException("The VPM package has no supported stable version. Set an exact Version.");
    }

    private static bool TryParseVersion(string value, bool allowPrerelease, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        int suffix = value.IndexOf('-');
        string core = suffix >= 0 ? value[..suffix] : value;
        if (suffix >= 0)
        {
            if (!allowPrerelease || suffix == value.Length - 1)
                return false;
            foreach (string identifier in value[(suffix + 1)..].Split('.'))
            {
                if (identifier.Length == 0 || identifier.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-') ||
                    identifier.Length > 1 && identifier[0] == '0' && identifier.All(static ch => ch is >= '0' and <= '9'))
                    return false;
            }
        }
        string[] parts = core.Split('.');
        return parts.Length == 3 && ParseNumber(parts[0], out major) &&
            ParseNumber(parts[1], out minor) && ParseNumber(parts[2], out patch);
    }

    private static bool ParseNumber(string text, out int number)
    {
        number = 0;
        return text.Length > 0 && (text.Length == 1 || text[0] != '0') &&
            text.All(static ch => ch is >= '0' and <= '9') && int.TryParse(text, out number);
    }

    private static string ReadRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The VPM manifest has no valid '{property}' value.");
        return value.GetString()!;
    }

    private static void ValidateVpmArchive(string path, string packageId, string version)
    {
        using var archive = ZipFile.OpenRead(path);
        ZipArchiveEntry? packageManifest = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase))
                continue;
            // Embedded libraries can have their own manifests below the package root.
            if (name.Count(static ch => ch == '/') > 1)
                continue;
            if (packageManifest is not null)
                throw new InvalidDataException("The VPM archive must have one package.json at its root or in one wrapper folder.");
            packageManifest = entry;
        }
        if (packageManifest is null || packageManifest.Length > 1024 * 1024)
            throw new InvalidDataException("The VPM archive has no small package.json.");

        using Stream input = packageManifest.Open();
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        int count;
        while ((count = input.Read(block, 0, block.Length)) != 0)
        {
            if (buffer.Length + count > 1024 * 1024)
                throw new InvalidDataException("The VPM package.json exceeds the size limit.");
            buffer.Write(block, 0, count);
        }
        buffer.Position = 0;
        using JsonDocument document = JsonDocument.Parse(buffer, new JsonDocumentOptions { MaxDepth = 32 });
        if (ReadRequiredString(document.RootElement, "name") != packageId ||
            ReadRequiredString(document.RootElement, "version") != version)
            throw new InvalidDataException("The VPM archive package.json does not match the selected package name and version.");
    }

    private static string ParseVccUrl(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("vcc", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("vpm", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "/addRepo" || uri.Fragment.Length != 0)
            throw new InvalidDataException("The VCC source must use vcc://vpm/addRepo?url=<encodedhttps>.");
        string query = uri.Query.TrimStart('?');
        if (!query.StartsWith("url=", StringComparison.Ordinal) || query.Contains('&'))
            throw new InvalidDataException("The VCC source must contain one URL parameter.");
        string url = Uri.UnescapeDataString(query[4..]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? repository))
            throw new InvalidDataException("The VCC repository URL is invalid.");
        ValidateUrl(repository);
        return repository.AbsoluteUri;
    }

    private static string GetCachedFile(HttpClient client, Uri uri, string root, string extension, long maxBytes, string? expectedHash)
    {
        string key = HashText(uri.AbsoluteUri);
        string file = Path.Combine(root, key + extension);
        string metadata = file + ".sha256";
        RejectReparsePoints(file);
        RejectReparsePoints(metadata);
        if (File.Exists(file) || File.Exists(metadata))
        {
            if (!File.Exists(file) || !File.Exists(metadata))
                throw new InvalidDataException("A Unity download cache entry is incomplete. Clear that entry and retry.");
            string[] lines = File.ReadAllLines(metadata);
            if (lines.Length != 2 || lines[0] != uri.AbsoluteUri || !IsSha256(lines[1]) ||
                !string.Equals(HashFile(file, maxBytes), lines[1], StringComparison.OrdinalIgnoreCase) ||
                expectedHash is not null && !string.Equals(lines[1], expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A Unity download cache hash does not match. Clear that entry and retry.");
            ValidateContent(file, extension);
            return file;
        }

        string partial = file + ".partial";
        RejectReparsePoints(partial);
        if (File.Exists(partial))
            throw new InvalidDataException("A Unity download is incomplete. Clear the .partial file and retry.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using HttpResponseMessage response = Send(client, uri, timeout.Token);
            if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
                throw new InvalidDataException("The Unity download exceeds the size limit.");
            using (Stream source = response.Content.ReadAsStream())
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[64 * 1024];
                long length = 0;
                int count;
                while ((count = source.ReadAsync(buffer, timeout.Token).GetAwaiter().GetResult()) != 0)
                {
                    length += count;
                    if (length > maxBytes)
                        throw new InvalidDataException("The Unity download exceeds the size limit.");
                    output.Write(buffer, 0, count);
                }
                output.Flush(flushToDisk: true);
            }
            string hash = HashFile(partial, maxBytes);
            if (expectedHash is not null && !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The VPM archive does not match zipSHA256.");
            ValidateContent(partial, extension);
            File.Move(partial, file);
            WriteAtomic(metadata, uri.AbsoluteUri + "\n" + hash);
            return file;
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static HttpResponseMessage Send(HttpClient client, Uri initial, CancellationToken cancellation)
    {
        Uri current = initial;
        for (int redirects = 0; redirects <= MaxRedirects; redirects++)
        {
            ValidateUrl(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .GetAwaiter().GetResult();
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == MaxRedirects)
                    throw new HttpRequestException("The Unity download has an invalid redirect chain.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                finally
                {
                    response.Dispose();
                }
            }
            return response;
        }
        throw new HttpRequestException("The Unity download has too many redirects.");
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            ConnectCallback = async (context, cancellation) =>
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                Exception? connectionError = null;
                bool foundPublicAddress = false;
                foreach (IPAddress address in addresses)
                {
                    if (!IsPublicAddress(address))
                        continue;
                    foundPublicAddress = true;
                    cancellation.ThrowIfCancellationRequested();
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException)
                    {
                        socket.Dispose();
                        throw;
                    }
                    catch (Exception error) when (error is SocketException or IOException)
                    {
                        socket.Dispose();
                        connectionError = error;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (!foundPublicAddress)
                    throw new InvalidDataException("The Unity download host does not resolve to a public address.");
                throw new HttpRequestException("The Unity download could not connect to any public address.", connectionError);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
    }

    private static void ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Host.Length == 0 || uri.Fragment.Length != 0 || uri.Port != 443 && !uri.IsDefaultPort ||
            uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.IdnHost, out IPAddress? address) && !IsPublicAddress(address))
            throw new InvalidDataException("A Unity download URL must use public HTTPS without credentials.");
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int a = bytes[0], b = bytes[1], c = bytes[2];
            return a is >= 1 and <= 223 && a != 10 && a != 127 &&
                !(a == 100 && b is >= 64 and <= 127) &&
                !(a == 169 && b == 254) && !(a == 172 && b is >= 16 and <= 31) &&
                !(a == 192 && (b == 0 || b == 168)) && !(a == 198 && (b == 18 || b == 19 || b == 51 && c == 100)) &&
                !(a == 203 && b == 0 && c == 113);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xE0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    private static void ValidateContent(string path, string extension)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        int count = stream.Read(header);
        if (extension == ".json" && stream.Length == 0 ||
            extension == ".zip" && (count < 4 || !(header[0] == 'P' && header[1] == 'K' &&
                (header[2] == 3 && header[3] == 4 || header[2] == 5 && header[3] == 6 || header[2] == 7 && header[3] == 8))) ||
            extension == ".unitypackage" && (count < 2 || header[0] != 0x1f || header[1] != 0x8b))
            throw new InvalidDataException("The Unity download content does not match its file type.");
    }

    private static string HashFile(string path, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes)
            throw new InvalidDataException("A Unity download cache entry exceeds the size limit.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void WriteAtomic(string path, string text)
    {
        string partial = path + ".partial";
        RejectReparsePoints(partial);
        using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(partial, path);
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Unity download cache path uses a reparse point.");
        }
    }
}
