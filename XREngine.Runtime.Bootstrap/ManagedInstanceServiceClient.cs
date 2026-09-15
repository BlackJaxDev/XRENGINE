using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using XREngine.ControlPlane;

namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Credential-safe client for the authenticated local managed-instance service. The bearer credential
/// is sent only in an HTTP authorization header and is never included in route, query, diagnostics, or launch state.
/// </summary>
public sealed class ManagedInstanceServiceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _origin;
    private readonly string _bearerCredential;
    private readonly TimeSpan _timeout;
    private readonly X509Certificate2? _clientCertificate;
    private bool _disposed;

    public ManagedInstanceServiceClient(Uri origin, string bearerCredential, TimeSpan? timeout = null, X509Certificate2? clientCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/")
        {
            throw new ArgumentException("Managed service origin must be a credential-free absolute root URI.", nameof(origin));
        }
        if (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(origin.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("Managed service requires HTTPS or explicit literal loopback HTTP.", nameof(origin));
        if (string.IsNullOrWhiteSpace(bearerCredential) && clientCertificate is null)
            throw new ArgumentException("A managed service bearer credential or client certificate is required.", nameof(bearerCredential));

        _origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        _bearerCredential = bearerCredential;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _clientCertificate = clientCertificate;
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        _http = new HttpClient(handler)
        {
            BaseAddress = _origin,
            Timeout = _timeout,
        };
        if (!string.IsNullOrWhiteSpace(bearerCredential))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerCredential);
    }

    /// <summary>Creates an HTTPS client for the optional mutual-TLS gateway without placing an API credential in the request.</summary>
    public ManagedInstanceServiceClient(Uri origin, X509Certificate2 clientCertificate, TimeSpan? timeout = null)
        : this(origin, string.Empty, timeout, clientCertificate ?? throw new ArgumentNullException(nameof(clientCertificate))) { }

    /// <summary>Creates an idempotent reservation for one stable native client identity.</summary>
    public async Task<JsonDocument> ListInstancesAsync(CancellationToken cancellationToken = default)
        => await ReadDocumentAsync(await _http.GetAsync("v1/instances", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Returns the catalog visible to the authenticated local service account.</summary>
    public async Task<JsonDocument> ListPackagesAsync(CancellationToken cancellationToken = default)
        => await ReadDocumentAsync(await _http.GetAsync("v1/packages", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Creates a local managed instance through the authenticated service.</summary>
    public async Task<JsonDocument> CreateInstanceAsync(ManagedServiceCreateInstanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/instances")
        {
            Content = JsonContent.Create(request, ManagedInstanceServiceClientJsonContext.Default.ManagedServiceCreateInstanceRequest),
        };
        return await ReadDocumentAsync(await _http.SendAsync(message, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates an idempotent reservation for one stable native client identity.</summary>
    public async Task<ManagedAdmissionReservation> ReserveAsync(
        string instanceId,
        ManagedServiceReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(request);
        using HttpRequestMessage message = new(HttpMethod.Post, $"v1/instances/{Uri.EscapeDataString(instanceId)}/reservations")
        {
            Content = JsonContent.Create(request, ManagedInstanceServiceClientJsonContext.Default.ManagedServiceReservationRequest),
        };
        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync(response, ManagedInstanceServiceClientJsonContext.Default.ManagedAdmissionReservation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves an already delivered player launch, mirrors its immutable package from the service, and returns
    /// an in-memory launch whose package path is the verified local cache rather than the worker's private path.
    /// </summary>
    public async Task<ManagedClientLaunchLease> AcquireLaunchAsync(
        string instanceId,
        string reservationId,
        RemoteWorldPackageCache packageCache,
        CancellationToken cancellationToken = default,
        IProgress<WorldPackageStagingProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentNullException.ThrowIfNull(packageCache);
        using HttpResponseMessage response = await _http.GetAsync(
            $"v1/instances/{Uri.EscapeDataString(instanceId)}/reservations/{Uri.EscapeDataString(reservationId)}/handoff",
            cancellationToken).ConfigureAwait(false);
        ManagedClientLaunch launch = await ReadRequiredAsync(response, XreControlPlaneJsonContext.Default.ManagedClientLaunch, cancellationToken).ConfigureAwait(false);
        RemoteWorldPackageLease packageLease = await packageCache.AcquireAsync(_http, _origin, launch.WorldPackage, cancellationToken, progress).ConfigureAwait(false);
        launch.PackageRootPath = packageLease.Path;
        // Public handoffs redact local cache paths. The downloaded immutable object already lives
        // beneath objects/&lt;manifest-hash&gt;, so staging against its parent re-verifies in place.
        if (string.IsNullOrWhiteSpace(launch.CacheRootPath))
            launch.CacheRootPath = Path.GetDirectoryName(packageLease.Path)
                ?? throw new InvalidOperationException("The managed package cache did not provide a staging parent.");
        return new ManagedClientLaunchLease(launch, packageLease);
    }

    /// <summary>Revokes a player reservation and asks the worker to disconnect it when it is still present.</summary>
    public async Task DeleteReservationAsync(string instanceId, string reservationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        using HttpResponseMessage response = await _http.DeleteAsync(
            $"v1/instances/{Uri.EscapeDataString(instanceId)}/reservations/{Uri.EscapeDataString(reservationId)}",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The managed service rejected the leave request.", null, response.StatusCode);
    }

    /// <summary>Creates a private session client so a coordinator can revoke its reservation after the caller releases its request client.</summary>
    internal ManagedInstanceServiceClient CreateSessionClient()
        => new(_origin, _bearerCredential, _timeout, _clientCertificate);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The managed service rejected the requested operation.", null, response.StatusCode);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The managed service returned an empty response.");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The managed service rejected the requested operation.", null, response.StatusCode);
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
