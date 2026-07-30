using System.Security.Cryptography;
using System.Text;

namespace XREngine.Data.Core;

/// <summary>
/// Creates stable object identifiers from durable source identities.
/// </summary>
public static class PersistentObjectID
{
    /// <summary>
    /// Hashes a namespaced identity into a deterministic, non-empty GUID.
    /// </summary>
    /// <param name="identity">
    /// A stable identity including the caller's domain and all required
    /// disambiguating fields.
    /// </param>
    public static Guid FromIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var result = new Guid(digest.AsSpan(0, 16));
        return result == Guid.Empty
            ? new Guid("00000000-0000-0000-0000-000000000001")
            : result;
    }
}
