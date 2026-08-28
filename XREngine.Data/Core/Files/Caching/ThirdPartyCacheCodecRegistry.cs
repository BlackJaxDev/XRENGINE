namespace XREngine.Core.Files.Caching;

/// <summary>Lease-based registry for feature-owned third-party cache codecs.</summary>
public static class ThirdPartyCacheCodecRegistry
{
    private static readonly object Sync = new();
    private static IThirdPartyCacheCodec[] _codecs = [];

    /// <summary>Installs a codec until the returned lease is disposed.</summary>
    public static IDisposable Install(IThirdPartyCacheCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        lock (Sync)
        {
            if (_codecs.Any(candidate => ReferenceEquals(candidate, codec)))
                throw new InvalidOperationException("The cache codec instance is already installed.");

            Volatile.Write(ref _codecs, [.. _codecs, codec]);
        }

        return new CodecLease(codec);
    }

    /// <summary>Finds the single codec that claims the requested asset type.</summary>
    public static IThirdPartyCacheCodec? Find(Type assetType)
    {
        ArgumentNullException.ThrowIfNull(assetType);

        IThirdPartyCacheCodec? match = null;
        foreach (IThirdPartyCacheCodec codec in Volatile.Read(ref _codecs))
        {
            if (codec.GetOwnership(assetType) == CacheCodecOwnership.NotHandled)
                continue;

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple third-party cache codecs claim asset type '{assetType.FullName}': " +
                    $"'{match.GetType().FullName}' and '{codec.GetType().FullName}'.");
            }

            match = codec;
        }

        return match;
    }

    /// <summary>Finds the single codec that advertises the requested stable authority role.</summary>
    public static IThirdPartyCacheCodec? FindByAuthorityRole(string authorityRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityRole);

        IThirdPartyCacheCodec? match = null;
        foreach (IThirdPartyCacheCodec codec in Volatile.Read(ref _codecs))
        {
            if (!string.Equals(codec.AuthorityRole, authorityRole, StringComparison.Ordinal))
                continue;

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple third-party cache codecs advertise authority role '{authorityRole}': " +
                    $"'{match.GetType().FullName}' and '{codec.GetType().FullName}'.");
            }

            match = codec;
        }

        return match;
    }

    private sealed class CodecLease(IThirdPartyCacheCodec codec) : IDisposable
    {
        private IThirdPartyCacheCodec? _codec = codec;

        public void Dispose()
        {
            IThirdPartyCacheCodec? current = Interlocked.Exchange(ref _codec, null);
            if (current is null)
                return;

            lock (Sync)
            {
                int index = Array.FindIndex(_codecs, candidate => ReferenceEquals(candidate, current));
                if (index < 0)
                    return;

                IThirdPartyCacheCodec[] updated = new IThirdPartyCacheCodec[_codecs.Length - 1];
                if (index > 0)
                    Array.Copy(_codecs, 0, updated, 0, index);
                if (index < _codecs.Length - 1)
                    Array.Copy(_codecs, index + 1, updated, index, _codecs.Length - index - 1);
                Volatile.Write(ref _codecs, updated);
            }
        }
    }
}
