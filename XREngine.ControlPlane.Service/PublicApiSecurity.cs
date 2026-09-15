using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace XREngine.ControlPlane.Service;

/// <summary>Public gateway TLS and certificate identity boundary. Backend worker APIs remain on the private loopback agent.</summary>
internal static class PublicApiSecurity
{
    /// <summary>
    /// Configures the public API security settings, including TLS and client certificate validation, for the specified web application builder.
    /// </summary>
    /// <param name="builder">The web application builder to configure.</param>
    /// <param name="service">The local service options containing the public API security policy.</param>
    public static void Configure(WebApplicationBuilder builder, LocalServiceOptions service)
    {
        if (service.PublicApi is not { } policy)
            return;
        
        policy.Validate(service);
        X509Certificate2 certificate = LoadServerCertificate(policy);
        builder.Services.AddSingleton(certificate);
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
        {
            https.ServerCertificate = certificate;
            https.SslProtocols = SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.CheckCertificateRevocation = true;
            https.ClientCertificateValidation = (client, _, errors) => errors == SslPolicyErrors.None && FindUser(client, service) is not null;
        }));
    }

    /// <summary>
    /// Authenticates the client certificate from the specified HTTP context against the public API security policy.
    /// </summary>
    /// <param name="context">The HTTP context containing the client certificate.</param>
    /// <param name="service">The local service options containing the public API security policy.</param>
    /// <returns>The authenticated <see cref="LocalApiUser"/> if the client certificate is valid; otherwise, <c>null</c>.</returns>
    public static async Task<LocalApiUser?> AuthenticateAsync(HttpContext context, LocalServiceOptions service)
    {
        if (!context.Request.IsHttps)
            return null;
        
        X509Certificate2? certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        return certificate is null 
            ? null 
            : FindUser(certificate, service);
    }

    /// <summary>
    /// Finds the local API user associated with the specified client certificate and local service options.
    /// </summary>
    /// <param name="certificate">The client certificate to match against the public API accounts.</param>
    /// <param name="service">The local service options containing the public API security policy.</param>
    /// <returns>The corresponding <see cref="LocalApiUser"/> if a match is found; otherwise, <c>null</c>.</returns>
    private static LocalApiUser? FindUser(X509Certificate2 certificate, LocalServiceOptions service)
    {
        if (DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || 
            DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            return null;
        
        string pin = Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        PublicApiAccount? account = service.PublicApi?.Accounts.FirstOrDefault(account => account.CertificatePublicKeyPins.Contains(pin, StringComparer.OrdinalIgnoreCase));
        return account is null 
            ? null 
            : service.Users.FirstOrDefault(user => user.UserId == account.UserId);
    }

    /// <summary>
    /// Loads the server certificate from the specified public API policy.
    /// </summary>
    /// <param name="policy">The public API options containing the certificate thumbprint and store location.</param>
    /// <returns>The loaded <see cref="X509Certificate2"/> instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the configured HTTPS private certificate is unavailable.</exception>
    private static X509Certificate2 LoadServerCertificate(PublicApiOptions policy)
    {
        using var store = new X509Store(
            StoreName.My, 
            policy.UseMachineCertificateStore 
                ? StoreLocation.LocalMachine 
                : StoreLocation.CurrentUser);
        
        store.Open(OpenFlags.ReadOnly);

        return store.Certificates
            .Find(X509FindType.FindByThumbprint, policy.CertificateThumbprint, validOnly: false)
            .Cast<X509Certificate2>()
            .SingleOrDefault(certificate => 
                certificate.HasPrivateKey && 
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && 
                certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            ?? throw new InvalidOperationException("The configured HTTPS private certificate is unavailable.");
    }
}
