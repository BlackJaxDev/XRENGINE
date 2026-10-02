using XREngine.Data.Core;

namespace XREngine;

/// <summary>Associates an exact published object lifetime with its canonical source identity.</summary>
internal sealed record RuntimeSourceAssetOwnership(string Path, ObjectCacheOwnership Objects);
