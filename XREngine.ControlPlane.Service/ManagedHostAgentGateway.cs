using System.Net.Http.Headers;
using System.Net.Http.Json;
using XREngine.Networking;

namespace XREngine.ControlPlane.Service;

/// <summary>Authenticated loopback gateway; worker routes and internal host metadata are never proxied.</summary>
internal static class ManagedHostAgentGateway
{
    public static void MapManagedHostAgentGateway(this WebApplication app)
    {
        app.MapMethods("/v1/{**path}", ["GET", "POST", "DELETE"], async (HttpContext context, string? path, IHttpClientFactory clients, LocalServiceOptions options) =>
        {
            path ??= string.Empty;

            string decodedPath = Uri.UnescapeDataString(path);
            if (decodedPath.Contains('\\') || 
                decodedPath.Split('/').Any(segment => segment is "." or "..") || 
                decodedPath.StartsWith("workers", StringComparison.OrdinalIgnoreCase) || 
                decodedPath.StartsWith("hosts", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri($"v1/{path}{context.Request.QueryString}", UriKind.Relative));
            if (context.Request.ContentLength > 0 || HttpMethods.IsPost(context.Request.Method))
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType is { } requestContentType)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", requestContentType);
            }

            string authorization = context.Request.Headers.Authorization.ToString();
            if (options.PublicApi is not null)
            {
                if (context.Items[typeof(LocalApiUser)] is not LocalApiUser user)
                    return Results.Unauthorized();
                
                authorization = $"Bearer {user.AgentBearerCredential()}";
            }

            if (!string.IsNullOrWhiteSpace(authorization)) 
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            
            if (context.Request.Headers.Range.Count > 0)
                request.Headers.TryAddWithoutValidation("Range", context.Request.Headers.Range.ToString());
            
            using HttpResponseMessage response = await clients.CreateClient("host-agent").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            if (options.PublicApi is not null && path.EndsWith("/handoff", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                ManagedClientLaunch? launch = await response.Content.ReadFromJsonAsync(XreControlPlaneJsonContext.Default.ManagedClientLaunch, context.RequestAborted);
                if (launch?.Handoff.Endpoint?.Transport != RealtimeTransportKind.NativeTls || launch.AdmissionCredential?.Signature is null)
                    return Results.Problem(statusCode: 503, title: "Protected admission unavailable");
                launch.PackageRootPath = string.Empty;
                launch.CacheRootPath = string.Empty;
                return Results.Ok(launch);
            }

            context.Response.StatusCode = (int)response.StatusCode;
            CopyEndToEndHeader(response.Headers, context.Response.Headers, "Cache-Control");
            CopyEndToEndHeader(response.Headers, context.Response.Headers, "ETag");
            CopyEndToEndHeader(response.Headers, context.Response.Headers, "Last-Modified");
            CopyEndToEndHeader(response.Headers, context.Response.Headers, "Accept-Ranges");
            CopyEndToEndHeader(response.Content.Headers, context.Response.Headers, "Content-Range");

            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            
            if (response.Content.Headers.ContentLength is long length)
                context.Response.ContentLength = length;
            
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);

            return Results.Empty;
        });
    }

    private static void CopyEndToEndHeader(HttpHeaders source, IHeaderDictionary destination, string name)
    {
        if (!source.TryGetValues(name, out IEnumerable<string>? values))
            return;
        
        destination[name] = values.ToArray();
    }
}
