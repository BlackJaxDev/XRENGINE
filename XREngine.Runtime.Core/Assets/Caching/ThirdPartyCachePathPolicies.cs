namespace XREngine;

/// <summary>Lease-based registry for feature-owned cache identity policies.</summary>
public static class ThirdPartyCachePathPolicies
{
    private static readonly object Sync = new();
    private static IThirdPartyCachePathPolicy[] _policies = [];

    public static IDisposable Install(IThirdPartyCachePathPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (Sync)
        {
            if (_policies.Any(candidate => ReferenceEquals(candidate, policy)))
                throw new InvalidOperationException("The cache path policy instance is already installed.");
            Volatile.Write(ref _policies, [.. _policies, policy]);
        }

        return new PolicyLease(policy);
    }

    public static IThirdPartyCachePathPolicy? Find(Type assetType)
    {
        ArgumentNullException.ThrowIfNull(assetType);
        IThirdPartyCachePathPolicy? match = null;
        foreach (IThirdPartyCachePathPolicy policy in Volatile.Read(ref _policies))
        {
            if (!policy.CanHandle(assetType))
                continue;

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple third-party cache path policies claim asset type '{assetType.FullName}': " +
                    $"'{match.GetType().FullName}' and '{policy.GetType().FullName}'.");
            }

            match = policy;
        }

        return match;
    }

    private sealed class PolicyLease(IThirdPartyCachePathPolicy policy) : IDisposable
    {
        private IThirdPartyCachePathPolicy? _policy = policy;

        public void Dispose()
        {
            IThirdPartyCachePathPolicy? current = Interlocked.Exchange(ref _policy, null);
            if (current is null)
                return;

            lock (Sync)
            {
                int index = Array.FindIndex(_policies, candidate => ReferenceEquals(candidate, current));
                if (index < 0)
                    return;

                IThirdPartyCachePathPolicy[] updated = new IThirdPartyCachePathPolicy[_policies.Length - 1];
                if (index > 0)
                    Array.Copy(_policies, 0, updated, 0, index);
                if (index < _policies.Length - 1)
                    Array.Copy(_policies, index + 1, updated, index, _policies.Length - index - 1);
                Volatile.Write(ref _policies, updated);
            }
        }
    }
}
