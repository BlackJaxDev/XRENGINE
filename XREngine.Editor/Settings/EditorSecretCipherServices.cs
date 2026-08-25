using System.Security.Cryptography;
using System.Text;
using XREngine.Settings;

namespace XREngine.Editor.Settings;

/// <summary>Editor implementation of protected secret persistence using Windows DPAPI.</summary>
internal sealed class EditorSecretCipherServices : ISecretCipherServices
{
    private static readonly object WarningLock = new();
    private static bool _warnedNonWindowsFallback;

    public string Resolve(string? serialized)
    {
        if (string.IsNullOrEmpty(serialized))
            return string.Empty;
        if (IsEnvironmentReference(serialized, out string environmentVariableName))
            return Environment.GetEnvironmentVariable(environmentVariableName) ?? string.Empty;

        if (serialized.StartsWith(SecretCipherServices.DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                return string.Empty;

            try
            {
                byte[] cipher = Convert.FromBase64String(serialized[SecretCipherServices.DpapiPrefix.Length..]);
                byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                return string.Empty;
            }
        }

        if (serialized.StartsWith(SecretCipherServices.ObfuscatedPrefix, StringComparison.Ordinal))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(
                    serialized[SecretCipherServices.ObfuscatedPrefix.Length..]));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        return serialized;
    }

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            return SecretCipherServices.DpapiPrefix + Convert.ToBase64String(cipher);
        }

        WarnNonWindowsOnce();
        return SecretCipherServices.ObfuscatedPrefix + Convert.ToBase64String(plain);
    }

    public string ReferenceEnvironmentVariable(string environmentVariableName)
        => string.IsNullOrEmpty(environmentVariableName)
            ? string.Empty
            : SecretCipherServices.EnvironmentPrefix + environmentVariableName;

    public bool IsLegacyPlaintext(string? serialized)
        => !string.IsNullOrEmpty(serialized)
            && !serialized.StartsWith(SecretCipherServices.DpapiPrefix, StringComparison.Ordinal)
            && !serialized.StartsWith(SecretCipherServices.EnvironmentPrefix, StringComparison.Ordinal)
            && !serialized.StartsWith(SecretCipherServices.ObfuscatedPrefix, StringComparison.Ordinal);

    public bool IsEnvironmentReference(string? serialized, out string environmentVariableName)
    {
        if (!string.IsNullOrEmpty(serialized)
            && serialized.StartsWith(SecretCipherServices.EnvironmentPrefix, StringComparison.Ordinal))
        {
            environmentVariableName = serialized[SecretCipherServices.EnvironmentPrefix.Length..];
            return true;
        }

        environmentVariableName = string.Empty;
        return false;
    }

    private static void WarnNonWindowsOnce()
    {
        if (_warnedNonWindowsFallback)
            return;

        lock (WarningLock)
        {
            if (_warnedNonWindowsFallback)
                return;
            _warnedNonWindowsFallback = true;
        }

        Console.WriteLine(
            "[SecretCipher] DPAPI unavailable on this platform; secrets are persisted with base64 obfuscation only (not encrypted).");
    }
}
