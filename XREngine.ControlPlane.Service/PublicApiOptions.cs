namespace XREngine.ControlPlane.Service;

/// <summary>Explicit HTTPS/mTLS gateway policy for operator-provisioned accounts. Does not configure an external identity provider.</summary>
public sealed class PublicApiOptions
{
    public string CertificateThumbprint { get; set; } = string.Empty;
    public bool UseMachineCertificateStore { get; set; }
    public List<PublicApiAccount> Accounts { get; set; } = [];

    public void Validate(LocalServiceOptions options)
    {
        if (options.Mode != LocalServiceMode.Gateway || options.NativeLauncher.Enabled)
            throw new InvalidOperationException("Public HTTPS runs only as a separate gateway with the server-side desktop launcher disabled.");

        if (string.IsNullOrWhiteSpace(CertificateThumbprint) || 
            Accounts.Count is < 1 or > 4096 || 
            !Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out Uri? listen) || 
            listen.Scheme != "https" || 
            listen.AbsolutePath != "/" || 
            !string.IsNullOrEmpty(listen.UserInfo) || 
            !string.IsNullOrEmpty(listen.Query) || 
            !string.IsNullOrEmpty(listen.Fragment))
            throw new InvalidOperationException("Public gateway requires an explicit HTTPS origin, certificate and account mapping.");
        
        var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new HashSet<string>(StringComparer.Ordinal);
        foreach (PublicApiAccount account in Accounts)
        {
            if (!users.Add(account.UserId) || 
                !options.Users.Any(user => user.UserId == account.UserId) || 
                account.CertificatePublicKeyPins.Count is < 1 or > 2)
                throw new InvalidOperationException("Public identities must map to distinct configured backend accounts and current/next certificate pins.");
            
            foreach (string pin in account.CertificatePublicKeyPins)
                if (pin.Length != 64 || !pin.All(Uri.IsHexDigit) || !pins.Add(pin))
                    throw new InvalidOperationException("Public certificate pins must be distinct SHA-256 SPKI values.");
        }
    }
}
