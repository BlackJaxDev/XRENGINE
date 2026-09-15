using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace XREngine.ControlPlane.Service;

/// <summary>Authenticated local reference API for managed dedicated-server instances.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--config")
            throw new ArgumentException("Usage: XREngine.ControlPlane.Service --config <operator-config.json>");
        string configurationPath = Path.GetFullPath(args[1]);
        LocalServiceOptions options = JsonSerializer.Deserialize<LocalServiceOptions>(
            await File.ReadAllTextAsync(configurationPath), ServiceJson.Options)
            ?? throw new InvalidOperationException("The local service configuration is empty.");
        options.Validate(Path.GetDirectoryName(configurationPath)!);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(options.ListenUrl);
        PublicApiSecurity.Configure(builder, options);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 512 * 1024);
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 250, Window = TimeSpan.FromSeconds(1), QueueLimit = 0 }));
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ManagedAdmissionSigner>();
        builder.Services.AddSingleton<LocalHostAgentStore>();
        builder.Services.AddSingleton<LocalNativeGameLauncher>();
        if (options.Mode == LocalServiceMode.HostAgent)
        {
        builder.Services.AddSingleton(new InMemoryControlPlane(new ControlPlaneOptions
        {
            AdmissionReservationLifetimeSeconds = options.AdmissionTimeoutSeconds,
            AdmissionDirectiveLeaseSeconds = options.WorkerLeaseSeconds,
            AdmissionResumeLeaseSeconds = options.ResumeWindowSeconds,
            HostHeartbeatLeaseSeconds = options.WorkerLeaseSeconds,
        }));
        builder.Services.AddSingleton<LocalPackageCatalog>();
        builder.Services.AddSingleton<IWorkerProcessLauncher, WindowsWorkerProcessLauncher>();
        builder.Services.AddSingleton<LocalWorkerSupervisor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<LocalWorkerSupervisor>());
        }
        else builder.Services.AddHttpClient("host-agent", client => client.BaseAddress = new Uri(options.AgentUrl + "/"));

        WebApplication app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            Uri origin = new(options.ListenUrl);
            if (!string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 400;
                return;
            }
            if (context.Request.Method is "POST" or "DELETE" && context.Request.Headers.Origin is { Count: > 0 } browserOrigin
                && !browserOrigin.ToString().Equals(origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (options.PublicApi is not null)
                context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            // The shell intentionally contains no data or credentials. Keeping it public lets an
            // ordinary local browser collect a bearer in memory before it calls any protected API.
            if (context.Request.Path != "/" && context.Request.Path != "/health" && !context.Request.Path.StartsWithSegments("/v1/workers"))
            {
                string token = Bearer(context);
                LocalApiUser? user = options.PublicApi is not null ? await PublicApiSecurity.AuthenticateAsync(context, options)
                    : token.Length is > 0 and <= 256 ? options.Users.FirstOrDefault(user => user.Matches(token)) : null;
                if (user is null)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { code = "Unauthorized", message = "A local API bearer credential is required." });
                    return;
                }
                context.Items[typeof(LocalApiUser)] = user;
                if (!user.TryAcquireRequestBudget())
                {
                    context.Response.StatusCode = 429;
                    return;
                }
            }
            try
            {
                await next(context);
                if (context.Request.Method is "POST" or "DELETE" && !context.Request.Path.StartsWithSegments("/v1/workers"))
                    app.Logger.LogInformation("Managed operation account={Account} tenant={Tenant} method={Method} path={Path} status={Status} correlation={Correlation}",
                        (context.Items[typeof(LocalApiUser)] as LocalApiUser)?.UserId,
                        (context.Items[typeof(LocalApiUser)] as LocalApiUser)?.TenantId,
                        context.Request.Method, context.Request.Path, context.Response.StatusCode, context.TraceIdentifier);
            }
            catch (Exception exception) when (!context.Response.HasStarted && exception is not OperationCanceledException)
            {
                bool inputFailure = exception is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or BadHttpRequestException;
                context.Response.StatusCode = inputFailure ? 400 : 500;
                app.Logger.LogError(exception, "Local API failure {TraceId}", context.TraceIdentifier);
                await context.Response.WriteAsJsonAsync(new
                {
                    code = inputFailure ? "InvalidOperation" : "InternalError",
                    message = "The operation failed. Check the request and correlated service diagnostics.",
                    correlationId = context.TraceIdentifier,
                });
            }
        });
        if (options.Mode == LocalServiceMode.Gateway)
        {
            app.MapGet("/health", () => Results.Ok(new { status = "ready", mode = options.PublicApi is null ? "local-gateway" : "https-mtls-gateway" }));
            app.MapManagedHostAgentGateway();
            await app.RunAsync();
            return;
        }
        app.MapManagedServiceUi();
        app.MapManagedContent();

        app.MapGet("/health", () => Results.Ok(new { status = "ready", contractVersion = 1, mode = "local-development" }));
        app.MapGet("/v1", () => Results.Ok(new { contractVersion = 1, mode = "host-agent", persistence = "protected-local-checkpoint",
            realtimeTransport = options.RealtimeTls is null ? "NativeUdp" : "NativeTls" }));
        app.MapGet("/v1/packages", (LocalPackageCatalog catalog) => Results.Ok(catalog.List()));
        app.MapGet("/v1/hosts", (HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor) =>
            User(context).IsAdministrator ? Results.Ok(new { hosts = registry.ListHosts(), resources = supervisor.ResourceHeadroom() }) : Results.StatusCode(403));
        app.MapGet("/v1/instances", (HttpContext context, InMemoryControlPlane registry, string? worldId, string? state, int? offset, int? limit, bool? includeStopped) =>
        {
            LocalApiUser user = User(context);
            IEnumerable<MultiplayerInstanceInfo> query = registry.ListInstances(includeStopped == true).Where(instance => CanView(user, instance));
            if (!string.IsNullOrEmpty(worldId))
                query = query.Where(instance => instance.WorldAsset.WorldId == worldId);
            if (!string.IsNullOrEmpty(state))
                query = query.Where(instance => instance.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase));
            MultiplayerInstanceInfo[] items = query.OrderBy(instance => instance.CreatedUtc).ThenBy(instance => instance.InstanceId).ToArray();
            return Results.Ok(new { total = items.Length, items = items.Skip(Math.Clamp(offset ?? 0, 0, 10_000)).Take(Math.Clamp(limit ?? 25, 1, 100)).Select(DirectoryEntry) });
        });
        app.MapGet("/v1/instances/{id}", (string id, HttpContext context, InMemoryControlPlane registry) =>
        {
            MultiplayerInstanceInfo? instance = VisibleInstance(registry, User(context), id);
            return instance is null ? Results.NotFound() : Results.Ok(DirectoryEntry(instance));
        });
        app.MapPost("/v1/instances", async (CreateLocalInstanceRequest request, HttpContext context, LocalWorkerSupervisor supervisor) =>
        {
            ControlPlaneResult<MultiplayerInstanceInfo> result = await supervisor.CreateAsync(request, User(context), context.RequestAborted);
            return result.Success && result.Value is not null
                ? Results.Json(DirectoryEntry(result.Value), statusCode: StatusCodes.Status202Accepted) : Failure(result);
        });
        app.MapGet("/v1/instances/{id}/status", (string id, HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor) =>
        {
            MultiplayerInstanceInfo? instance = VisibleInstance(registry, User(context), id);
            return instance is null ? Results.NotFound() : !CanManage(User(context), instance) ? Results.StatusCode(403) : Results.Ok(supervisor.Status(id));
        });
        app.MapPost("/v1/instances/{id}/stop", (string id, HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor) =>
            RequestLifecycle(id, context, registry, supervisor, drain: false));
        app.MapPost("/v1/instances/{id}/drain", (string id, HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor) =>
            RequestLifecycle(id, context, registry, supervisor, drain: true));
        app.MapPost("/v1/instances/{id}/reservations", (string id, JoinLocalInstanceRequest request, HttpContext context, InMemoryControlPlane registry) =>
        {
            LocalApiUser user = User(context);
            MultiplayerInstanceInfo? instance = VisibleInstance(registry, user, id);
            if (instance is null)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128
                || string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 128
                || string.IsNullOrWhiteSpace(request.BuildVersion))
                return Results.BadRequest(new { code = "InvalidRequest", message = "Operation ID, client ID, and build version are required." });
            ControlPlaneResult<ReserveManagedAdmissionResult> result = registry.ReserveAdmission(new()
            {
                InstanceId = id, AccountId = user.UserId, ClientId = request.ClientId,
                IdempotencyKey = user.UserId + ":" + request.OperationId, BuildVersion = request.BuildVersion,
            });
            return result.Success && result.Value is not null
                ? Results.Json(result.Value.Reservation, statusCode: StatusCodes.Status202Accepted) : Failure(result);
        });
        app.MapGet("/v1/instances/{id}/reservations/{reservationId}", (string id, string reservationId, HttpContext context, InMemoryControlPlane registry) =>
        {
            var reservation = registry.GetReservation(id, reservationId);
            if (!reservation.Success || reservation.Value is null)
                return Failure(reservation);
            return OwnsReservation(User(context), reservation.Value, registry) ? Results.Ok(reservation.Value) : Results.NotFound();
        });
        app.MapGet("/v1/instances/{id}/reservations/{reservationId}/handoff", (string id, string reservationId, HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor, ManagedAdmissionSigner signer) =>
        {
            var reservation = registry.GetReservation(id, reservationId);
            if (!reservation.Success || reservation.Value is null || !OwnsReservation(User(context), reservation.Value, registry))
                return Results.NotFound();
            ControlPlaneResult<ReserveManagedAdmissionResult> delivered = registry.GetDeliveredAdmission(id, reservationId);
            if (!delivered.Success || delivered.Value?.Grant is not { } grant)
                return Failure(delivered);
            ManagedWorkerLaunch? launch = supervisor.GetLaunch(id);
            if (launch is null)
                return Results.NotFound();
            signer.Sign(grant, launch);
            return Results.Ok(new ManagedClientLaunch
            {
                AdmissionCredential = grant,
                Handoff = new()
                {
                    SessionId = launch.SessionId, ClientId = grant.ClientId, AccountId = grant.AccountId,
                    ReservationId = grant.ReservationId, AdmissionSecret = grant.Secret, WorkerGeneration = grant.Generation,
                    ResumeRequested = grant.Purpose == ManagedAdmissionGrantPurpose.Resume, CredentialEpoch = grant.CredentialEpoch,
                    Endpoint = launch.AdvertisedEndpoint, WorldAsset = launch.WorldPackage.Asset,
                },
                WorldPackage = launch.WorldPackage, PackageRootPath = launch.PackageRootPath,
                CacheRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XREngine", "ManagedWorldCache"),
                WorldEntryPoint = launch.WorldEntryPoint, GameBootstrapId = launch.GameBootstrapId,
                BuildVersion = launch.BuildVersion,
            });
        });
        app.MapDelete("/v1/instances/{id}/reservations/{reservationId}", (string id, string reservationId, HttpContext context, InMemoryControlPlane registry) =>
        {
            var reservation = registry.GetReservation(id, reservationId);
            if (!reservation.Success || reservation.Value is null)
                return Results.NoContent();
            if (!OwnsReservation(User(context), reservation.Value, registry))
                return Results.NotFound();
            registry.RevokeAdmission(id, reservationId, kick: true);
            return Results.NoContent();
        });
        app.MapPost("/v1/instances/{id}/reservations/{reservationId}/kick", (string id, string reservationId, HttpContext context, InMemoryControlPlane registry) =>
        {
            MultiplayerInstanceInfo? instance = VisibleInstance(registry, User(context), id);
            if (instance is null)
                return Results.NotFound();
            if (!CanManage(User(context), instance))
                return Results.StatusCode(403);
            registry.RevokeAdmission(id, reservationId, kick: true);
            return Results.NoContent();
        });
        app.MapPost("/v1/workers/{id}/reports", (string id, ManagedWorkerReport report, HttpContext context, LocalWorkerSupervisor supervisor) =>
        {
            if (id != report.InstanceId)
                return Results.BadRequest(new { code = "InvalidRequest" });
            var result = supervisor.Report(report, Bearer(context));
            return result.Success ? Results.Ok(result.Value) : Failure(result);
        });
        await app.RunAsync();
    }

    private static LocalApiUser User(HttpContext context) => (LocalApiUser)context.Items[typeof(LocalApiUser)]!;
    private static string Bearer(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
    }
    private static bool CanView(LocalApiUser user, MultiplayerInstanceInfo instance)
        => user.TenantId == instance.TenantId && (instance.Visibility == MultiplayerInstanceVisibility.Public || CanManage(user, instance));
    private static bool CanManage(LocalApiUser user, MultiplayerInstanceInfo instance)
        => user.TenantId == instance.TenantId && (user.IsAdministrator || user.UserId == instance.OwnerUserId);
    private static bool OwnsReservation(LocalApiUser user, ManagedAdmissionReservation reservation, InMemoryControlPlane registry)
        => registry.GetInstance(reservation.InstanceId).Value is { } instance && user.TenantId == instance.TenantId
            && (user.IsAdministrator || user.UserId == reservation.AccountId);
    private static MultiplayerInstanceInfo? VisibleInstance(InMemoryControlPlane registry, LocalApiUser user, string id)
        => registry.GetInstance(id).Value is { } instance && CanView(user, instance) ? instance : null;
    private static object DirectoryEntry(MultiplayerInstanceInfo instance) => new
    {
        instance.InstanceId, instance.DisplayName, instance.OwnerUserId, instance.Visibility, instance.OperationId,
        instance.WorkerGeneration, instance.SessionId, endpoint = instance.Endpoint,
        world = new { instance.WorldAsset.WorldId, instance.WorldAsset.RevisionId, instance.WorldAsset.ContentHash,
            instance.WorldAsset.AssetSchemaVersion, instance.WorldAsset.RequiredBuildVersion },
        instance.State, instance.MaxPlayers, instance.ReservedPlayers, instance.ConnectedPlayers,
        instance.SynchronizedPlayers, instance.ResumeHeldPlayers, instance.CreatedUtc,
    };
    private static IResult RequestLifecycle(string id, HttpContext context, InMemoryControlPlane registry, LocalWorkerSupervisor supervisor, bool drain)
    {
        MultiplayerInstanceInfo? instance = VisibleInstance(registry, User(context), id);
        if (instance is null)
            return Results.NotFound();
        if (!CanManage(User(context), instance))
            return Results.StatusCode(403);
        supervisor.RequestStop(id, drain);
        return Results.Json(DirectoryEntry(registry.GetInstance(id).Value!), statusCode: StatusCodes.Status202Accepted);
    }
    private static IResult Failure<T>(ControlPlaneResult<T> result)
    {
        int status = result.FailureReason switch
        {
            ControlPlaneFailureReason.InstanceNotFound or ControlPlaneFailureReason.ReservationNotFound => 404,
            ControlPlaneFailureReason.InvalidRequest or ControlPlaneFailureReason.WorldAssetMismatch or ControlPlaneFailureReason.BuildVersionMismatch => 400,
            _ => 409,
        };
        return Results.Json(new { code = result.FailureReason.ToString(), message = result.Message }, statusCode: status);
    }
}

