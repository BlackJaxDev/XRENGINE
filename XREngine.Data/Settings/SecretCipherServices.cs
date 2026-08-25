namespace XREngine.Settings;

/// <summary>
/// Lease-based installation point for editor-owned protected-secret persistence.
/// Runtime applications can resolve environment references without loading editor crypto policy.
/// </summary>
public static class SecretCipherServices
{
    public const string DpapiPrefix = "dpapi:";
    public const string EnvironmentPrefix = "env:";
    public const string ObfuscatedPrefix = "b64:";

    private static readonly ISecretCipherServices Default = new MissingSecretCipherServices();
    private static ISecretCipherServices _current = Default;

    public static ISecretCipherServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(ISecretCipherServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ISecretCipherServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        ISecretCipherServices installed,
        ISecretCipherServices previous) : IDisposable
    {
        private ISecretCipherServices? _installed = installed;

        public void Dispose()
        {
            ISecretCipherServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class MissingSecretCipherServices : ISecretCipherServices
    {
        public string Resolve(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                return string.Empty;
            if (IsEnvironmentReference(serialized, out string environmentVariableName))
                return Environment.GetEnvironmentVariable(environmentVariableName) ?? string.Empty;
            if (IsLegacyPlaintext(serialized))
                return serialized;

            throw MissingRegistration("resolve", serialized);
        }

        public string Protect(string? plaintext)
            => string.IsNullOrEmpty(plaintext)
                ? string.Empty
                : throw MissingRegistration("protect", null);

        public string ReferenceEnvironmentVariable(string environmentVariableName)
            => string.IsNullOrEmpty(environmentVariableName)
                ? string.Empty
                : EnvironmentPrefix + environmentVariableName;

        public bool IsLegacyPlaintext(string? serialized)
            => !string.IsNullOrEmpty(serialized)
                && !serialized.StartsWith(DpapiPrefix, StringComparison.Ordinal)
                && !serialized.StartsWith(EnvironmentPrefix, StringComparison.Ordinal)
                && !serialized.StartsWith(ObfuscatedPrefix, StringComparison.Ordinal);

        public bool IsEnvironmentReference(string? serialized, out string environmentVariableName)
        {
            if (!string.IsNullOrEmpty(serialized)
                && serialized.StartsWith(EnvironmentPrefix, StringComparison.Ordinal))
            {
                environmentVariableName = serialized[EnvironmentPrefix.Length..];
                return true;
            }

            environmentVariableName = string.Empty;
            return false;
        }

        private static InvalidOperationException MissingRegistration(string operation, string? serialized)
            => new(
                $"Editor secret cipher owner '{nameof(ISecretCipherServices)}' is not installed for {operation}" +
                (string.IsNullOrWhiteSpace(serialized) ? "." : $" of value kind '{GetValueKind(serialized)}'.") +
                " Install the editor secret-persistence service at the application composition root.");

        private static string GetValueKind(string serialized)
            => serialized.StartsWith(DpapiPrefix, StringComparison.Ordinal)
                ? "DPAPI"
                : serialized.StartsWith(ObfuscatedPrefix, StringComparison.Ordinal)
                    ? "obfuscated"
                    : "unknown";
    }
}
