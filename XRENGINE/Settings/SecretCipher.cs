using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Settings
{
    /// <summary>
    /// Helpers for persisting editor-preference secrets (API keys, auth tokens) without storing
    /// them as plaintext in YAML config files. On Windows the cipher uses DPAPI scoped to the
    /// current user, which means the encrypted blob is only decryptable by the same Windows
    /// account that wrote it; copying the config file to another machine or another user account
    /// will silently yield an empty string on load.
    ///
    /// Serialized format:
    /// <list type="bullet">
    /// <item><c>""</c> — empty / unset.</item>
    /// <item><c>"env:NAME"</c> — read from environment variable <c>NAME</c> at runtime; the
    /// stored YAML never contains the secret value.</item>
    /// <item><c>"dpapi:BASE64"</c> — Windows DPAPI ciphertext, base64 encoded.</item>
    /// <item>any other non-empty string — legacy plaintext from a previous version of the engine
    /// that wrote secrets directly. Accepted on load and migrated to <c>dpapi:</c> on the next
    /// save. Logged once per secret on first detection so the user knows a rewrite is happening.</item>
    /// </list>
    ///
    /// Non-Windows platforms fall back to a base64-only obfuscation (no real encryption) and log
    /// a one-time warning, so the same code paths work for tests and for the WIP Linux/macOS
    /// editor builds without crashing.
    /// </summary>
    public static class SecretCipher
    {
        public const string DpapiPrefix = "dpapi:";
        public const string EnvPrefix = "env:";
        private const string ObfuscatedPrefix = "b64:"; // non-Windows fallback marker

        private static readonly object _logLock = new();
        private static bool _warnedNonWindowsFallback;

        /// <summary>
        /// Resolves a serialized secret to its plaintext value. Empty/invalid input returns
        /// <see cref="string.Empty"/>; the caller decides whether to treat that as an error.
        /// </summary>
        public static string Resolve(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                return string.Empty;

            if (serialized.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                string envName = serialized.Substring(EnvPrefix.Length);
                return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
            }

            if (serialized.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                    return string.Empty; // DPAPI ciphertext written on another machine.

                string base64 = serialized.Substring(DpapiPrefix.Length);
                try
                {
                    byte[] cipher = Convert.FromBase64String(base64);
                    byte[] plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
                catch
                {
                    // Decryption failure: bad blob, wrong user, or DPAPI key rotation. Treat as
                    // empty so the engine keeps running; the user can re-enter the secret.
                    return string.Empty;
                }
            }

            if (serialized.StartsWith(ObfuscatedPrefix, StringComparison.Ordinal))
            {
                string base64 = serialized.Substring(ObfuscatedPrefix.Length);
                try
                {
                    byte[] bytes = Convert.FromBase64String(base64);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return string.Empty;
                }
            }

            // Legacy plaintext: accepted for migration. Caller's getter returns this as-is; the
            // next save will re-emit it encrypted.
            return serialized;
        }

        /// <summary>
        /// Serializes a plaintext secret for persistence. Empty input round-trips to
        /// <see cref="string.Empty"/>. Non-empty input is DPAPI-encrypted on Windows, or
        /// base64-obfuscated elsewhere with a one-time warning.
        /// </summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return string.Empty;

            if (OperatingSystem.IsWindows())
            {
                byte[] plain = Encoding.UTF8.GetBytes(plaintext);
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(cipher);
            }

            WarnNonWindowsOnce();
            return ObfuscatedPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        /// <summary>
        /// Returns a serialized marker that defers resolution to a process environment variable
        /// at load time. Use this when the user prefers their secret kept out of the config file
        /// entirely (CI, dedicated server, shared profile).
        /// </summary>
        public static string ReferenceEnv(string envVarName)
            => string.IsNullOrEmpty(envVarName) ? string.Empty : EnvPrefix + envVarName;

        /// <summary>
        /// Returns true if the given serialized form is a legacy plaintext secret that should be
        /// migrated to a protected form on the next save. Used by callers that want to log or
        /// surface a migration banner to the user.
        /// </summary>
        public static bool IsLegacyPlaintext(string? serialized)
        {
            if (string.IsNullOrEmpty(serialized))
                return false;
            return !serialized.StartsWith(DpapiPrefix, StringComparison.Ordinal)
                && !serialized.StartsWith(EnvPrefix, StringComparison.Ordinal)
                && !serialized.StartsWith(ObfuscatedPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true if the serialized form references a process environment variable rather
        /// than storing the secret value.
        /// </summary>
        public static bool IsEnvReference(string? serialized, out string envVarName)
        {
            if (!string.IsNullOrEmpty(serialized)
                && serialized.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                envVarName = serialized.Substring(EnvPrefix.Length);
                return true;
            }
            envVarName = string.Empty;
            return false;
        }

        private static void WarnNonWindowsOnce()
        {
            if (_warnedNonWindowsFallback)
                return;
            lock (_logLock)
            {
                if (_warnedNonWindowsFallback)
                    return;
                _warnedNonWindowsFallback = true;
            }
            // Avoid pulling Debug here; SecretCipher is a low-level helper. Console is fine.
            Console.WriteLine("[SecretCipher] DPAPI unavailable on this platform; secrets persisted with base64 obfuscation only (NOT encrypted).");
        }
    }
}
