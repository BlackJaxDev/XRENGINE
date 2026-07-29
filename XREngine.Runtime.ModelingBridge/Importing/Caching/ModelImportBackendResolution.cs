using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable snapshot of a model backend resolution decision.
/// </summary>
public sealed class ModelImportBackendResolution
{
    private readonly ReadOnlyCollection<ModelImportBackendDescriptor> _candidates;

    internal ModelImportBackendResolution(
        uint resolverPolicyVersion,
        string sourceExtension,
        ModelImportBackendPolicy requestedPolicy,
        ModelImportBackendPolicy hostPreference,
        IEnumerable<ModelImportBackendDescriptor> candidates)
    {
        ModelImportBackendDescriptor[] candidateArray = candidates.ToArray();

        ResolverPolicyVersion = resolverPolicyVersion;
        SourceExtension = sourceExtension;
        RequestedPolicy = requestedPolicy;
        HostPreference = hostPreference;
        _candidates = Array.AsReadOnly(candidateArray);
        CandidateListHash = ComputeCandidateListHash(candidateArray);
    }

    /// <summary>
    /// Gets the output-affecting version of the resolver policy.
    /// </summary>
    public uint ResolverPolicyVersion { get; }

    /// <summary>
    /// Gets the normalized lowercase source extension, or an empty string when absent.
    /// </summary>
    public string SourceExtension { get; }

    /// <summary>
    /// Gets the policy requested by import options before applying host preference.
    /// </summary>
    public ModelImportBackendPolicy RequestedPolicy { get; }

    /// <summary>
    /// Gets the host preference consulted when the requested policy is <see cref="ModelImportBackendPolicy.Auto"/>.
    /// </summary>
    public ModelImportBackendPolicy HostPreference { get; }

    /// <summary>
    /// Gets the eligible backends in deterministic attempt order.
    /// </summary>
    public IReadOnlyList<ModelImportBackendDescriptor> Candidates => _candidates;

    /// <summary>
    /// Gets the lowercase SHA-256 hash of the ordered candidate IDs and implementation versions.
    /// </summary>
    public string CandidateListHash { get; }

    /// <summary>
    /// Returns whether another candidate follows the supplied candidate in this snapshot.
    /// </summary>
    public bool HasCandidateAfter(string stableId)
    {
        for (int i = 0; i < _candidates.Count; i++)
        {
            if (string.Equals(_candidates[i].StableId, stableId, StringComparison.Ordinal))
                return i + 1 < _candidates.Count;
        }

        return false;
    }

    private static string ComputeCandidateListHash(IReadOnlyList<ModelImportBackendDescriptor> candidates)
    {
        StringBuilder canonical = new();
        canonical.Append(candidates.Count.ToString(CultureInfo.InvariantCulture)).Append(';');

        for (int i = 0; i < candidates.Count; i++)
        {
            ModelImportBackendDescriptor candidate = candidates[i];
            canonical
                .Append(candidate.StableId.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(candidate.StableId)
                .Append('@')
                .Append(candidate.ImplementationVersion.ToString(CultureInfo.InvariantCulture))
                .Append(';');
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
