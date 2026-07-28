using System.Security.Cryptography;
using System.Text;

namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Builds stable proposal identities used by the duplicate-attempt gate.
/// </summary>
public static class SelfIterationFingerprint
{
    public static string Compute(
        string campaignId,
        SelfIterationAgentProposal proposal)
    {
        string identity = string.Join(
            "\n",
            campaignId.Trim().ToLowerInvariant(),
            proposal.TargetScenario.Trim().ToLowerInvariant(),
            proposal.IssueKey.Trim().ToLowerInvariant(),
            proposal.AttemptKey.Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
