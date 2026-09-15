using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace XREngine.ControlPlane.Service;

/// <summary>Signs grants outside simulation; a rollout publishes current and verification-only previous public keys to workers.</summary>
internal sealed class ManagedAdmissionSigner : IDisposable
{
    private readonly AdmissionSigningOptions? _options;
    private readonly ECDsa? _key;
    private readonly X509Certificate2? _certificate;
    private readonly object _sync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedAdmissionSigner"/> class using the specified local service options.
    /// </summary>
    /// <param name="service">The local service options used to configure the managed admission signer.</param>
    /// <exception cref="InvalidOperationException">Thrown if the admission signing configuration is invalid or the required certificate or key is unavailable.</exception>
    public ManagedAdmissionSigner(LocalServiceOptions service)
    {
        _options = service.AdmissionSigning;

        if (_options is null)
            return;
        
        if (string.IsNullOrWhiteSpace(_options.Issuer) || 
            string.IsNullOrWhiteSpace(_options.ActiveKeyId) || 
            _options.PreviousVerificationKeys.Count > 7)
            throw new InvalidOperationException("Admission issuer, active key and bounded verification keys are required.");
        
        using var store = new X509Store(
            StoreName.My, 
            _options.UseMachineStore 
                ? StoreLocation.LocalMachine 
                : StoreLocation.CurrentUser);

        store.Open(OpenFlags.ReadOnly);

        _certificate = store.Certificates
            .Find(X509FindType.FindByThumbprint, _options.CertificateThumbprint, validOnly: false)
            .Cast<X509Certificate2>()
            .SingleOrDefault(certificate => 
                certificate.HasPrivateKey && 
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && 
                certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            ?? throw new InvalidOperationException("The configured admission signing certificate is unavailable.");
        
        _key = _certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Admission signing requires an ECDSA key.");

        if (_key.KeySize != 256)
            throw new InvalidOperationException("Admission signing requires P-256.");
    }

    /// <summary>
    /// Configures the specified worker launch with the necessary admission signing information based on the current managed admission signer settings.
    /// </summary>
    /// <param name="launch">The worker launch to be configured with admission signing information.</param>
    public void ConfigureLaunch(ManagedWorkerLaunch launch)
    {
        if (_options is null || _key is null)
            return;
        
        launch.AdmissionIssuer = _options.Issuer;
        launch.AdmissionSigningKeys = new(_options.PreviousVerificationKeys, StringComparer.Ordinal)
        {
            [_options.ActiveKeyId] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        };
    }

    /// <summary>
    /// Signs the specified managed admission grant using the configured admission signing key and associates the signature with the provided worker launch.
    /// </summary>
    /// <param name="grant">The managed admission grant to be signed.</param>
    /// <param name="launch">The worker launch information associated with the grant.</param>
    public void Sign(ManagedAdmissionGrant grant, ManagedWorkerLaunch launch)
    {
        if (_options is null || _key is null)
            return;
        
        lock (_sync)
            grant.Signature = ManagedAdmissionSigning.Sign(grant, launch, _options.Issuer, _options.ActiveKeyId, _key);
    }

    /// <summary>
    /// Disposes the managed admission signer, releasing any associated cryptographic resources.
    /// </summary>
    public void Dispose()
    {
        _key?.Dispose();
        _certificate?.Dispose();
    }
}
